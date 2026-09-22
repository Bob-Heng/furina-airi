# -*- coding: utf-8 -*-
"""LLM 守卫代理：在响应到达 AIRI 之前检查输出长度，超限自动打回上游压缩重生成。

位置：AIRI → 本代理(:3001) → NewAPI/DeepSeek。AIRI 只需把 API 地址指到本代理，
检查环节完全前置——超长的回复在显示之前就被拦截、压缩、替换，用户无感知。

- 仅对 POST /v1/chat/completions 做长度守卫；其余请求原样透传。
- 上游一律按 stream=false 取完整响应（便于统计与替换）；客户端若要流式，
  本代理把最终正文重新打包成 SSE 发送。
- 纯标准库，无依赖。日志写在同目录 llm_guard.log（由启动器每次启动清空）。

用法：python llm_guard.py --port 3001 --upstream http://127.0.0.1:3000/v1 --max-chars 1000
"""
import json
import os
import re
import sys
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_FILE = os.path.join(BASE_DIR, 'llm_guard.log')


def _arg(name, default):
    if name in sys.argv:
        i = sys.argv.index(name)
        if i + 1 < len(sys.argv):
            return sys.argv[i + 1]
    return default


PORT = int(_arg('--port', '3001'))
UPSTREAM = _arg('--upstream', 'http://127.0.0.1:3000/v1').rstrip('/')
MAX_CHARS = int(_arg('--max-chars', '1000'))
# 语音适配器地址：守卫拿到完整回复后按句预合成入缓存，AIRI 的逐句请求将命中缓存
ADAPTER = _arg('--adapter', 'http://127.0.0.1:9881').rstrip('/')
# 视觉路由：含图片的请求改走视觉模型（在 NewAPI 里另建视觉渠道），纯文本仍走原模型。
# 留空 = 未配置视觉模型：守卫剥除图片后按纯文本继续（ she 看不到图但至少会回复）。
VISION_MODEL = _arg('--vision-model', '')
VISION_UPSTREAM = _arg('--vision-upstream', UPSTREAM).rstrip('/')

CORRECTION = ('你的上一段回复超过了长度限制（%d字）。请在完全保持角色人格与语气的前提下，'
              '把同样的意思压缩到%d字以内重新输出整段回复，只输出正文，不要解释、不要分段标题。')

# ---------------- ACT 情绪分段：LLM 自报情绪 → 语音适配器 ----------------
# 角色卡的 ACT token（如 <|ACT emotion="happy"|>）标记其后正文段的情绪，
# AIRI 用它驱动动作；这里把"段落-情绪"对应关系写下来，适配器按句查表选参考音，
# 实现语音与动作的情绪同源。段边界 = ACT token 出现的位置（与原文顺序一致）。
_STAGE_RE = re.compile(r'<\|.*?\|>', re.DOTALL)
_ACT_RE = re.compile(r'<\|\s*ACT\b', re.IGNORECASE)
# AIRI 实际格式为 <|ACT {"emotion":{"name":"happy","intensity":0.8}}|>（JSON），
# 兼容旧式 emotion="happy" 写法
_EMOTION_RE = re.compile(r'"emotion"\s*:\s*\{[^}]*?"name"\s*:\s*"([A-Za-z_]+)"', re.IGNORECASE)
_EMOTION_RE_FALLBACK = re.compile(r'emotion\s*=\s*"?([A-Za-z_]+)"?', re.IGNORECASE)

# AIRI 动作系统的 emotion 名 → 适配器参考音池（soft/calm/bright）
_EMOTION_POOL = {
    'soft': {'sad', 'tearful', 'gloomy', 'depressed', 'lonely', 'cry', 'upset',
             'melancholy', 'tired', 'sleepy', 'down', 'hurt', 'anxious'},
    'bright': {'happy', 'joyful', 'excited', 'proud', 'delighted', 'cheerful',
               'playful', 'amused', 'laughing', 'smug', 'eager', 'thrilled'},
}


def _pool_of(emotion):
    e = (emotion or '').lower()
    for pool, names in _EMOTION_POOL.items():
        if e in names:
            return pool
    return 'calm'


MOOD_MAP_FILE = os.path.join(BASE_DIR, 'voice', 'act_mood_map.json')


