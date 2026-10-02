@echo off
chcp 65001 >nul
setlocal
rem 用脚本所在目录定位，不硬编码绝对路径——挪目录/拷给别人都能用。
set "ROOT=%~dp0"
set "STAGED=%LOCALAPPDATA%\DeepSeekHarness\update-staging\DeepSeekHarness.exe"

rem ── 目标版本（脚本文件名里也带着它）──────────────────────────────────────
rem 本脚本改名的规则：**文件名末尾必须写明"更新到哪个版本"**，即
rem   更新启动器-更新到<版本>.bat
rem 理由：资源管理器里常常同时躺着好几份历史脚本，功能完全相同、差别只在"装的是
rem 哪一版"，用户没法从内容上分辨该双击哪一个——名字上写清楚即可一眼选对。
rem 前缀保持不变，是为了让"搜 更新启动器"仍然只命中最新那份（代价是每次发布
rem 都要改名，所以 csproj 里加了对照检查，忘了改名构建就失败）。
rem TARGET_VERSION 与文件名是两处独立书写，所以开头立刻自查一次：名字与 echo
rem 对不上就当场中止，别等到用户装完才发现装的不是他以为的那一版。
set "TARGET_VERSION=1.4.3"
echo %~nx0 | find /i /c "更新到%TARGET_VERSION%" >nul 2>&1
if errorlevel 1 (
  echo.
  echo ✗ 脚本名与目标版本不一致：本脚本叫「%~nx0」，但 TARGET_VERSION 是 %TARGET_VERSION%。
  echo   本脚本改名的规则是「更新启动器-更新到版本号.bat」，改名与 echo 必须同步。
  echo   （引擎没被动过，当前一切照旧。）
  pause
  exit /b 1
)

rem ── 本脚本的机制（1.3.0 起，本机实测验证）─────────────────────────────────
rem 引擎的 stdout/stderr 已从管道改为 engine-stdio.log 文件（启动器 tail 文件捕获
rem 认证链接与进度），引擎的生死与启动器解耦。所以：
rem   ① taskkill /f 只按映像名杀 DeepSeekHarness.exe（绝不碰 node.exe）——
rem      引擎与 3080 上的 Web 会话原样存活；
rem   ② 新实例起来后探到 web-url.txt 的链接仍可用，直接复用引擎、打开浏览器，
rem      全程无感（实测：模拟启动器强杀后引擎持续存活写日志）；
rem   ③ 例外：从管道耦合的旧版跨进 1.3.0 的【首次】更新，旧引擎仍会随断管退出，
rem      新实例自动走完整重启（十几秒，会话历史在盘上不丢）。这一次之后皆无感。
echo 即将把启动器更新到 %TARGET_VERSION%（semver 护栏补上比较器的预发布门槛；杀进程匹配收窄到引擎入口；恢复前留底失败即中止；引擎认领与日志 tail 的竞态收口；弹窗一律挂到主窗体上）。
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
rem ⚠ 1.4.2 修复：存在性判断必须在 enabledelayedexpansion **之前**做。
rem %LOCALAPPDATA% 含 "!"（合法用户名，如 C:\Users\yule!）时，延迟展开会把
rem % 展开结果里的 "!" 当延迟变量标记吃掉，路径被改坏、if exist 恒为假，
rem 整个校验块被**静默跳过**——fail-open，与上面"有则必须比对通过才继续"的承诺相反。
rem 所以先在延迟展开未启用的环境里判一次存在性，用 DO_SHA 把结果带进块内；
rem 块内引用路径一律用 !VAR!（延迟展开的取值结果不会被二次扫描，"!" 安全），
rem 不能再退回 %VAR% 内联展开。
set "SHA256FILE=%STAGED%.sha256"
set "DO_SHA=0"
if exist "%SHA256FILE%" set "DO_SHA=1"

setlocal enabledelayedexpansion
if "!DO_SHA!"=="1" (
  set "COMPUTED="
  for /f "tokens=*" %%a in ('certutil -hashfile "!STAGED!" SHA256 2^>nul ^| findstr /r /i /c:"^[0-9a-f][0-9a-f ]*$"') do set "COMPUTED=%%a"
  set "COMPUTED=!COMPUTED: =!"
  rem .sha256 若用记事本以"UTF-8"保存会带 BOM（EF BB BF），"set /p" 会把这 3 字节
  rem 读进 EXPECTED 开头，与纯十六进制的 COMPUTED 永不相等 → 校验永远失败且提示误导
  rem （"文件可能已损坏"）。用 findstr 只认十六进制行读取，天然滤掉 BOM/空白/换行。
  set "EXPECTED="
  for /f "tokens=*" %%a in ('findstr /r /i /c:"^[0-9a-f][0-9a-f ]*$" "!SHA256FILE!"') do set "EXPECTED=%%a"
  set "EXPECTED=!EXPECTED: =!"
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
if errorlevel 1 echo （当前没有运行中的启动器，直接更新文件。）
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
rem 没有 bin\Release 输出目录（.bat 连 exe 一起分发给别人、或 bin 被清理）时，
rem 只更新脚本旁边的副本并启动它——不能因为"工程目录不存在"整体失败，
rem 那会把本可成功的更新变成报错退出（1.4.2 修复：原先连桌面副本都不更新）。
if exist "%ROOT%bin\Release\net8.0-windows\win-x64\" goto update_bin
copy /y "%STAGED%" "%ROOT%DeepSeekHarness.exe"
if errorlevel 1 (
  echo 更新失败：桌面副本写不进去（可能只读或被占用）。
  pause
  exit /b 1
)
echo 更新完成，正在启动新版本（会自动接上还在跑的引擎）...
start "" "%ROOT%DeepSeekHarness.exe"
exit /b 0

:update_bin
copy /y "%STAGED%" "%ROOT%bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
if errorlevel 1 (
  echo 复制到 bin\Release 失败：目标 exe 可能仍被占用或只读。
  echo 旧启动器可能没被杀干净——确认 DeepSeekHarness.exe 已退出后重跑脚本。
  echo （引擎不受影响，会话不会因此中断。）
  pause
  exit /b 1
)
copy /y "%STAGED%" "%ROOT%DeepSeekHarness.exe" >nul 2>&1
if errorlevel 1 echo （提示：脚本旁边的桌面副本没更新成功，不影响本次启动，可之后手动复制。）
echo 更新完成，正在启动新版本（会自动接上还在跑的引擎）...
start "" "%ROOT%bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
exit /b 0
