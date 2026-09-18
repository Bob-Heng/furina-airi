@echo off
rem ============================================================
rem  锁定网关为仅本机访问.bat - 阻止局域网访问 NewAPI(3000端口)
rem  背景: new-api 无绑定地址参数, 实际监听 0.0.0.0:3000(全网卡)。
rem  Windows 防火墙不过滤回环流量, 本加规则只拦外部入站,
rem  本机 AIRI/启动器/守卫代理访问 127.0.0.1:3000 不受影响。
rem  需要【以管理员身份运行】(右键 → 以管理员身份运行)。
rem ============================================================
net session >nul 2>&1
if errorlevel 1 (
    echo [失败] 需要管理员权限：请右键本文件 → 以管理员身份运行
    pause
    exit /b 1
)
netsh advfirewall firewall delete rule name="furina-newapi-localonly" >nul 2>&1
netsh advfirewall firewall add rule name="furina-newapi-localonly" dir=in action=block protocol=TCP localport=3000
if errorlevel 1 (
    echo [失败] 规则添加失败
) else (
    echo [OK] 已阻断 3000 端口的所有外部入站连接，仅本机可访问
    echo      如需撤销，运行「撤销网关仅本机.bat」
)
pause
