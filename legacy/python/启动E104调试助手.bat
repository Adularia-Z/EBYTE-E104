@echo off
rem E104-BT53 蓝牙模块调试助手 启动器
cd /d "%~dp0"
where pythonw >nul 2>nul
if %errorlevel%==0 (
    start "" pythonw app_main.pyw
) else (
    start "" python app_main.pyw
)
