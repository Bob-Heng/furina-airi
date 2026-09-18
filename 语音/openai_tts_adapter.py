# -*- coding: utf-8 -*-
"""OpenAI 兼容 TTS 适配器：把 AIRI 的 /v1/audio/speech 请求翻译成 GPT-SoVITS api_v2 的 /tts。
仅监听 127.0.0.1:9881，仅依赖标准库。

v3：
- 启动校验参考音时长（SoVITS 要求 3~10 秒），越界直接移出轮换池；
- 上游合成失败（如 400）时把该参考音拉入进程级黑名单，并用已知可用的兜底参考音重试一次，
  彻底消除「某条参考音损坏 → 粘性轮换复用 → 整段回复无声」的故障模式；
- 日志双写：主日志 adapter.log 写失败时退到 %TEMP%\\airi_tts_adapter_fallback.log 并记录原因，
  启动时记录 __file__ / BASE_DIR / cwd 诊断信息；
- 合成音频尾部补 0.28s、头部补 0.10s 静默，避免 AIRI 连播时句子之间没有停顿；
- 清理 AIRI 断句残留的 '，？' 一类边缘标点。

v2：
- 参考音按文本情绪分组轮换（soft/calm/bright），默认 calm；
- 粘性策略：同一轮对话（15 秒内）且情绪未明确变化时，沿用上一参考音；
- 严格跳变：soft 与 bright 互为情绪两极，跳变需要更强证据，否则钳制为 calm；
- 文本预处理：剥离 <|...|> 舞台指令残留；纯标点/空文本返回 0.3 秒静默而非噪声；
- 日志记录文本预览、情绪判定、参考音与产出时长，便于排查跳句。
"""
import json
import os
import re
import tempfile
import threading
import time
import urllib.error
import urllib.request
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
SOVITS_URL = 'http://127.0.0.1:9880/tts'
TEXT_LANG = 'zh'
# 断句方式：cut3 = 只按中文句号切（cut5 默认会按所有标点切，长句听感稀碎）
TEXT_SPLIT_METHOD = 'cut3'
# 同一轮对话的判定窗口（秒）：窗口内情绪未变则沿用同一参考音，避免一句一个腔调
BURST_WINDOW = 15.0
# soft 与 bright 互为两极，互相跳变需要的最少情绪信号数
POLE_JUMP_MIN_SIGNALS = 3
# SoVITS 对参考音的时长要求（秒）
REF_MIN_SEC, REF_MAX_SEC = 3.0, 10.0
# 句间节奏：给每段合成音频补头/尾静默（秒），防止 AIRI 连播时句子和句子粘在一起
PAD_HEAD_SEC, PAD_TAIL_SEC = 0.10, 0.28

# 情绪信号词表（按字符匹配，宁缺毋滥）
BRIGHT_WORDS = ('哇', '耶', '哈哈', '太棒', '太好', '好耶', '超', '最棒', '起飞')
SOFT_WORDS = ('唉', '晚安', '累', '困', '难过', '伤心', '孤单', '眼泪', '哭',
              '遗憾', '抱歉', '对不起', '算了', '没办法', '对不起')
# 情绪分组：ref_pool.json 里的 mood → 组
MOOD_GROUP = {
    'soft': 'soft',
    'calm': 'calm', 'content': 'calm', 'musing': 'calm', 'serious': 'calm',
    'playful': 'bright', 'bright': 'bright',
    'default': 'calm',
}
# 情绪能量：两极判定用
ENERGY = {'soft': 0, 'calm': 1, 'bright': 2}

_LOG_PRIMARY = os.path.join(BASE_DIR, 'adapter.log')
_LOG_FALLBACK = os.path.join(tempfile.gettempdir(), 'airi_tts_adapter_fallback.log')


def _log(msg):
    """主日志写失败时退到 TEMP 兜底日志，并写明失败原因——绝不再静默丢失。"""
    line = '%s %s\n' % (time.strftime('%Y-%m-%d %H:%M:%S'), msg)
    try:
        with open(_LOG_PRIMARY, 'a', encoding='utf-8') as f:
            f.write(line)
    except Exception as e:
        try:
            with open(_LOG_FALLBACK, 'a', encoding='utf-8') as f:
                f.write('%s [primary log failed: %s] %s\n' % (
                    time.strftime('%Y-%m-%d %H:%M:%S'), e, msg))
        except Exception:
            pass


def _wav_duration(path):
    try:
        with wave.open(path) as w:
            return w.getnframes() / float(w.getframerate())
    except Exception:
        return -1.0


