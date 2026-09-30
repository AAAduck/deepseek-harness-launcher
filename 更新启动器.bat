@echo off
chcp 65001 >nul
setlocal
rem 用脚本所在目录定位，不硬编码绝对路径——挪目录/拷给别人都能用。
set "ROOT=%~dp0"
set "STAGED=%LOCALAPPDATA%\DeepSeekHarness\update-staging\DeepSeekHarness.exe"

rem ── 本脚本的机制（1.3.0 起，本机实测验证）─────────────────────────────────
rem 引擎的 stdout/stderr 已从管道改为 engine-stdio.log 文件（启动器 tail 文件捕获
rem 认证链接与进度），引擎的生死与启动器解耦。所以：
rem   ① taskkill /f 只按映像名杀 DeepSeekHarness.exe（绝不碰 node.exe）——
rem      引擎与 3080 上的 Web 会话原样存活；
rem   ② 新实例起来后探到 web-url.txt 的链接仍可用，直接复用引擎、打开浏览器，
rem      全程无感（实测：模拟启动器强杀后引擎持续存活写日志）；
rem   ③ 例外：从管道耦合的旧版跨进 1.3.0 的【首次】更新，旧引擎仍会随断管退出，
rem      新实例自动走完整重启（十几秒，会话历史在盘上不丢）。这一次之后皆无感。
echo 即将把启动器更新到 1.3.0（无感更新：引擎 stdio 文件化，不再随启动器陪葬）。
echo Web 会话不会中断（首次从旧版迁移除外，那会重启一次引擎、历史不丢）。
echo.

if not exist "%STAGED%" (
  echo 找不到待更新的启动器：
  echo   %STAGED%
  echo 请先把新版本 exe 放进 update-staging\ 目录，再运行本脚本。
  echo （引擎没被动过，当前一切照旧。）
  pause
  exit /b 1
)

echo 按任意键开始更新（浏览器里的会话不用关）...
pause >nul
taskkill /im DeepSeekHarness.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
copy /y "%STAGED%" "%ROOT%bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
if errorlevel 1 (
  echo 复制到 bin\Release 失败：目标 exe 可能仍被占用。
  echo 旧启动器可能没被杀干净——确认 DeepSeekHarness.exe 已退出后重跑脚本。
  echo （引擎不受影响，会话不会因此中断。）
  pause
  exit /b 1
)
copy /y "%STAGED%" "%ROOT%DeepSeekHarness.exe" >nul 2>&1
if errorlevel 1 echo （提示：脚本旁边的桌面副本没更新成功，不影响本次启动，可之后手动复制。）
echo 更新完成，正在启动新版本（会自动接上还在跑的引擎）...
start "" "%ROOT%bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
