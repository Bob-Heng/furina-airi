@echo off
chcp 65001 >nul
setlocal

rem ROOT = 本 bat 所在目录（项目根），项目可整体搬迁，无需改路径
set "ROOT=%~dp0"
set "PY=%ROOT%\sovits-venv\Scripts\python.exe"
set "GS_DIR=%ROOT%\GPT-SoVITS-main"

rem === GPT-SoVITS main server, port 9880 ===
rem Probe first: any HTTP response means alive, timeout means dead.
curl -s -m 5 -o nul http://127.0.0.1:9880/ 2>nul
if errorlevel 1 (
    for /f "tokens=5" %%P in ('netstat -ano ^| findstr /C:":9880" ^| findstr /C:"LISTENING"') do taskkill /F /PID %%P >nul 2>&1
    cd /d "%GS_DIR%"
    set "PYTHONPATH=%GS_DIR%;%GS_DIR%\GPT_SoVITS"
    rem /b = 不新建控制台：从隐藏/无窗口的父进程启动时，/min 的窗口会显示出来（踩过的坑），
    rem /b 让 python 直接共享父控制台（父控制台隐藏时完全无窗口），杀进程也直接命中 python
    start "" /b cmd /c ""%PY%" api_v2.py -c "GPT_SoVITS\configs\tts_infer_furina.yaml" -a 127.0.0.1 -p 9880 >> "%ROOT%\语音\tts_api.log" 2>&1"
)

rem === OpenAI-compatible adapter, port 9881 ===
curl -s -m 5 -o nul http://127.0.0.1:9881/health 2>nul
if errorlevel 1 (
    for /f "tokens=5" %%P in ('netstat -ano ^| findstr /C:":9881" ^| findstr /C:"LISTENING"') do taskkill /F /PID %%P >nul 2>&1
    rem NOTE: adapter writes adapter.log by itself. Never redirect stdout to adapter.log here,
    rem or cmd's exclusive handle makes every adapter log write fail with Permission denied.
    start "" /b cmd /c ""%PY%" "%ROOT%\语音\openai_tts_adapter.py" > nul 2>&1"
)

endlocal
