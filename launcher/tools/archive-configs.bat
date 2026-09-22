@echo off
rem ============================================================
rem  archive-configs.bat - 把分散在本机各处的活配置归档进 Furina\configs\
rem  说明：这些是"副本"归档，应用仍在读各自的原位置。
rem  实测结论（2026-09-18）：渠道/TTS/情绪映射的活配置在 Local Storage\leveldb，
rem  不在 app-config.json（后者只有窗口位置）——两个都归档。
rem ============================================================
rem 本 bat 在 Furina\tools\ 下，项目根 = 上两级
set "FURINA=%~dp0.."
set "AI=%APPDATA%\ai.moeru.airi"

if not exist "%FURINA%\configs\airi" md "%FURINA%\configs\airi"

for %%F in (app-config.json app-options.json server-channel-config.json artistry-options.json mcp.json Preferences) do (
    copy /y "%AI%\%%F" "%FURINA%\configs\airi\" >nul 2>&1
    if errorlevel 1 (echo [WARN] %%F 归档失败） else (echo [OK] %%F)
)

robocopy "%AI%\Local Storage" "%FURINA%\configs\airi\Local Storage" /MIR /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (echo [WARN] Local Storage 归档失败） else (echo [OK] Local Storage ^(leveldb，渠道/TTS配置本体^))

echo.
echo 提示：one-api.db 由 furina.exe 每次启动时自动备份到 configs\newapi\（保留最近 10 份）
echo 未归档：IndexedDB / File System（聊天数据与缓存，体积大，重建后可再生成）
pause
