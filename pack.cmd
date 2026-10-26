@echo off
chcp 65001 >nul
rem 编译并打绿色版 zip（dist\ADHD-lite-v0.11.1-win64.zip）
cd /d "%~dp0"

call build.cmd < nul
if errorlevel 1 (
  echo.
  echo 打包取消：编译失败。
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\pack.ps1" -Version "v0.11.1"
if errorlevel 1 (
  echo.
  echo 打包失败，请看上面的错误。
  pause
  exit /b 1
)

echo.
echo 绿色版已生成：dist\ADHD-lite-v0.11.1-win64.zip
pause
exit /b 0