def _load_ref_pool():
    """从 ref_pool.json 读取参考音池；校验存在性与 3~10s 时长，不合格者移出并记日志。"""
    fallback = [{
        'path': r'sliced\vo_aq_vo_furina_vo_fdaq001_5_furina_01.wav_0000000000_0000192640.wav',
        'text': '富有的、贫穷的，带着酒杯或一无所有的子民们。',
        'mood': 'default',
    }]
    try:
        with open(os.path.join(BASE_DIR, 'ref_pool.json'), encoding='utf-8') as f:
            pool = json.load(f)['refs']
        good = []
        for ref in pool:
            ref['abs_path'] = os.path.join(BASE_DIR, ref['path'])
            if not os.path.exists(ref['abs_path']):
                _log('ref dropped (missing): %s' % ref['path'])
                continue
            dur = _wav_duration(ref['abs_path'])
            if not (REF_MIN_SEC <= dur <= REF_MAX_SEC):
                _log('ref dropped (duration %.2fs out of 3~10s): %s' % (dur, ref['path']))
                continue
            good.append(ref)
        if not good:
            raise ValueError('no valid ref in pool')
        return good
    except Exception as e:
        _log('ref_pool load failed, fallback single ref: %s' % e)
        for ref in fallback:
            ref['abs_path'] = os.path.join(BASE_DIR, ref['path'])
        return fallback


REF_POOL = _load_ref_pool()
# 按情绪组分类
_GROUP_REFS = {'soft': [], 'calm': [], 'bright': []}
for _ref in REF_POOL:
    _GROUP_REFS.setdefault(MOOD_GROUP.get(_ref.get('mood', 'default'), 'calm'), []).append(_ref)
if not _GROUP_REFS['calm']:  # 兜底：任何组为空都用 calm 顶
    _GROUP_REFS['calm'] = REF_POOL[:1]
for _g in ('soft', 'bright'):
    if not _GROUP_REFS[_g]:
        _GROUP_REFS[_g] = _GROUP_REFS['calm']
_GROUP_IDX = {'soft': 0, 'calm': 0, 'bright': 0}
# 进程级参考音黑名单：合成失败过的不再使用
_BAD_REFS = set()

# 合成串行锁：SoVITS 一次只能合一句，并发请求串行处理既不影响真实吞吐
# （GPU 本来就是单流），又能保证"先请求的句子先返回"，播放顺序与请求顺序一致。
# 排队中的请求在语音服务掉线恢复后按序继续（配合 _synthesize_resilient 的等待重试）。
_SYNTH_LOCK = threading.Lock()

# 粘性状态
_last_ref = None
_last_mood = 'calm'
_last_time = 0.0


def _infer_mood(text):
    """返回 (mood, bright_score)。只识别有把握的信号，拿不准就 calm。"""
    bright_score = text.count('！') + text.count('!')
    bright_score += sum(1 for w in BRIGHT_WORDS if w in text)
    soft_hit = text.startswith('……') or text.startswith('…') \
        or any(w in text for w in SOFT_WORDS)
    if bright_score >= 2 and not soft_hit:
        return 'bright', bright_score
    if soft_hit and bright_score == 0:
        return 'soft', bright_score
    return 'calm', bright_score


def _pick_ref(text):
    """情绪感知 + 粘性轮换。同一轮对话内默认沿用上一参考音；黑名单参考音跳过。"""
    global _last_ref, _last_mood, _last_time
    now = time.time()
    mood, bright_score = _infer_mood(text)

    # 严格跳变：只有「向 bright（激动/喜悦）方向」的跳变需要强证据，否则钳回 calm；
    # 向 soft 方向的跳变本身就需要明确的低落信号词才能触发，无需额外钳制。
    if mood == 'bright' and _last_mood == 'soft' and bright_score < POLE_JUMP_MIN_SIGNALS:
        mood = 'calm'

    # 超短句（≤5 字）：soft/bright 组的参考音多为气声、慵懒腔，短文本极易塌成
    # 一口呼吸声。直接改用吐字最清晰的 calm 组第一条，且不扰动粘性状态。
    if _hanzi_count(text) <= 5:
        for r in _GROUP_REFS['calm']:
            if r['abs_path'] not in _BAD_REFS:
                return r, 'calm(short)', False
        return REF_POOL[0], 'calm(short)', False

    # 粘性：对话窗口内、情绪未明确改变（calm 是中性，不强制切换），且上一条参考音未被拉黑
    if (_last_ref is not None
            and _last_ref['abs_path'] not in _BAD_REFS
            and now - _last_time < BURST_WINDOW
            and (mood == _last_mood or mood == 'calm')):
        _last_time = now
        return _last_ref, _last_mood, True

    # 切换：在该情绪组内轮换下一条（跳过黑名单）
    candidates = [r for r in _GROUP_REFS[mood] if r['abs_path'] not in _BAD_REFS]
    if not candidates:
        candidates = [r for r in REF_POOL if r['abs_path'] not in _BAD_REFS] or REF_POOL[:1]
    idx = _GROUP_IDX[mood] % len(candidates)
    _GROUP_IDX[mood] += 1
    _last_ref = candidates[idx]
    _last_mood = mood
    _last_time = now
    return _last_ref, mood, False


