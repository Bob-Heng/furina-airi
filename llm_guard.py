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
import sys
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

CORRECTION = ('你的上一段回复超过了长度限制（%d字）。请在完全保持角色人格与语气的前提下，'
              '把同样的意思压缩到%d字以内重新输出整段回复，只输出正文，不要解释、不要分段标题。')


def _log(msg):
    line = '%s %s\n' % (time.strftime('%Y-%m-%d %H:%M:%S'), msg)
    try:
        with open(LOG_FILE, 'a', encoding='utf-8') as f:
            f.write(line)
    except Exception:
        pass


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


def _upstream_chat(messages, model, temperature):
    body = {
        'model': model,
        'messages': messages,
        'stream': False,
    }
    if temperature is not None:
        body['temperature'] = temperature
    req = urllib.request.Request(
        UPSTREAM + '/chat/completions',
        data=json.dumps(body).encode('utf-8'),
        headers={'Content-Type': 'application/json'},
        method='POST',
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        obj = json.loads(resp.read().decode('utf-8'))
    return obj['choices'][0]['message']['content']


def _repack_sse(content, model):
    """把完整正文打包成 OpenAI 流式分块。"""
    base = {'id': 'chatcmpl-furina-guard', 'object': 'chat.completion.chunk',
            'created': int(time.time()), 'model': model}
    c1 = dict(base)
    c1['choices'] = [{'index': 0, 'delta': {'role': 'assistant', 'content': content},
                      'finish_reason': None}]
    c2 = dict(base)
    c2['choices'] = [{'index': 0, 'delta': {}, 'finish_reason': 'stop'}]
    return ('data: %s\n\n' % json.dumps(c1, ensure_ascii=False)
            + 'data: %s\n\n' % json.dumps(c2, ensure_ascii=False)
            + 'data: [DONE]\n\n')


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
        """非 chat/completions 请求原样转发（/models、删除会话等）。"""
        req = urllib.request.Request(
            UPSTREAM + self.path,
            data=body if body else None,
            headers={'Content-Type': self.headers.get('Content-Type', 'application/json'),
                     'Authorization': self.headers.get('Authorization', '')},
            method=self.command,
        )
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                data = resp.read()
                ct = resp.headers.get('Content-Type', 'application/json')
                self._send(resp.status, data, ct)
        except urllib.error.HTTPError as e:
            self._send(e.code, e.read(), 'application/json')
        except Exception as e:
            _log('passthrough error: %s' % e)
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
            req = json.loads(self._read_body().decode('utf-8') or '{}')
            messages = req.get('messages') or []
            model = req.get('model', '')
            want_stream = bool(req.get('stream'))
            temperature = req.get('temperature')

            content = _upstream_chat(messages, model, temperature)
            n = _char_count(content)
            _log('chat %d chars (limit %d) model=%s' % (n, MAX_CHARS, model))

            if n > MAX_CHARS:
                _log('over limit -> regenerate with correction')
                fix = messages + [
                    {'role': 'assistant', 'content': content},
                    {'role': 'user', 'content': CORRECTION % (MAX_CHARS, MAX_CHARS)},
                ]
                content = _upstream_chat(fix, model, temperature)
                n2 = _char_count(content)
                _log('regenerated: %d chars' % n2)
                if n2 > MAX_CHARS:
                    content = _trim_to_limit(content, MAX_CHARS)
                    _log('still over limit, trimmed to %d chars' % _char_count(content))

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
