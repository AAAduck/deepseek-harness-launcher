@echo off
chcp 65001 >nul
echo 即将把启动器更新到修复版（2026-09-30 审查修复轮）。
echo.
echo 注意：会先关闭正在运行的启动器，当前对话会中断一次；
echo 更新完自动重启启动器，对话会自动接续。
echo.
pause
taskkill /im DeepSeekHarness.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
copy /y "%LOCALAPPDATA%\DeepSeekHarness\update-staging\DeepSeekHarness.exe" "D:\桌面\DeepSeekHarness\bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
if errorlevel 1 (
  echo 复制到 bin\Release 失败，请确认启动器已关闭后重试。
  pause
  exit /b 1
)
copy /y "%LOCALAPPDATA%\DeepSeekHarness\update-staging\DeepSeekHarness.exe" "D:\桌面\DeepSeekHarness\DeepSeekHarness.exe" >nul 2>&1
echo 更新完成，正在启动新版本...
start "" "D:\桌面\DeepSeekHarness\bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"