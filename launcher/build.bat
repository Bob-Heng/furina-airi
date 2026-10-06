@echo off
rem ============================================================
rem  build.bat - 编译 furina.exe（仅需 .NET Framework 自带 csc）
rem  源码改动后双击本文件即可重新生成 furina.exe
rem ============================================================
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [FAIL] csc.exe not found - .NET Framework missing?
    exit /b 1
)

rem 源文件为 UTF-8，/codepage:65001 告诉编译器按 UTF-8 读取
rem /target:winexe = GUI 程序（双击无控制台黑窗）；加参数运行仍可走控制台模式
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /out:furina.exe ^
    /win32manifest:furina.manifest ^
    furina.cs furina_gui.cs tutorial_text.cs ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll
if errorlevel 1 (
    echo [FAIL] build failed
    exit /b 1
)
echo [OK] %CD%\furina.exe
endlocal
