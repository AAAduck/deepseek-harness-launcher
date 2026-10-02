@echo off
setlocal
rem 用脚本所在目录定位，不硬编码绝对路径——挪目录/拷给别人都能用。
set "ROOT=%~dp0"
set "STAGE=%LOCALAPPDATA%\DeepSeekHarness\update-staging"

rem ── 目标版本 ───────────────────────────────────────────────────────────────
rem 版本号写在四处：csproj <Version> / app.manifest / 本脚本的**文件名** / 下面这行。
rem 文件名带版本是给发布者/使用者一眼选对用的；真正的"装的是不是这一版"由下面
rem 对 exe 实际版本号的校验把关，不再靠"文件名对得上"这种自我声明。
rem csproj 的 VerifyManifestAssemblyVersion 负责在构建期保证这四处同步。
set "TARGET_VERSION=1.4.3"

rem ── 找新 exe ──────────────────────────────────────────────────────────────
rem 依次尝试，用第一个找到的：
rem   1) 第一个命令行参数 ——把 exe 直接拖到本脚本上（最省事，也最不容易装错）
rem   2) 脚本旁 bin\Release\...\publish\  ——**本工程 dotnet publish 的默认落点**。
rem      把它排在第二位，是为了让"publish 完直接双击"成立，而不用再手工挪一份。
rem      ※ 与脚本的**目标**（...\win-x64\DeepSeekHarness.exe）差一层 publish\，
rem      是两个不同的目录，不会出现"拿自己盖自己"。
rem   3) 脚本旁 out\
rem   4) 脚本旁 新版本\
rem   5) 脚本旁 DeepSeekHarness-<版本>.exe ——与旧 exe 同名会撞车，所以带版本号
rem   6) 上级 out\
rem   7) update-staging\（旧流程，保留是为了不打断已经在用的人）
rem
rem ※ **脚本所在目录下的 DeepSeekHarness.exe 绝不作为候选**：那是本脚本的
rem **目标**（脚本自己就要覆盖它）。把它当来源 = 拿旧版盖旧版，然后报"更新成功"。
rem
rem 全部判断都在顶层、不在任何 (...) 块里，因此普通 %VAR% 展开就够用——
rem 块内 %VAR% 会在块执行前一次性展开成空串，是 cmd 最经典的一类坑（见下方校验块）。
set "SRC="
if not "%~1"=="" if exist "%~1" set "SRC=%~1"
if not defined SRC if exist "%ROOT%bin\Release\net8.0-windows\win-x64\publish\DeepSeekHarness.exe" set "SRC=%ROOT%bin\Release\net8.0-windows\win-x64\publish\DeepSeekHarness.exe"
if not defined SRC if exist "%ROOT%out\DeepSeekHarness.exe" set "SRC=%ROOT%out\DeepSeekHarness.exe"
if not defined SRC if exist "%ROOT%新版本\DeepSeekHarness.exe" set "SRC=%ROOT%新版本\DeepSeekHarness.exe"
if not defined SRC if exist "%ROOT%DeepSeekHarness-%TARGET_VERSION%.exe" set "SRC=%ROOT%DeepSeekHarness-%TARGET_VERSION%.exe"
if not defined SRC if exist "%ROOT%..\out\DeepSeekHarness.exe" set "SRC=%ROOT%..\out\DeepSeekHarness.exe"
if not defined SRC if exist "%STAGE%\DeepSeekHarness.exe" set "SRC=%STAGE%\DeepSeekHarness.exe"

if not defined SRC goto no_source

echo.
echo   %TARGET_VERSION%   <--   %SRC%
echo.

rem ── 对 exe 实际版本号的校验 ────────────────────────────────────────────────
rem 此前脚本从头到尾只比对"文件名里的字符串"和"脚本里写的字符串"——那是在拿
rem 自我声明当证据：把任何版本的 exe 放进去，它照样装、照样报成功。
rem 这里读 exe 自己的版本资源（右键→属性 里显示的那个），对不上才拦。
rem 读不到（PowerShell 被禁用/精简系统）就跳过——不因缺工具而中止。
set "SRCVER="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item -LiteralPath '%SRC%').VersionInfo.ProductVersion" 2^>nul`) do set "SRCVER=%%v"
if not defined SRCVER goto skip_version
rem ProductVersion 常带 "+源码哈希" 后缀（1.4.3+abc1234），先按 '+' 截掉再**全等**比较。
rem 不用 findstr /b /c:"<版本>" 做前缀匹配：/b 只锚定行首，于是 1.4.30 会被当成 1.4.3 放行
rem ——而这正是"装错版本"最不能出现的一种错。for /f 按 '+' 切分不需要转义，
rem 避开了在批处理里拼正则要转义每个 "." 的麻烦（那是另一类引号地狱）。
set "SRCVER_BASE="
for /f "tokens=1 delims=+" %%v in ("%SRCVER%") do set "SRCVER_BASE=%%v"
if /i not "%SRCVER_BASE%"=="%TARGET_VERSION%" goto version_mismatch

:skip_version

rem ── SHA256 完整性校验（可选）───────────────────────────────────────────────
rem 旁边有同名 .sha256 才校验，没有就跳过——不要求用户为了跑一次更新而先去
rem 生成一个校验文件（那才是真正的反人类）。
rem
rem ※ 存在性判断必须在 enabledelayedexpansion **之前**做：%LOCALAPPDATA% 含 "!"
rem （合法用户名，如 C:\Users\yule!）时，延迟展开会把展开结果里的 "!" 当延迟变量
rem 标记吃掉，路径被改坏、if exist 恒为假，整个校验块被**静默跳过**（fail-open）。
rem 所以这里先在未启用延迟展开的环境里判一次，用 DO_SHA 把结果带进块内；
rem 块内一律用 !VAR!（延迟展开的取值结果不会被二次扫描）。
set "DO_SHA=0"
if exist "%SRC%.sha256" set "DO_SHA=1"