def _parse_act_segments(content):
    """剥离全部舞台指令得正文；按 ACT 分界得到 [{mood, text}] 段（原文顺序）。

    ACT 之间的正文累积进当前情绪段；DELAY/CALL 等非 ACT 指令只从正文剥离，
    不切断情绪段（其前后文本同属一种情绪）。
    """
    segments = []
    plain_parts = []
    cur_emotion = None
    pending = ''
    pos = 0
    for m in _STAGE_RE.finditer(content):
        seg = content[pos:m.start()]
        if seg.strip():
            pending += seg
            plain_parts.append(seg)
        if _ACT_RE.match(m.group(0)):
            if pending.strip():
                segments.append({'mood': _pool_of(cur_emotion), 'text': pending.strip()})
                pending = ''
            em = _EMOTION_RE.search(m.group(0)) or _EMOTION_RE_FALLBACK.search(m.group(0))
            cur_emotion = em.group(1) if em else None
        pos = m.end()
    tail = content[pos:]
    if tail.strip():
        pending += tail
        plain_parts.append(tail)
    if pending.strip():
        segments.append({'mood': _pool_of(cur_emotion), 'text': pending.strip()})
    return ''.join(plain_parts), segments


def _write_mood_map(segments):
    try:
        tmp = MOOD_MAP_FILE + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump({'updated': time.strftime('%Y-%m-%d %H:%M:%S'),
                       'segments': segments[-40:]},  # 只留最近 40 段，防膨胀
                      f, ensure_ascii=False)
        os.replace(tmp, MOOD_MAP_FILE)
    except Exception as e:
        _log('mood map write failed: %s' % e)


def _log(msg):
    line = '%s %s\n' % (time.strftime('%Y-%m-%d %H:%M:%S'), msg)
    try:
        with open(LOG_FILE, 'a', encoding='utf-8') as f:
            f.write(line)
    except Exception:
        pass


def _has_image(messages):
    """消息里是否含图片（OpenAI 多模态 content 数组）。"""
    for m in messages or []:
        c = m.get('content')
        if isinstance(c, list):
            for part in c:
                if isinstance(part, dict) and part.get('type') == 'image_url':
                    return True
    return False


def _downscale_images(messages, max_px=1024, quality=80):
    """压缩内嵌 base64 图片（防 413、省 token）。PIL 不可用时原样返回。

    http(s) 外链图片不处理。非图片内容与纯文本消息原样保留。
    """
    try:
        import base64
        import io
        from PIL import Image
    except Exception:
        return messages

    def shrink(url):
        try:
            head, _, b64 = url.partition(',')
            if 'base64' not in head:
                return url
            img = Image.open(io.BytesIO(base64.b64decode(b64)))
            img.thumbnail((max_px, max_px))
            if img.mode != 'RGB':
                img = img.convert('RGB')
            buf = io.BytesIO()
            img.save(buf, 'JPEG', quality=quality)
            return 'data:image/jpeg;base64,%s' % base64.b64encode(buf.getvalue()).decode()
        except Exception:
            return url

    out = []
    for m in messages or []:
        c = m.get('content')
        if isinstance(c, list):
            c = [dict(p, image_url={'url': shrink(p['image_url']['url'])})
                 if isinstance(p, dict) and p.get('type') == 'image_url'
                 and isinstance(p.get('image_url'), dict) else p for p in c]
            m = dict(m, content=c)
        out.append(m)
    return out


def _char_count(s):
    # 按"字"计数：忽略所有空白字符
    return len(''.join((s or '').split()))


def _trim_to_limit(s, limit):
    """按句读边界截断到 limit 字以内，截断处补省略号。"""
    s = s.strip()
    if _char_count(s) <= limit:
        return s
    cut = 0
    n = 0
    for i, ch in enumerate(s):
        if not ch.isspace():
            n += 1
        if ch in '。！？!?；;':
            cut = i + 1
        if n >= limit:
            break
    body = s[:cut] if cut > 0 else s[:i]
    return body.rstrip() + '……'


