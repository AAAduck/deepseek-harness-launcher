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
echo 即将把启动器更新到 1.4.1（重启不再回放旧会话日志里的死链接；SHA256 校验兼容空格分隔的老式 certutil）。
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

rem ── SHA256 完整性校验（防 staging 目录被写坏或替换）──────────────────────
rem 发布时把 exe 和它的 .sha256 文件一起放进 update-staging\。
rem 没有 .sha256 文件时跳过校验（兼容旧流程），有则必须比对通过才继续。
rem
rem ⚠ 1.4.0 修复（务必别退回旧写法）：旧版把比较写在 if (...) 括号块里且用
rem %VAR% 引用——cmd 对整个括号块做**一次性解析**，块内 %VAR% 在任何一行
rem 执行前就展开成空串，于是实际比较的是 ""==""，校验**永远通过**
rem （实测：期望哈希故意写错仍打印"✓ 校验通过"）。必须用 setlocal
rem enabledelayedexpansion + !VAR! 延迟展开。
rem 摘要提取也一并修了：不再 findstr /v "hash"（中文系统的 certutil 表头是
rem "SHA256 的 C:\… 哈希:"，不含小写 "hash"，过滤靠不住），改为只认
rem 十六进制行。⚠ 1.4.1 两处缺一不可：① 字符类补空格——老式 certutil 按空格
rem 分隔字节对输出（"ab cd ef"），不含空格的正则会把哈希行整行滤掉、校验在
rem 老系统上永远中止；空格由下一行 !COMPUTED: =! 去掉。② 必须加 /c:——
rem findstr /r 会把带空格的引号串**拆成多个模式**（实测表头/提示行反而被误命中），
rem /c: 才让整串是"一个"正则。
setlocal enabledelayedexpansion
set "SHA256FILE=%STAGED%.sha256"
if exist "%SHA256FILE%" (
  set "COMPUTED="
  for /f "tokens=*" %%a in ('certutil -hashfile "%STAGED%" SHA256 2^>nul ^| findstr /r /i /c:"^[0-9a-f][0-9a-f ]*$"') do set "COMPUTED=%%a"
  set "COMPUTED=!COMPUTED: =!"
  set /p EXPECTED=<"%SHA256FILE%"
  if not defined COMPUTED (
    echo.
    echo ✗ 无法计算 staging 文件的 SHA256（certutil 失败）。
    echo   请确认文件完整后重试，或删掉 .sha256 文件跳过校验。
    pause
    exit /b 1
  )
  if not defined EXPECTED (
    echo.
    echo ✗ .sha256 文件是空的，无法校验。
    echo   请重新生成或删掉 .sha256 文件跳过校验。
    pause
    exit /b 1
  )
  if /i not "!COMPUTED!"=="!EXPECTED!" (
    echo.
    echo ✗ SHA256 校验失败，staging 文件可能已损坏或被替换。
    echo   期望：!EXPECTED!
    echo   实际：!COMPUTED!
    echo   请重新放入正确的 DeepSeekHarness.exe 和 .sha256 文件。
    pause
    exit /b 1
  )
  echo ✓ SHA256 校验通过
)
endlocal & rem 延迟展开只用于校验块；后续按普通展开继续（保持脚本其余部分原样）

echo 按任意键开始更新（浏览器里的会话不用关）...
pause >nul
rem 只按映像名杀启动器（taskkill 无法按登录会话过滤，可能命中其他会话的启动器实例；
rem 强杀不走 FormClosing，所以对方的引擎与会话不受影响，重开窗口即可）。
taskkill /im DeepSeekHarness.exe /f >nul 2>&1
rem 等进程真的退出（最多 10 秒），比固定 timeout 2 稳——复制失败分支仍在兜底。
set /a _w=0
:poll_exit
tasklist /fi "imagename eq DeepSeekHarness.exe" | find /i "DeepSeekHarness.exe" >nul 2>&1
if errorlevel 1 goto exited
if %_w% GEQ 10 goto exited
timeout /t 1 /nobreak >nul
set /a _w+=1
goto poll_exit
:exited
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