setlocal enabledelayedexpansion
if "!DO_SHA!"=="1" (
  set "COMPUTED="
  for /f "tokens=*" %%a in ('certutil -hashfile "!SRC!" SHA256 2^>nul ^| findstr /r /i /c:"^[0-9a-f][0-9a-f ]*$"') do set "COMPUTED=%%a"
  set "COMPUTED=!COMPUTED: =!"
  rem 字符类里那个空格不能少：老式 certutil 按空格分隔字节对输出（"ab cd ef"），
  rem 不含空格的正则会把哈希行整行滤掉、校验在老系统上永远中止。空格由下一行去掉。
  rem .sha256 若用记事本以 UTF-8 保存会带 BOM（EF BB BF），"set /p" 会把这 3 字节
  rem 读进 EXPECTED 开头，与纯十六进制的 COMPUTED 永不相等 → 永远失败且提示误导。
  rem findstr 只认十六进制行，天然滤掉 BOM/空白/换行。
  set "EXPECTED="
  for /f "tokens=*" %%a in ('findstr /r /i /c:"^[0-9a-f][0-9a-f ]*$" "!SRC!.sha256"') do set "EXPECTED=%%a"
  set "EXPECTED=!EXPECTED: =!"
  if not defined COMPUTED (
    >&2 echo.
    >&2 echo   [x] 算不出这个 exe 的 SHA256（certutil 失败），没敢装。
    >&2 echo       换个目录、关掉占用它的程序后重试；或删掉旁边的 .sha256 跳过校验。
    >&2 echo.
    pause
    exit /b 1
  )
  if not defined EXPECTED (
    >&2 echo.
    >&2 echo   [x] 旁边的 .sha256 是空的，没法校验。
    >&2 echo       重新生成它，或者删掉它跳过校验。
    >&2 echo.
    pause
    exit /b 1
  )
  if /i not "!COMPUTED!"=="!EXPECTED!" (
    >&2 echo.
    >&2 echo   [x] SHA256 对不上：这个 exe 多半没复制完整。
    >&2 echo       把 exe 和它的 .sha256 一起重新复制一份到同一目录再运行。
    >&2 echo.
    pause
    exit /b 1
  )
)
endlocal

rem ── 覆盖 ──────────────────────────────────────────────────────────────────
rem 引擎的 stdout/stderr 已从管道改为 engine-stdio.log 文件，引擎的生死与启动器
rem 解耦。所以 taskkill /f 只按映像名杀 DeepSeekHarness.exe（**绝不碰 node.exe**）：
rem 引擎与 3080 上的 Web 会话原样存活，新实例起来后探到 web-url.txt 的链接仍可用，
rem 直接复用引擎、打开浏览器，全程无感（实测：模拟启动器强杀后引擎持续存活写日志）。
rem 例外：从管道耦合的旧版跨进 1.3.0 的【首次】更新，旧引擎仍会随断管退出，
rem 新实例自动走完整重启（十几秒，会话历史在盘上不丢）。这一次之后皆无感。

rem taskkill 无法按登录会话过滤，可能命中其他会话的启动器实例；强杀不走
rem FormClosing，所以对方的引擎与会话不受影响，重开窗口即可。
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

echo   正在更新（约 10 秒，浏览器里的会话不受影响）...

if exist "%ROOT%bin\Release\net8.0-windows\win-x64\" goto update_bin
set "TARGET=%ROOT%DeepSeekHarness.exe"
copy /y "%SRC%" "%TARGET%" >nul
if errorlevel 1 goto copy_failed
set "START=%TARGET%"
goto done

:update_bin
rem 没有 bin\Release 输出目录（.bat 连 exe 一起分发给别人、或 bin 被清理）时，
rem 只更新脚本旁边的副本并启动它——不能因为"工程目录不存在"整体失败，
rem 那会把本可成功的更新变成报错退出（1.4.2 修复：原先连桌面副本都不更新）。
set "TARGET=%ROOT%bin\Release\net8.0-windows\win-x64\DeepSeekHarness.exe"
copy /y "%SRC%" "%TARGET%" >nul
if errorlevel 1 goto copy_bin_failed
copy /y "%SRC%" "%ROOT%DeepSeekHarness.exe" >nul 2>&1
set "START=%TARGET%"

:done
echo   OK  已更新到 %TARGET_VERSION%
echo.
echo   正在启动新版本...
start "" "%START%"
exit /b 0

rem ── 失败出口 ──────────────────────────────────────────────────────────────
rem 文案一律两行：出了什么事 + 你该干什么。不解释内部机制，也不让人去删安全文件。

:no_source
echo.
echo   [x] 没找到要装的新 exe。
echo       放到本脚本旁边的 out\ 目录，或直接把 DeepSeekHarness.exe 拖到这个脚本上再运行。
echo.
pause
exit /b 1

:version_mismatch
echo.
echo   [x] 这个 exe 是 %SRCVER%，不是 %TARGET_VERSION%。
echo       请把 %TARGET_VERSION% 的 exe 拖到这个脚本上再运行。
echo.
pause
exit /b 1

:copy_bin_failed
echo.
echo   [x] 写不进去：%TARGET%
echo       确认 DeepSeekHarness.exe 已完全退出（任务管理器里没有它）后重跑本脚本。
echo.
pause
exit /b 1

:copy_failed
echo.
echo   [x] 写不进去：%TARGET%
echo       文件可能只读或被别的程序占用，关掉占用它的程序后重跑本脚本。
echo.
pause
exit /b 1