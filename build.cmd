@echo off
chcp 65001 >nul
rem 重新编译 ADHD-lite.exe（只用系统自带的 .NET Framework 编译器，不需要装 SDK）
rem 注意：下面不要用 if (...) else (...) 包中文，cmd 在 65001 代码页下会切坏多字节字符
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "GAC=%WINDIR%\Microsoft.NET\assembly"

if exist "ADHD-lite.exe" (
  taskkill /f /im ADHD-lite.exe >nul 2>nul
  ping -n 2 127.0.0.1 >nul
)

"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:ADHD-lite.exe ^
 /r:"%GAC%\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll" ^
 /r:"%GAC%\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll" ^
 /r:"%GAC%\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll" ^
 /r:"%GAC%\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll" ^
 src\Program.cs src\Ui.cs src\MainWindow.cs src\Ai.cs src\Habit.cs src\PushPlan.cs src\Deco.cs src\Assist.cs src\MobileServer.cs

if errorlevel 1 goto failed
echo.
echo 编译成功: ADHD-lite.exe
goto done

:failed
echo.
echo 编译失败，请看上面的错误信息。
pause
exit /b 1

:done
pause
exit /b 0
