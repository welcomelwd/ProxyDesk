@echo off
setlocal
cd /d "%~dp0"
start "ProxyDesk development service" /min "ProxyDesk.Service.exe" --console --data-dir "%LOCALAPPDATA%\ProxyDesk\Preview"
start "ProxyDesk" "ProxyDesk.Desktop.exe"
