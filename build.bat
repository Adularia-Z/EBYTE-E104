@echo off
rem ============================================================
rem  智家伴蓝牙串口助手 — 一键编译 (仓库根目录版)
rem  使用 Windows 系统 .NET Framework 4.8 自带 csc 编译器,
rem  无需安装 .NET SDK 或 NuGet 包,任何 Win10/11 可直接构建。
rem
rem  用法:  双击本文件,或在命令行执行  build.bat
rem ============================================================
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [错误] 未找到系统 csc 编译器,请确认已安装 .NET Framework 4.x
    pause
    exit /b 1
)

set SRC=src\program.cs src\ui_theme.cs src\serial_io.cs src\multi_device.cs src\main_form.cs src\pages_extra.cs src\pages_multi.cs
set REFS=/r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll

echo [1/3] 编译主程序 => 智家伴蓝牙串口助手.exe
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:"智家伴蓝牙串口助手.exe" %SRC% %REFS%
if errorlevel 1 goto :fail

echo [2/3] 编译协议自检 => TestProto.exe
"%CSC%" /nologo /target:exe /codepage:65001 /out:"TestProto.exe" src\test_proto.cs src\serial_io.cs src\multi_device.cs /r:System.dll
if errorlevel 1 goto :fail

echo [3/3] 编译多机逻辑自检 => TestMulti.exe
"%CSC%" /nologo /target:exe /codepage:65001 /out:"TestMulti.exe" src\test_multi.cs src\multi_device.cs src\serial_io.cs /r:System.dll
if errorlevel 1 goto :fail

echo.
echo ============================================
echo  构建完成:
echo    智家伴蓝牙串口助手.exe   主程序
echo    TestProto.exe            AT 协议自检(需模组)
echo    TestMulti.exe            多机分配逻辑自检
echo ============================================
pause
exit /b 0

:fail
echo.
echo [错误] 编译失败,请检查上方输出。
pause
exit /b 1
