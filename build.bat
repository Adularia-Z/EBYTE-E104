@echo off
rem ============================================================
rem  智家伴蓝牙串口助手 — 一键编译
rem  使用 Windows 系统 .NET Framework 4.8 自带 csc 编译器
rem  (无需安装 .NET SDK / NuGet 包,任何 Win10/11 可直接构建)
rem ============================================================
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo [错误] 未找到系统 csc 编译器,请确认 .NET Framework 4.x!
    pause
    exit /b 1
)

echo [1/4] 编译主程序 => 智家伴蓝牙串口助手.exe
%CSC% /nologo /target:winexe /codepage:65001 /optimize+ /out:"智家伴蓝牙串口助手.exe" program.cs ui_theme.cs serial_io.cs multi_device.cs main_form.cs pages_extra.cs pages_multi.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
if errorlevel 1 goto :fail

echo [2/4] 编译协议自检 => TestProto.exe (需模组)
%CSC% /nologo /target:exe /codepage:65001 /out:"TestProto.exe" test_proto.cs serial_io.cs multi_device.cs /r:System.dll
if errorlevel 1 goto :fail

echo [3/4] 编译多机逻辑自检 => TestMulti.exe (无需硬件)
%CSC% /nologo /target:exe /codepage:65001 /out:"TestMulti.exe" test_multi.cs multi_device.cs serial_io.cs /r:System.dll
if errorlevel 1 goto :fail

echo [4/4] 编译双机互传验证 => TestDuplex.exe (需 COM3+COM4 两个模组)
%CSC% /nologo /target:exe /codepage:65001 /out:"TestDuplex.exe" test_duplex.cs serial_io.cs multi_device.cs /r:System.dll
if errorlevel 1 goto :fail

echo.
echo ==================================================
echo  构建完成:
echo    智家伴蓝牙串口助手.exe   主程序
echo    TestProto.exe            AT 协议自检(单模组)
echo    TestMulti.exe            多机分配逻辑自检(无硬件)
echo    TestDuplex.exe           双机数据互传验证(双模组)
echo ==================================================
pause
exit /b 0

:fail
echo.
echo [错误] 编译失败,请检查上方输出。
pause
exit /b 1
