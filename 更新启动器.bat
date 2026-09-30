@echo off
chcp 65001 >nul
setlocal
rem 用脚本所在目录定位，不硬编码绝对路径——挪目录/拷给别人都能用。
set "ROOT=%~dp0"
set "STAGED=%LOCALAPPDATA%\DeepSeekHarness\update-staging\DeepSeekHarness.exe"

rem ── 本脚本的真实行为（2026-10 本机实测，别抱幻想）─────────────────────────
rem 引擎的 stdout/stderr 管道读端挂在启动器进程上。taskkill /f 杀掉启动器后，
rem 管道断裂，node 引擎在**下一次写日志时**即被 EPIPE 带崩（实测约 1 秒内
rem EXIT code=1）。所以本脚本会短暂打断 Web UI，做不到"引擎原地无感存活"。
rem 脚本的实际价值是：
rem   ① 时机由你选——在**空闲时段**手动执行，把打断成本压到零；
rem   ② 只按映像名杀 DeepSeekHarness.exe，精确、可重跑、失败分支不伤引擎；
rem   ③ 新实例起来后走自动流程：探针失败 → 停残留 → 重新拉起引擎 →
rem      新认证链接自动开浏览器；会话历史在盘上，接续不丢。
rem （真想做到更新零打断，需要把引擎 stdio 从启动器剥离成日志文件——见 README
rem   「已知限制」，那是代码层面的改造，不是脚本能解决的。）
echo 即将把启动器更新到 1.2.0（审查修复轮：PATH 覆盖 / 过期链接 / 进程缓存 / 误触恢复 等）。
echo 提醒：更新会短暂打断 Web UI（实测引擎约 1 秒内随断管退出），请确认当前没有
echo 正在跑的对话再执行；新启动器会自动重拉引擎，历史会话不丢。
echo.

if not exist "%STAGED%" (
  echo 找不到待更新的启动器：
  echo   %STAGED%
  echo 请先把新版本 exe 放进 update-staging\ 目录，再运行本脚本。
  pause
  exit /b 1
)

echo 按任意键开始更新（建议先关掉/暂停浏览器里的会话页）...
pause >nul
taskkill /im DeepSeekHarness.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
copy /y "%STAGED%" "%ROOT%bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
if errorlevel 1 (
  echo 复制到 bin\Release 失败：目标 exe 可能仍被占用。
  echo 旧启动器可能没被杀干净——确认 DeepSeekHarness.exe 已退出后重跑脚本。
  pause
  exit /b 1
)
copy /y "%STAGED%" "%ROOT%DeepSeekHarness.exe" >nul 2>&1
if errorlevel 1 echo （提示：脚本旁边的桌面副本没更新成功，不影响本次启动，可之后手动复制。）
echo 更新完成，正在启动新版本（它会重新拉起引擎并自动打开认证链接）...
start "" "%ROOT%bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