def _upstream_chat(messages, model, temperature, auth='', upstream=None):
    body = {
        'model': model,
        'messages': messages,
        'stream': False,
    }
    if temperature is not None:
        body['temperature'] = temperature
    headers = {'Content-Type': 'application/json'}
    if auth:
        headers['Authorization'] = auth
    req = urllib.request.Request(
        (upstream or UPSTREAM) + '/chat/completions',
        # 必须 ensure_ascii=False：中文转义会让体积膨胀约 2 倍，
        # 带 base64 图片的请求会被上游 413 拒绝
        data=json.dumps(body, ensure_ascii=False).encode('utf-8'),
        headers=headers,
        method='POST',
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        obj = json.loads(resp.read().decode('utf-8'))
    return obj['choices'][0]['message']['content']


def _repack_sse(content, model):
    """把完整正文按句拆成多个 SSE 增量分块，恢复逐句流式节奏。

    分块按句尾标点（。！？!?；;）切；ACT 指令不含此类标点，天然留在各自句块内，
    AIRI 的动作解析不受影响。
    """
    base = {'id': 'chatcmpl-furina-guard', 'object': 'chat.completion.chunk',
            'created': int(time.time()), 'model': model}
    out = []
    for i, chunk in enumerate(_split_sentences(content)):
        c = dict(base)
        c['choices'] = [{'index': 0, 'delta': {'role': 'assistant', 'content': chunk},
                         'finish_reason': None}]
        out.append('data: %s\n\n' % json.dumps(c, ensure_ascii=False))
    c = dict(base)
    c['choices'] = [{'index': 0, 'delta': {}, 'finish_reason': 'stop'}]
    out.append('data: %s\n\n' % json.dumps(c, ensure_ascii=False))
    out.append('data: [DONE]\n\n')
    return ''.join(out)


# 断句粒度对齐 AIRI 实测的断句表（。！？!?，、），不在分号/省略号/破折号处拆：
# 分号长句由适配器内部二次拆分合成，缓存键保持 AIRI 粒度，预合成才能命中
_SENT_SPLIT = re.compile(r'[^。！？!?，、\n]*[。！？!?，、]')


def _split_sentences(text):
    """按句尾标点断句（保留标点），末段无标点则作为最后一句。"""
    parts = _SENT_SPLIT.findall(text)
    tail = _SENT_SPLIT.sub('', text).strip()
    if tail:
        parts.append(tail)
    return [p for p in parts if p.strip()]


def _prefetch_voice(plain):
    """把剥离 ACT 的正文按句推给适配器预合成（后台线程，不阻塞 SSE 返回）。

    AIRI 随后逐句请求时，适配器直接命中缓存秒回——语音时延被藏进
    "守卫处理 + SSE 推送"的间隙里，播放顺序由 AIRI 的请求顺序天然保证。
    """
    sentences = _split_sentences(plain)
    if not sentences:
        return

    def worker():
        for s in sentences:
            try:
                body = json.dumps({'input': s}).encode('utf-8')
                req = urllib.request.Request(
                    ADAPTER + '/warm', data=body,
                    headers={'Content-Type': 'application/json'}, method='POST')
                urllib.request.urlopen(req, timeout=120).read()
            except Exception as e:
                _log('warm failed for %r: %s' % (s[:20], e))

    threading.Thread(target=worker, daemon=True).start()


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, body_bytes, content_type):
        self.send_response(code)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(body_bytes)))
        self.send_header('Access-Control-Allow-Origin', '*')
        self.end_headers()
        self.wfile.write(body_bytes)

    def _send_json(self, code, obj):
        self._send(code, json.dumps(obj, ensure_ascii=False).encode('utf-8'),
                   'application/json')

    def _read_body(self):
        n = int(self.headers.get('Content-Length', 0))
        return self.rfile.read(n) if n > 0 else b''

    def _passthrough(self, body):
        """非 chat/completions 请求原样转发（/models、删除会话等）。

        入站路径自带 /v1 前缀，而 UPSTREAM 已含 /v1——拼接前去重，否则会变成
        /v1/v1/models（AIRI 的 listModels 验证因此 404）。"""
        path = self.path
        if path == '/v1' or path.startswith('/v1/'):
            path = path[len('/v1'):] or '/'
        req = urllib.request.Request(
            UPSTREAM + path,
            data=body if body else None,
            headers={'Content-Type': self.headers.get('Content-Type', 'application/json'),
                     'Authorization': self.headers.get('Authorization', '')},
            method=self.command,
        )
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                data = resp.read()
                ct = resp.headers.get('Content-Type', 'application/json')
                _log('passthrough %s %s -> %d' % (self.command, self.path, resp.status))
                self._send(resp.status, data, ct)
        except urllib.error.HTTPError as e:
            _log('passthrough %s %s -> HTTP %d' % (self.command, self.path, e.code))
            self._send(e.code, e.read(), 'application/json')
        except Exception as e:
            _log('passthrough %s %s ERROR: %s' % (self.command, self.path, e))
            self._send_json(502, {'error': 'upstream failed: %s' % e})

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type, Authorization')
        self.send_header('Content-Length', '0')
        self.end_headers()

    def do_GET(self):
        if self.path in ('/', '/health'):
            self._send_json(200, {'status': 'ok', 'upstream': UPSTREAM, 'max_chars': MAX_CHARS})
            return
        self._passthrough(b'')

    do_DELETE = do_GET

    def do_POST(self):
        if self.path != '/v1/chat/completions':
            self._passthrough(self._read_body())
            return
        try:
            raw = self._read_body()
            try:
                req = json.loads(raw.decode('utf-8'))
            except UnicodeDecodeError:
                req = json.loads(raw.decode('gbk', errors='replace'))
            messages = req.get('messages') or []
            model = req.get('model', '')
            want_stream = bool(req.get('stream'))
            temperature = req.get('temperature')
            auth = self.headers.get('Authorization', '')

            # 图片请求：默认原样走原上游（该上游支持视觉）；配置了视觉模型时
            # 改走视觉渠道并压缩内嵌图片。绝不剥除图片——剥除等于替她拒绝看图。
            target_upstream = UPSTREAM
            if _has_image(messages):
                if VISION_MODEL:
                    target_upstream = VISION_UPSTREAM
                    model = VISION_MODEL
                    _log('vision route: -> %s' % VISION_MODEL)
                messages = _downscale_images(messages)

            content = _upstream_chat(messages, model, temperature, auth,
                                     upstream=target_upstream)
            # 字数上限只算"最终显示给用户的正文"：ACT/DELAY 等舞台指令不计入
            plain, segments = _parse_act_segments(content)
            n = _char_count(plain)
            _log('chat %d chars (limit %d) model=%s act=%d' % (
                n, MAX_CHARS, model, len(segments)))

            if n > MAX_CHARS:
                _log('over limit -> regenerate with correction')
                fix = messages + [
                    {'role': 'assistant', 'content': content},
                    {'role': 'user', 'content': CORRECTION % (MAX_CHARS, MAX_CHARS)},
                ]
                content = _upstream_chat(fix, model, temperature, auth,
                                         upstream=target_upstream)
                plain, segments = _parse_act_segments(content)
                n2 = _char_count(plain)
                _log('regenerated: %d chars' % n2)
                if n2 > MAX_CHARS:
                    # 二次超限：截断正文但保留舞台指令格式由模型负责，这里截 plain
                    content = _trim_to_limit(plain, MAX_CHARS)
                    segments = [{'mood': 'calm', 'text': content}]
                    _log('still over limit, trimmed to %d chars' % _char_count(content))

            # 把最终版本的 ACT 情绪分段写给语音适配器（含打回重生成的版本）
            _write_mood_map(segments)
            # 预合成：AIRI 随后逐句请求时命中缓存，语音零等待
            _prefetch_voice(plain)

            if want_stream:
                self._send(200, _repack_sse(content, model).encode('utf-8'),
                           'text/event-stream')
            else:
                self._send_json(200, {
                    'id': 'chatcmpl-furina-guard',
                    'object': 'chat.completion',
                    'created': int(time.time()),
                    'model': model,
                    'choices': [{'index': 0, 'message': {'role': 'assistant', 'content': content},
                                 'finish_reason': 'stop'}],
                    'usage': {},
                })
        except Exception as e:
            _log('ERROR: %s' % e)
            self._send_json(502, {'error': 'llm guard failed: %s' % e})


if __name__ == '__main__':
    _log('llm guard started on 127.0.0.1:%d -> %s (max %d chars)' % (PORT, UPSTREAM, MAX_CHARS))
    print('LLM guard on http://127.0.0.1:%d (upstream %s, max %d chars)' % (PORT, UPSTREAM, MAX_CHARS))
    ThreadingHTTPServer(('127.0.0.1', PORT), H).serve_forever()
