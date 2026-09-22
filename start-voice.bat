@echo off
chcp 65001 >nul
setlocal

rem ROOT = 本 bat 所在目录（项目根），项目可整体搬迁，无需改路径
set "ROOT=%~dp0"
set "PY=%ROOT%\runtime\sovits-venv\Scripts\python.exe"
set "GS_DIR=%ROOT%\runtime\GPT-SoVITS-main"

rem === GPT-SoVITS main server, port 9880 ===
rem Probe first: any HTTP response means alive, timeout means dead or busy.
curl -s -m 20 -o nul http://127.0.0.1:9880/ 2>nul
if errorlevel 1 (
    rem 无响应时分两步：有监听进程 = 正在合成（忙），绝不能误杀；无监听 = 真死了才拉起。
    rem （曾因 curl 5s 超时 + 直接 taskkill 导致把"正在合成"的健康服务杀掉，形成重拉风暴）
    netstat -ano | findstr /C:":9880" | findstr /C:"LISTENING" >nul 2>&1
    if errorlevel 1 (
        cd /d "%GS_DIR%"
        set "PYTHONPATH=%GS_DIR%;%GS_DIR%\GPT_SoVITS"
        rem /b = 不新建控制台：从隐藏/无窗口的父进程启动时，/min 的窗口会显示出来（踩过的坑），
        rem /b 让 python 直接共享父控制台（父控制台隐藏时完全无窗口），杀进程也直接命中 python
        start "" /b cmd /c ""%PY%" api_v2.py -c "GPT_SoVITS\configs\tts_infer_furina.yaml" -a 127.0.0.1 -p 9880 >> "%ROOT%\voice\tts_api.log" 2>&1"
    )
)

rem === OpenAI-compatible adapter, port 9881 ===
curl -s -m 20 -o nul http://127.0.0.1:9881/health 2>nul
if errorlevel 1 (
    rem 同上：有监听进程 = 忙（可能在等待上游恢复），勿杀；无监听才拉起
    netstat -ano | findstr /C:":9881" | findstr /C:"LISTENING" >nul 2>&1
    if errorlevel 1 (
        rem NOTE: adapter writes adapter.log by itself. Never redirect stdout to adapter.log here,
        rem or cmd's exclusive handle makes every adapter log write fail with Permission denied.
        start "" /b cmd /c ""%PY%" "%ROOT%\voice\openai_tts_adapter.py" > nul 2>&1"
    )
)

endlocal