def _clean_text(text):
    """剥离舞台指令残留、AIRI 断句造成的边缘标点与多余空白。"""
    text = re.sub(r'<\|.*?\|>', '', text)
    text = re.sub(r'[，、；：]\s*([？！])', r'\1', text)  # '，？' → '？' 一类断句残留
    text = re.sub(r'—+\s*$', '', text)  # 句尾孤悬的破折号是断句碎片，会让模型拖出气息声
    text = re.sub(r'\s+', ' ', text).strip()
    return text


def _hanzi_count(text):
    return len(re.findall(r'[一-鿿A-Za-z0-9]', text))


def _silence_wav(seconds=0.3, rate=22050):
    """生成一小段静默 wav（纯标点/空文本时用，避免 SoVITS 吐出语气词噪声）。"""
    frames = b'\x00\x00' * int(rate * seconds)
    import io
    buf = io.BytesIO()
    with wave.open(buf, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(frames)
    return buf.getvalue()


def _pad_wav(wav_bytes, head=PAD_HEAD_SEC, tail=PAD_TAIL_SEC):
    """给合成音频补头/尾静默，让 AIRI 连播时句子之间有自然停顿。失败则原样返回。"""
    import io
    try:
        with wave.open(io.BytesIO(wav_bytes)) as w:
            params = (w.getnchannels(), w.getsampwidth(), w.getframerate())
            frames = w.readframes(w.getnframes())
        ch, sw, rate = params
        frame_bytes = ch * sw
        head_b = b'\x00' * (int(rate * head) * frame_bytes)
        tail_b = b'\x00' * (int(rate * tail) * frame_bytes)
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as w:
            w.setnchannels(ch)
            w.setsampwidth(sw)
            w.setframerate(rate)
            w.writeframes(head_b + frames + tail_b)
        return buf.getvalue()
    except Exception as e:
        _log('pad failed (passthrough): %s' % e)
        return wav_bytes


def _synthesize(text, ref):
    """调用 SoVITS 合成，返回 (wav_bytes, duration)。失败抛异常。"""
    payload = {
        'text': text,
        'text_lang': TEXT_LANG,
        'ref_audio_path': ref['abs_path'],
        'prompt_text': ref['text'],
        'prompt_lang': 'zh',
        'text_split_method': TEXT_SPLIT_METHOD,
        'media_type': 'wav',
        'streaming_mode': False,
    }
    upstream = urllib.request.Request(
        SOVITS_URL,
        data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json'},
        method='POST',
    )
    with urllib.request.urlopen(upstream, timeout=30) as resp:
        wav = resp.read()
    dur = 0.0
    try:
        import io
        with wave.open(io.BytesIO(wav)) as w:
            dur = w.getnframes() / w.getframerate()
    except Exception:
        pass
    return wav, dur


# 上游宕机恢复窗口：连接级失败（服务重启中）时原地等待重试的最长时间
UPSTREAM_WAIT_SEC = 75.0
UPSTREAM_RETRY_EVERY = 5.0


def _synthesize_resilient(text, ref):
    """带恢复的合成。

    - HTTP 4xx/5xx（参考音被 SoVITS 拒绝等）：拉黑该参考音，换兜底音重试；
    - 连接级失败（ConnectionRefused/超时等，即语音服务正在重启）：
      原地等待、按固定间隔重试，**不拉黑参考音**——掉线不是参考音的错。
      恢复后当前这句照常合成返回，AIRI 从断点继续播放。
    """
    t0 = time.time()
    while True:
        try:
            return _synthesize(text, ref)
        except urllib.error.HTTPError as e:
            _log('HTTP %s with ref %s -> blacklist & fallback' % (
                e.code, os.path.basename(ref['path'])))
            _BAD_REFS.add(ref['abs_path'])
            fb = next((r for r in _GROUP_REFS['calm']
                       if r['abs_path'] not in _BAD_REFS), None)
            if fb is None or fb['abs_path'] == ref['abs_path']:
                raise
            ref = fb
            continue
        except Exception as e:
            elapsed = time.time() - t0
            if elapsed > UPSTREAM_WAIT_SEC:
                raise
            _log('upstream unreachable (%.0fs), waiting for restart: %s' % (
                elapsed, type(e).__name__))
            time.sleep(UPSTREAM_RETRY_EVERY)


class H(BaseHTTPRequestHandler):
    def end_headers(self):
        # AIRI 渲染进程（file:// 来源）fetch 本会触发 CORS 预检，必须放行
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type, Authorization')
        super().end_headers()

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header('Content-Length', '0')
        self.end_headers()

    def _send_json(self, code, obj):
        body = json.dumps(obj).encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_wav(self, wav):
        self.send_response(200)
        self.send_header('Content-Type', 'audio/wav')
        self.send_header('Content-Length', str(len(wav)))
        self.end_headers()
        self.wfile.write(wav)

    def do_GET(self):
        if self.path in ('/', '/health'):
            self._send_json(200, {'status': 'ok'})
        elif self.path == '/v1/models':
            # AIRI 会拉模型列表并筛 id 含 "tts" 的条目
            self._send_json(200, {'object': 'list', 'data': [
                {'id': 'gpt-sovits-tts', 'object': 'model', 'created': 0, 'owned_by': 'local'},
            ]})
        else:
            self._send_json(404, {'error': 'not found'})

    def do_POST(self):
        if self.path != '/v1/audio/speech':
            self._send_json(404, {'error': 'not found'})
            return
        try:
            n = int(self.headers.get('Content-Length', 0))
            raw = self.rfile.read(n)
            try:
                req = json.loads(raw.decode('utf-8'))
            except UnicodeDecodeError:
                req = json.loads(raw.decode('gbk', errors='replace'))
            text = _clean_text(req.get('input') or '')
            if not text:
                self._send_json(400, {'error': 'empty input'})
                return
            # 长度保险丝：单句请求异常过长（正常为句片段）直接拒绝，避免拖死合成服务
            if _hanzi_count(text) > 500:
                _log('reject: input too long (%d chars) | %s...' % (
                    _hanzi_count(text), text[:30]))
                self._send_json(400, {'error': 'input too long (max 500 chars per request)'})
                return
            # 纯标点/语气残留不值得合成——SoVITS 会把它变成莫名其妙的语气词
            if not re.search(r'[一-鿿A-Za-z0-9]', text):
                _log('skip(punct-only): %r -> 0.3s silence' % text)
                self._send_wav(_silence_wav())
                return
            ref, mood, sticky = _pick_ref(text)
            t0 = time.time()
            with _SYNTH_LOCK:
                try:
                    wav, dur = _synthesize_resilient(text, ref)
                except Exception as e:
                    _log('ERROR: %s' % e)
                    self._send_json(502, {'error': f'upstream GPT-SoVITS failed: {e}'})
                    return
                # 时长合理性检查：合成结果相对字数明显偏短，多半是塌成了气声/吞字，
                # 换兜底参考音重试一次。留 0.08s 余量，避免边界值（如 2.0s<2.0s）误触发。
                min_dur = max(0.6, 0.20 * _hanzi_count(text))
                if dur < min_dur - 0.08:
                    fallback = next(
                        (r for r in _GROUP_REFS['calm']
                         if r['abs_path'] not in _BAD_REFS and r['abs_path'] != ref['abs_path']),
                        None)
                    if fallback is not None:
                        _log('audio too short (%.1fs < %.1fs) ref=%s -> retry with %s' % (
                            dur, min_dur, os.path.basename(ref['path']),
                            os.path.basename(fallback['path'])))
                        ref, sticky = fallback, False
                        wav, dur = _synthesize(text, ref)
                wav = _pad_wav(wav)
            _log('tts %d chars mood=%s%s ref=%s -> %.1fs audio, %.1fs gen | %s' % (
                len(text), mood, '(sticky)' if sticky else '',
                os.path.basename(ref['path']), dur, time.time() - t0,
                text[:40].replace('\n', ' ')))
            self._send_wav(wav)
        except Exception as e:
            _log('ERROR: %s' % e)
            self._send_json(502, {'error': f'upstream GPT-SoVITS failed: {e}'})

    def log_message(self, *a):
        pass


if __name__ == '__main__':
    _log('adapter started, %d refs (soft=%d calm=%d bright=%d) | __file__=%s BASE_DIR=%s cwd=%s' % (
        len(REF_POOL), len(_GROUP_REFS['soft']), len(_GROUP_REFS['calm']), len(_GROUP_REFS['bright']),
        os.path.abspath(__file__), BASE_DIR, os.getcwd()))
    try:
        print('OpenAI-compatible TTS adapter on http://127.0.0.1:9881 (upstream: %s)' % SOVITS_URL)
    except Exception:
        pass  # pythonw 下无 stdout
    ThreadingHTTPServer(('127.0.0.1', 9881), H).serve_forever()
