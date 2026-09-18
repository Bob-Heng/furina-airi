@echo off
rem 撤销「锁定网关为仅本机访问.bat」添加的防火墙规则（同样需管理员运行）
chcp 65001 >nul
net session >nul 2>&1
if errorlevel 1 (
    echo [失败] 需要管理员权限：请右键本文件 → 以管理员身份运行
    pause
    exit /b 1
)
netsh advfirewall firewall delete rule name="furina-newapi-localonly"
if errorlevel 1 (echo [提示] 规则不存在或已删除) else (echo [OK] 已撤销)
pause
