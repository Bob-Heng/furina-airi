# -*- coding: utf-8 -*-
"""OpenAI 兼容 TTS 适配器：把 AIRI 的 /v1/audio/speech 请求翻译成 GPT-SoVITS api_v2 的 /tts。
仅监听 127.0.0.1:9881，仅依赖标准库。

v4（范式重整：守卫预合成 + 逐句缓存命中）：
- /warm 预合成端点：守卫（llm_guard.py）拿到完整回复后按句预合成入缓存（去标点键，
  上限 64 条），AIRI 的逐句 /audio/speech 请求命中即秒回，语音时延藏进 LLM 响应间隙；
- 情绪路由：守卫解析的 ACT 情绪分段优先，信号词+粘性退化为无守卫时的兜底；
- 合成串行锁保留为廉价保险（新范式下竞争极少）。

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

# 预合成缓存：守卫（llm_guard.py）拿到完整回复后按句 /warm 预合成入缓存，
# AIRI 的逐句 /audio/speech 请求命中即秒回。key = 去标点空白后的句子文本。
_AUDIO_CACHE = {}
_CACHE_ORDER = []
_CACHE_CAP = 64


def _cache_key(text):
    return _strip_for_match(text)


def _cache_put(key, wav):
    _AUDIO_CACHE[key] = wav
    _CACHE_ORDER.append(key)
    while len(_CACHE_ORDER) > _CACHE_CAP:
        _AUDIO_CACHE.pop(_CACHE_ORDER.pop(0), None)

# 粘性状态
_last_ref = None
_last_mood = 'calm'
_last_time = 0.0


# ACT 情绪分段表：LLM 守卫（llm_guard.py）解析 ACT token 后写入，
# 实现"语音与动作情绪同源"——LLM 自报情绪优先于本地信号词猜测。
_ACT_MOOD_MAP_FILE = os.path.join(BASE_DIR, 'act_mood_map.json')


def _strip_for_match(s):
    return re.sub(r'[^0-9A-Za-z一-鿿]', '', s or '')


def _load_act_mood(text):
    """查 ACT 情绪分段表，返回输入句子所属段的情绪池（soft/calm/bright）。

    位置对应：守卫按 ACT 分界保留原文顺序的正文段；这里对输入句子做
    去标点子串匹配。表超过 10 分钟未更新视为陈旧放弃，走原信号词逻辑。
    """
    try:
        with open(_ACT_MOOD_MAP_FILE, encoding='utf-8') as f:
            data = json.load(f)
        upd = data.get('updated', '')
        if upd:
            age = time.time() - time.mktime(time.strptime(upd, '%Y-%m-%d %H:%M:%S'))
            if age > 600:
                return None
        key = _strip_for_match(text)
        if not key:
            return None
        for seg in data.get('segments', []):
            if key in _strip_for_match(seg.get('text', '')):
                return seg.get('mood')
    except Exception:
        return None
    return None


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

    # LLM 自报情绪（守卫解析的 ACT token，与动作同源）优先于信号词猜测
    act_mood = _load_act_mood(text)
    if act_mood is not None:
        mood = act_mood

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
    """剥离舞台指令残留、AIRI 断句造成的边缘标点与多余空白。

    括号舞台指示（（顿了顿，语气软下来）一类）是动作描写不是台词，整句剥除——
    否则会被朗读成怪声/哭腔气声。"""
    text = re.sub(r'<\|.*?\|>', '', text)
    text = re.sub(r'（[^（）]*）', '', text)
    text = re.sub(r'\([^()]*\)', '', text)
    text = re.sub(r'[，、；：]\s*([？！])', r'\1', text)  # '，？' → '？' 一类断句残留
    text = re.sub(r'—+\s*$', '', text)  # 句尾孤悬的破折号是断句碎片，会让模型拖出气息声
    text = re.sub(r'\s+', ' ', text).strip()
    return text


# ---------------- 文本归一化：数字与英文 → 中文读音 ----------------
# 送给 SoVITS 的每个字都是中文，保证芙宁娜音色；代价是英文为中式读法
# （中文声线模型的固有限度），听感上契合角色说外语的语境。
_CN_DIGITS = '零一二三四五六七八九'

_LETTER_READINGS = {
    'a': '诶', 'b': '毕', 'c': '西', 'd': '迪', 'e': '伊', 'f': '埃弗', 'g': '吉',
    'h': '艾尺', 'i': '爱', 'j': '杰', 'k': '凯', 'l': '埃勒', 'm': '埃姆', 'n': '恩',
    'o': '欧', 'p': '屁', 'q': '扣', 'r': '阿尔', 's': '埃斯', 't': '提', 'u': '优',
    'v': '维', 'w': '达不溜', 'x': '埃克斯', 'y': '歪', 'z': '贼',
}

_EN_DICT = {
    'ok': '欧凯', 'hp': '艾尺屁', 'mp': '埃姆屁', 'ai': '诶爱', 'cpu': '西屁优',
    'gpu': '吉屁优', 'app': '诶屁屁', 'wifi': '歪坏', 'id': '埃迪', 'ip': '埃屁',
    'url': '优阿尔艾勒', 'api': '诶屁爱', 'tts': '提提埃斯', 'gpt': '吉屁提',
    'gdp': '吉迪屁', 'fps': '埃弗屁埃斯', 'rmb': '阿尔埃姆毕', 'ssr': '埃斯埃斯阿尔',
    'ur': '优阿尔', 'pv': '屁维', 'up': '优屁', 'cv': '西维', 'cos': '扣斯',
    'www': '达不溜达不溜达不溜', 'com': '扣姆', 'org': '欧阿尔吉', 'vs': '维埃斯',
    'lv': '埃勒维', 'cd': '西迪', 'xp': '埃克斯屁', 'dl': '迪埃勒', 'afk': '诶埃弗凯',
    'mmd': '埃姆埃姆迪',
}

_NUM_RE = re.compile(r'\d+(?:\.\d+)?')
_EN_RE = re.compile(r'[A-Za-z]+')


def _four_digits(x):
    """<10000 的段转中文读法，带零压缩（1002→一千零二，1050→一千零五十）。"""
    parts = []
    zero = False
    for unit, name in ((1000, '千'), (100, '百'), (10, '十')):
        d = x // unit
        x %= unit
        if d:
            if zero and parts:
                parts.append('零')
            zero = False
            if unit == 10 and d == 1 and not parts:
                parts.append('十')  # 12→十二 而非 一十二
            else:
                parts.append(_CN_DIGITS[d] + name)
        elif parts:
            zero = True
    if x:
        if zero and parts:
            parts.append('零')
        parts.append(_CN_DIGITS[x])
    return ''.join(parts)


def _int_to_cn(n):
    if n == 0:
        return '零'
    if n < 0:
        return '负' + _int_to_cn(-n)
    if n < 10000:
        return _four_digits(n)
    if n < 10 ** 8:
        hi, lo = divmod(n, 10000)
        s = _four_digits(hi) + '万'
        if lo:
            s += ('零' if lo < 1000 else '') + _four_digits(lo)
        return s
    hi, lo = divmod(n, 10 ** 8)
    s = _four_digits(hi) + '亿'
    if lo:
        s += ('零' if lo < 10 ** 7 else '') + _int_to_cn(lo)
    return s


def _normalize_text(text):
    """阿拉伯数字→中文读法；英文单词→内置音译词典，未命中逐字母读音。"""
    def num_repl(m):
        s = m.group(0)
        if '.' in s:
            a, b = s.split('.')
            return _int_to_cn(int(a)) + '点' + ''.join(_CN_DIGITS[int(d)] for d in b)
        return _int_to_cn(int(s))

    def en_repl(m):
        w = m.group(0).lower()
        if w in _EN_DICT:
            return _EN_DICT[w]
        if len(w) == 1:
            return _LETTER_READINGS.get(w, m.group(0))
        return ''.join(_LETTER_READINGS.get(c, '') for c in w) or m.group(0)

    text = _NUM_RE.sub(num_repl, text)
    text = _EN_RE.sub(en_repl, text)
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


# 二次拆分标记：分号/省略号/破折号（AIRI 断句表不含它们，适配器内部补拆）
_SPLIT_MARKS = re.compile(r'[；;]|——|—|……|…')


def _needs_split(text):
    # >12 字且含分号/省略号/破折号时内部二次拆分（标点保留在片段尾部，语气不丢）
    return _hanzi_count(text) > 12 and bool(_SPLIT_MARKS.search(text))


def _split_long(text):
    """在分号/省略号/破折号处断成片段（标点留在片段尾部，保留语气）。"""
    out, cur = [], ''
    for m in re.finditer(r'[^；;…—]+|[；;]|——|—|……|…', text):
        cur += m.group(0)
        if _SPLIT_MARKS.fullmatch(m.group(0)):
            # 省略号/分号保留在片段尾部（拖长的语气）；破折号直接丢弃——
            # 带破折号尾部的片段合成极易塌成一口气声（实测 0.8s 塌缩）
            if not re.fullmatch(r'—{1,2}', m.group(0)):
                pass  # 已并入 cur
            else:
                cur = cur[:-len(m.group(0))]
            if cur.strip():
                out.append(cur.strip())
            cur = ''
    if cur.strip():
        out.append(cur.strip())
    return [p for p in out if re.search(r'[一-鿿A-Za-z0-9]', p)]


def _concat_wavs(wavs):
    """拼接多段 wav（同一合成管线产出，格式一致）。"""
    import io
    frames_all = b''
    params = None
    for wv in wavs:
        with wave.open(io.BytesIO(wv)) as w:
            if params is None:
                params = (w.getnchannels(), w.getsampwidth(), w.getframerate())
            frames_all += w.readframes(w.getnframes())
    buf = io.BytesIO()
    with wave.open(buf, 'wb') as w:
        w.setnchannels(params[0])
        w.setsampwidth(params[1])
        w.setframerate(params[2])
        w.writeframes(frames_all)
    return buf.getvalue()


def _produce_single_locked(text):
    """单片段合成（锁已由 _produce 持有）：选音 → 弹性合成 → 时长检查 → 补静默。"""
    ref, mood, sticky = _pick_ref(text)
    t0 = time.time()
    wav, dur = _synthesize_resilient(text, ref)
    # 时长合理性检查（重校准版）：基准按自然语速 0.12s/字（原 0.20 高于自然
    # 语速，把正常音频误判成气声）；≤5 字短句免检（本就短）；重试用同情绪组，
    # 保住 ACT 标注的语气。
    n = _hanzi_count(text)
    if n > 5:
        min_dur = max(0.6, 0.12 * n)
        if dur < min_dur - 0.08:
            grp = _GROUP_REFS.get(mood) or _GROUP_REFS['calm']
            fallback = next(
                (r for r in grp
                 if r['abs_path'] not in _BAD_REFS and r['abs_path'] != ref['abs_path']),
                None) or next(
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
    return wav


# 正式请求优先：有 AIRI 的实时请求在等待时，守卫的预热 job 不抢合成锁
_pending_real = 0
_real_cond = threading.Condition()


def _produce(text, real=True):
    """合成一句（已清洗+归一化的文本），整句持锁原子完成（拆分片段不与其他
    请求交错——否则片段间会被别的句子插队，响应被拉长数秒）。"""
    global _pending_real
    if real:
        with _real_cond:
            _pending_real += 1
    try:
        if not real:
            with _real_cond:
                while _pending_real > 0:
                    _real_cond.wait()
        with _SYNTH_LOCK:
            if _needs_split(text):
                pieces = _split_long(text)
                _log('split into %d pieces (；……——) | %s' % (len(pieces), text[:30]))
                return _concat_wavs([_produce_single_locked(p) for p in pieces])
            return _produce_single_locked(text)
    finally:
        if real:
            with _real_cond:
                _pending_real -= 1
                _real_cond.notify_all()


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
        if self.path == '/warm':
            self._handle_warm()
            return
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
                # 纯舞台指示/空文本：返回一拍静默而非报错，保持句间节奏
                _log('skip(stage-dir/empty): %r -> 0.3s silence' % (req.get('input') or '')[:30])
                self._send_wav(_silence_wav())
                return
            # 长度保险丝：单句请求异常过长（正常为句片段）直接拒绝，避免拖死合成服务
            if _hanzi_count(text) > 500:
                _log('reject: input too long (%d chars) | %s...' % (
                    _hanzi_count(text), text[:30]))
                self._send_json(400, {'error': 'input too long (max 500 chars per request)'})
                return
            # 归一化：数字/英文 → 中文读音，保证芙宁娜音色（见 _normalize_text）
            text = _normalize_text(text)
            # 纯标点/语气残留不值得合成——SoVITS 会把它变成莫名其妙的语气词
            if not re.search(r'[一-鿿A-Za-z0-9]', text):
                _log('skip(punct-only): %r -> 0.3s silence' % text)
                self._send_wav(_silence_wav())
                return
            # 预合成缓存命中即秒回（守卫已按句预合成）
            key = _cache_key(text)
            cached = _AUDIO_CACHE.pop(key, None)
            if cached is not None:
                _log('tts (cache-hit) %d chars | %s' % (
                    len(text), text[:40].replace('\n', ' ')))
                self._send_wav(cached)
                return
            wav = _produce(text, real=True)
            _cache_put(key, wav)
            self._send_wav(wav)
        except Exception as e:
            _log('ERROR: %s' % e)
            self._send_json(502, {'error': f'upstream GPT-SoVITS failed: {e}'})

    def _handle_warm(self):
        """预合成端点：守卫按句推送，合成结果入缓存；AIRI 的正式请求命中即秒回。"""
        try:
            n = int(self.headers.get('Content-Length', 0))
            req = json.loads(self.rfile.read(n).decode('utf-8'))
            text = _normalize_text(_clean_text(req.get('input') or ''))
            if not re.search(r'[一-鿿A-Za-z0-9]', text or ''):
                self._send_json(200, {'ok': True, 'skipped': True})
                return
            key = _cache_key(text)
            if key in _AUDIO_CACHE:
                self._send_json(200, {'ok': True, 'cached': True})
                return
            t0 = time.time()
            _cache_put(key, _produce(text, real=False))
            _log('warm %.1fs | %s' % (time.time() - t0, text[:30]))
            self._send_json(200, {'ok': True})
        except Exception as e:
            _log('warm ERROR: %s' % e)
            self._send_json(502, {'error': '%s' % e})

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
