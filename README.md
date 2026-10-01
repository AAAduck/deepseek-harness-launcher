# DeepSeek Harness 启动器

> **非官方项目**：本项目为第三方个人作品，与 DeepSeek 官方无隶属、赞助或背书关系。
> 启动器仅在你本机调用官方发布的 npm 包 `@deepseek-ai/dsh`，不捆绑、不修改其代码。

一个 Windows 启动器项目，用于启动本机 DeepSeek Harness 的 `web` profile：自动装引擎、捕获认证链接、打开控制台页面。

## 要不要装 .NET？

不用。启动器是 **.NET 8 自包含单文件**，直接分发给同学即可，对方电脑上不需要装任何运行时。

| 依赖 | 必需与否 | 说明 |
|---|---|---|
| Windows 10/11 x64 | 必需 | |
| Node.js ≥ 18 | 必需 | npm 随 Node 一起装，首次装引擎要用 |
| pnpm | 可选 | 只用于「启动后更新插件」，没装会自动回退 corepack |
| DeepSeek API Key | 必需 | 打开 Web 界面后，在「设置 → 模型」里自行填写 |

## 怎么用

1. 双击 `DeepSeekHarness.exe`（未签名，第一次会被 SmartScreen 拦一下，点「更多信息 → 仍要运行」）。
2. **第一次**点「启动」：自动把引擎装到固定目录，需要联网，约 1–2 分钟，界面有进度。
3. **之后**每次启动：窗口约 2–3 秒出现，引擎引导约 8–13 秒后浏览器自动打开认证链接
   （引擎引导实测：热缓存 8.2 秒、冷缓存 12.3 秒——这段是 DSH 引擎自己的开销，启动器只是等它）。
   插件更新要等引擎**完全就绪**才起跑（不与引擎启动时重建 profile 模块链接的写入并发改同一棵树），
   且只在需要更新时多花几秒，不再拖慢控制台出现的时间。

窗口上的按钮（一行七个，尺寸一致）：

- **启动/停止**（同一个按钮，文字与颜色随运行状态切换：未运行→绿色「启动」，运行中→红色「停止」）与 **重启**：管理引擎进程和端口。
- **刷新**：重新探测 3080 的状态并刷新按钮与状态文案（窗口开着时每 1.5 秒自动探测一次）。
- **升级**：查 npm 最新版 → 装到 `engine.tmp` → **成功才替换**正式引擎；失败保留当前版本，不影响使用。
  点下去之前会先拿新版本对一遍 profile 里插件声明的 peer 要求，有明确不满足的会先弹窗问一句（默认选「否」）。
- **版本**：查看/切换/删除本机已安装的引擎版本。升级留下的旧版本保留在 `engine.old`（下次启动归档为 `engine.<版本号>`），插件不兼容时一键切回。
- **目录**：DeepSeek Harness 相关目录一览（双击在资源管理器里打开）。
- **启动后更新插件**（复选框）：引擎启动后跑 `pnpm update`，改动在**下次启动**生效。上限是**距上次成功 20 小时**内不再自动跑（只在真正成功时才记戳记，所以失败会照常重试）；要强制重跑就删掉 `lastPluginUpdate.txt`。
- **环境**：检测 Node / npm / pnpm / 引擎 / 插件兼容性 / 端口占用。报告只列结论与需要处理的问题；若已有配置快照，弹窗会问要不要从最近一份恢复——**默认按钮是「否」**（「是」会用快照覆盖当前配置，只在确实要回滚时点）。

键盘：**回车** = 启动/停止（随主按钮），**Esc** = 停止引擎（启动/升级等忙碌期间不响应，等本次操作收尾），**Tab** 可遍历所有控件。

### 生命周期与单实例

- **关闭窗口 = 结束引擎**（刻意为之）。点 ✕ 走 `FormClosing` 主动清理，把引擎进程树
  一并结束，不留占 3080 的后台进程——"关窗"就是你亲手终止会话的语义。
- **强杀 / 崩溃 / 更新不再带走引擎**（1.3.0 起）。引擎的 stdout/stderr 原先是管道、
  读端挂在启动器进程上——启动器一死，引擎下次写日志就随断管退出（旧版实测 ~1 秒）。
  现在引擎经 cmd 把 stdio 重定向进 `engine-stdio.log`，启动器按增量 tail 读文件
  （捕获认证链接与进度）。引擎的生死从此与启动器解耦：新实例起来后探到
  `web-url.txt` 的链接仍然活着，直接**复用**还在跑的引擎并打开浏览器，
  **Web 会话全程不中断**（模拟实测：强杀"启动器"后引擎继续存活写日志）。
  ⚠ 唯一例外：**从管道耦合的旧版跨进 1.3.0 的首次更新**仍会重启一次引擎——
  当时活着的还是旧架构引擎。
- **同时只能开一个启动器**。重复双击不会开出第二个实例，而是把已有窗口切到前台。
  这是刻意的：两个实例会互相把对方的引擎当残留进程杀掉，还会争同一个 `web-url.txt`。
- 任何时候引擎真的重启了：对话**历史在盘上，不丢**；新认证链接自动开浏览器，接续原会话。

## 几个常见问题

**有多份 Node 怎么办？** 启动器会逐个试 `--version`，自动选第一个 ≥ 18 的，npm 强制用同一目录那份。装在非标准位置时，把路径写进 `node-dir.txt` 即可。

Node 查找顺序：PATH → exe 同级的 `node\` → `%ProgramFiles%\nodejs` → `%APPDATA%\npm` → `%LOCALAPPDATA%\DeepSeekHarness\node-dir.txt`。

**引擎装在哪、会不会自动更新？** 引擎装在固定目录，启动时直接运行 `node_modules\@deepseek-ai\dsh\lib\bin.js web`，不走 npx、不查版本、不联网重装。所以它**不会**自动跟进新版，想升级就点「升级」。

装引擎用的是与 `npx` 内部相同的标准做法：在私有目录里跑一次 `npm install` 再直接执行 `bin.js`。
区别在于版本是**钉死的**：安装时先查精确版本号，配合 `npm install --save-exact`，所以
`engine/package.json` 与 `engine/package-lock.json` 记录的版本始终一致，同一份 manifest
在任何日子重装都得到同一个版本（直接写 `"latest"` 会绕过锁文件、抓到当天最新版，无法复现）。

**npm 源跟随你的配置。** 引擎的安装与「升级」的版本查询都会先读
`npm config get registry` 并显式使用它，所以 `npm config set registry https://registry.npmmirror.com`
对启动器**同样生效**。没配过才回落到官方源 `https://registry.npmjs.org/`。
读取到的源会做参数校验，含引号或空白的值不采用。

首次安装参考耗时：实测本机 537 个包约 **67 秒**（不含版本查询），视网络而定；安装超时上限为 900 秒。

## 默认环境一览

| 项 | 位置 |
|---|---|
| DSH 引擎 | `%LOCALAPPDATA%\DeepSeekHarness\engine` |
| DSH profile | `%USERPROFILE%\.dsh\profiles\web` |
| 认证 URL | `%LOCALAPPDATA%\DeepSeekHarness\web-url.txt` |
| 引擎输出日志 | `%LOCALAPPDATA%\DeepSeekHarness\engine-stdio.log`（引擎 stdout/stderr 全量；每次重新拉起引擎时重开。复用期间持续增长，量级为引擎自身日志量） |
| 更新日志 | `%LOCALAPPDATA%\DeepSeekHarness\update-log.txt`（超 256 KB 自动截断） |
| 插件更新节流戳记 | `%LOCALAPPDATA%\DeepSeekHarness\lastPluginUpdate.txt`（只在更新成功后写；删掉 = 下次启动强制更新） |
| 启动异常日志 | `%LOCALAPPDATA%\DeepSeekHarness\startup-log.txt`、`crash-log.txt` |
| 自定义 Node 目录 | `%LOCALAPPDATA%\DeepSeekHarness\node-dir.txt`（可选） |
| 引擎版本锁 | `%LOCALAPPDATA%\DeepSeekHarness\engine-version.txt`（可选，见下） |
| 配置快照 | `%LOCALAPPDATA%\DeepSeekHarness\config-backups\`（保留最近 8 份；**含明文密钥副本，目录本身勿外传**） |
| Web 端口 | `3080` |

### 引擎版本锁（可选）

插件的 peer 要求往往只针对某个引擎版本验证过。若希望**永不自动跟进新版**，
把版本号写进 `engine-version.txt`（例如 `0.1.5-rc.2`）：

- 引擎缺失时会**只装**该版本；
- 「升级」按钮会提示"已锁定"并跳过，不会把它顶掉；
- 「环境」里会显示 `🔒 引擎已锁定 <版本>`。

清空该文件即恢复跟随最新版。

## 环境变量（可选）

| 变量 | 作用 |
|---|---|
| `HTTP_PROXY` / `HTTPS_PROXY` | 让 node/npm/pnpm 走代理（Node 24+ 需配合内置的 `NODE_USE_ENV_PROXY`，启动器会自动加；装引擎、升级引擎、更新插件三条路径都会带上） |
| `DSH_FORCE_LOCAL_PROXY=1` | 强制探测本机 `127.0.0.1:7897` 代理。默认只在系统里存在任何代理线索时才探测——没有代理的机器上，这次探测会白等一个 150 ms 超时，所以默认跳过 |


## 构建

需要 .NET 8 SDK，发布参数（自包含、单文件、win-x64、Release 无调试符号）已写进工程文件，直接跑：

```powershell
dotnet publish --configuration Release
```

产物在 `bin/Release/net8.0-windows/win-x64/publish/DeepSeekHarness.exe`。

> 旧启动器**正在运行时**这个默认路径会被锁住（报 MSB3027，锁文件的正是运行中的
> `DeepSeekHarness.exe`），此时改用独立输出目录，见下面「更新启动器本体」。

### 跑单测

```powershell
dotnet test tests\DeepSeekHarness.Tests\DeepSeekHarness.Tests.csproj
```

只测"判错了不报错"的那几处决策：杀哪些进程、要不要拍配置快照、恢复路径是否越界、
semver 范围怎么判。UI 与 IO 不测——那些靠肉眼和上面的布局自检。
改动这几个函数前先跑一遍，绿了再改。

覆盖的分支与用例是**一一对应**的，改匹配规则前先看注释里写的事故与误伤面。

## 更新启动器本体（标准流程：随时跑 `更新启动器.bat`，Web 会话不中断）

每次优化启动器本体都按这个方式更新。1.3.0 起引擎 stdio 已文件化（见「生命周期与单实例」），
强杀启动器不再连带引擎：脚本杀掉旧启动器后，引擎与 3080 上的 Web 会话原样活着，
新实例自动探到 `web-url.txt` 的链接可用 → **复用引擎**、打开浏览器，全程十几秒内完成，
**不需要挑空闲时段**。唯一的例外是**首次**从管道耦合的旧版跨进 1.3.0 的那一次：
当时的旧引擎仍会随断管退出，新实例自动走完整重启（清残留 → 拉引擎 → 新链接开浏览器），
打断约十几秒、历史不丢——此后所有更新都无感。

流程五步：

1. **升版本号**：`DeepSeekHarness.csproj` 的 `<Version>`，同步 `app.manifest` 的
   `assemblyIdentity version` 和 `更新启动器.bat` 开头 echo 的版本说明。
2. **发布单文件包**（旧启动器在跑，用独立输出目录绕开 `bin\` 锁）：
   ```powershell
   dotnet publish DeepSeekHarness.csproj -c Release -p:OutDir=D:\tmp\dsh-pub\bin\ -o D:\tmp\dsh-pub\out
   ```
3. **放进暂存目录**（脚本从这里取件；同时生成 SHA256 校验文件，脚本覆盖前会比对）：
   ```powershell
   Copy-Item D:\tmp\dsh-pub\out\DeepSeekHarness.exe "$env:LOCALAPPDATA\DeepSeekHarness\update-staging\DeepSeekHarness.exe" -Force
   certutil -hashfile "$env:LOCALAPPDATA\DeepSeekHarness\update-staging\DeepSeekHarness.exe" SHA256 | Select-Object -Skip 1 -First 1 | ForEach-Object { $_.Replace(' ','') } | Out-File "$env:LOCALAPPDATA\DeepSeekHarness\update-staging\DeepSeekHarness.exe.sha256" -Encoding ascii
   ```
4. **双击 `更新启动器.bat`，按任意键**。脚本依次：按映像名强杀旧启动器（绝不碰 node）→
   覆盖 `bin\Release\net8.0-windows\win-x64\` 与脚本旁的桌面副本 → 启动新实例。
5. **验收**：新窗口出现、浏览器自动接回 3080 正在跑的会话；点「环境」确认版本与状态。

注意：

- 复用路径不会跑本轮插件后台更新（本来它也只是"下次启动生效"，无碍）；
- 脚本只按映像名杀启动器这一个进程；引擎版本要变的话，更新完在新窗口点「升级」；
- 复制失败分支（目标仍被占用）不伤引擎与会话：确认旧进程退干净后重跑脚本即可；
- 单实例互斥保证新旧实例不会重叠争抢引擎与 `web-url.txt`（脚本先杀后起，顺序天然正确）；
- `.sha256` 文件可选：放了就校验，没放就跳过（兼容旧流程）。

## 已知限制

- **未代码签名**，SmartScreen 会提示；Defender 也可能问是否允许 node 监听本地端口，点允许。
  给引擎目录加一条 Defender 排除路径可以明显加快引擎引导（实测冷启动 12.3 → 8.2 秒），
  因为 `engine` 下有 25 000 多个文件会被实时扫描。
- **不自动跟进引擎版本**，升级是显式动作，免得每次启动都静默重装。
- **首次跨入 1.3.0 的更新仍会重启一次引擎**：当时在跑的旧引擎 stdio 还是管道、随旧启动器陪葬；
  迁完这次，之后才进入无感复用的常态。
- 引擎输出日志 `engine-stdio.log` 由 cmd 以追加句柄持有，**引擎运行期间删不掉**；
  它只在下次重新拉起引擎时重开，长期复用期间会缓慢增长（在意的话重启一次引擎即可归零）。
- **端口 3080 被占**时启动失败，报错会附 `netstat -ano | findstr :3080`；
  若该端口落在 Windows 动态端口范围内（Hyper-V / WSL / Docker Desktop 预留段），报错会额外给出释放方法。
- 首次装引擎中途被强行结束，会留下 `engine.tmp` 残留；下次安装会自动清理（「环境」检测也会提示）。
- 国内访问 npm 慢的话，先执行 `npm config set registry https://registry.npmmirror.com`。
- **token 有效性无法在启动器侧校验**：DSH 的 `/` 无论 token 对错都返回同样的页面，
  `/api/*` 对任何 HTTP 头形式（Bearer / Cookie / query）一律 401——认证是浏览器端握手。
  所以启动器只能确认"这台 3080 上跑的确实是 DSH"，无法确认链接里的 token 还有效。

以下几条是已实现的增强（1.4.0 起），记录设计意图，供后续维护参考：

- **token DPAPI 加密落盘**：`web-url.txt` 里的认证链接现在经 `ProtectedData.Protect`
  （CurrentUser 作用域）加密后写入，只有同一个 Windows 用户能解开。旧版明文文件读取时
  自动升级为加密格式，用户无感。配置快照仍备份 `.credentials.yaml` 明文（还原需要），
  提示不变：**这两个目录勿贴进截图或共享给别人**。
- **`更新启动器.bat` SHA256 校验**：把 exe 放进 `update-staging\` 时同时放一个
  `.sha256` 文件，脚本覆盖前自动比对。校验失败会中止并提示，防止 staging 文件被写坏
  或替换。没有 `.sha256` 文件时跳过校验（兼容旧流程）。
  ⚠ 1.4.0 修复过它自身的两个坑（旧版校验**永远通过**）：① 括号块内 `%VAR%` 是
  解析期展开，比较的两个值都是空串——必须 `setlocal enabledelayedexpansion` +
  `!VAR!`；② `findstr /v "hash"` 过滤不掉中文系统的 certutil 表头（"…哈希:"），
  改为只认纯十六进制行。改回来时先拿"故意写坏的 .sha256"验收一次。
- **代理探测正负分开缓存**：探到 7897 开着 → 永久缓存，之后零成本；探不到 → 负缓存
  60 秒后重探。四条调用路径（启动/装引擎/升级/查版本）在一次启动内不会重复探测，
  而"启动器开着才打开代理"的用户最多多等 60 秒。
- **低频路径异常统一日志**：配置快照、恢复、引擎归档、孤儿锁清理等低频关键路径的
  静默 `catch { }` 改为经 `Swallow.Quiet(ex, context)` 记录到 startup-log，
  同一异常源每小时只记一条。磁盘满、权限被撤这类系统性问题不再完全无信号。
- **`engine.migrating` 定时归档**：`RefreshStatusAsync`（每 1.5 秒）挂一个每小时一次的
  节流器，长期只复用不重启的用户也能在 1 小时内自动归档 `engine.migrating`，
  不再需要点「重启」才能释放那 214 MB。
- **PID 复用 StartTime 校验**：`StopHarnessProcessesCore` 与 `StopEngineForExit` 杀进程前，
  先比对 `Process.StartTime` 与 WMI 快照里的 `CreationDate`。PID 被系统复用时
  StartTime 必然不同，直接跳过——宁可漏掉一个残留，也不能误杀同 PID 的新进程。
  ⚠ 1.4.0 修复：比对必须带 **1ms 容差**（`IsSameProcessStart`，有单测）。WMI 的
  `CreationDate` 是 DMTF 微秒精度、`Process.StartTime` 是 100ns 精度，同一进程的
  两个读数恒差 0–0.9 µs——严格相等在实测样本里只命中约 1/7，旧写法让「停止」
  大概率空转且不报错。改回严格相等前先看 `ProcessStartToleranceTests`。
- **semver 预发布门槛**（1.4.0）：caret/tilde 范围只放行与基准同
  `major.minor.patch` 三元组的预发布候选（npm 规则，`PrereleaseAllowedInRange`）。
  此前 `0.1.5-rc.9` 会被判满足 `^0.1.0`——升级护栏在**漏报警**的方向出错。
  范围 token 里基准本身带预发布标识时维持旧语义（返回 null"未能判定"）。
- **引擎日志截断保留尾部**：每次真启动时把 `engine-stdio.log` 截断到末尾 8 MB
  （在第一个完整换行符处切开，避免截断多字节 UTF-8 字符），日志体量从此有界。

## 文件说明

- `HarnessForm.cs`：主界面、进程管理、端口检测、认证 URL 捕获（engine-stdio.log 增量 tail）、
  引擎安装与升级、插件兼容性检查、配置备份恢复。
- `EngineVersionsForm.cs`：引擎版本管理窗口（列表 / 切换 / 删除）。列举与删除都放后台线程，
  窗口因此能在遍历 2.5 万个 `node_modules` 文件时保持响应。
- `FoldersForm.cs`：相关目录一览窗口。
- `DpapiFile.cs`：DPAPI（CurrentUser 作用域）文件加密/解密。认证链接（`web-url.txt`）含完整
  token，明文落盘时任何能读 `%LOCALAPPDATA%` 的进程都能拿到；加密后只有同一 Windows 用户
  能解开。旧版明文文件读取时自动升级为加密格式，用户无感。DPAPI 不可用（企业策略禁用等）
  时降级写明文——暴露面回到旧版，好过"链接无法持久化"让引擎复用静默失效。
- `Swallow.cs`：低频路径异常统一日志（`Swallow.Quiet(ex, context)`）。同一异常源
  每小时只写一条 startup-log，磁盘满/权限消失等系统性问题不再完全静默；
  高频路径（刷新、tail 循环）保持零开销静默。
- `ConfigBackup.cs`：配置快照（启动前与升级前自动拍摄、按文件哈希去重、一键恢复）。
  快照判定（`NeedsSnapshot`）与恢复路径守卫（`IsWithinRoot`）是纯函数，有单测。
  `engine.old` 的归档不在这个窗口里做——那是**写操作**，而列举版本是只读操作，
  挂在只读路径上会让两个并发的后台扫描同时对同一个 `engine.<版本>` 槽"删除 + 改名"。
  归档因此移到了点「启动/重启」的主流程里（而不是引擎安装那条支路上：
  引擎随启动器存活时启动流程会走复用分支直接返回，挂在支路上等于形同虚设）。
  跨进程互斥靠"同卷目录改名是原子的"：先 `engine.old` → `engine.migrating` 认领，
  抢到的人才做删除+归档。原先两个会话的启动器会各自删一次同名槽，交错起来会把
  对方刚归档好的那份一起删掉。认领中途被杀会留下 `engine.migrating`（它就是那份数据本身），
  下次启动先收尾再认领；活动引擎缺失时 `RecoverEngineSwap` 也会认它当恢复源。
- `LayoutDump.cs`：布局自检（`DSH_LAYOUT_DUMP=1` / `DSH_LAYOUT_TEST=1` 时把真实几何写入 `layout-dump.txt`）。
  自检只在 `Program.cs` 的 `DSH_LAYOUT_TEST=1` 入口里驱动，两个对话框不再各自挂 `Shown` 处理器。
- `Program.cs`：程序入口、单实例互斥、启动异常兜底（写 `crash-log.txt`）。
- `app.manifest`：应用清单（asInvoker 不提权、supportedOS 声明、长路径感知）。
  DPI 感知不在清单里声明——它由 csproj 的 `ApplicationHighDpiMode` 给出（取值 `SystemAware`），
  与 `UseWindowsForms` 生成的 `ApplicationConfiguration.Initialize()` 保持单一来源。
- `DeepSeekHarness.csproj`：.NET 8 构建配置（发布参数已内置）。含 `InternalsVisibleTo`：
  只对配套测试工程开放几个 internal 纯函数；同时用 `DefaultItemExcludes` 把整个 `tests\`
  从默认通配里摘掉（只挡 `.cs` 的话，测试工程的 bin/obj 产物仍会被逐个求值，
  将来谁在 `tests\` 下放个 `.resx` 还会被编进启动器资源）。
- `tests\DeepSeekHarness.Tests\`：xunit 单测（141 条）。刻意只覆盖"判错了不报错"的决策：
  两条杀进程路径（点「停止」与关窗清扫）、PID 复用 StartTime 容差、端口收窄、快照判定、
  恢复路径守卫、版本号白名单、版本归档的规划顺序、semver 范围判定（含预发布门槛）。
  这几处的共同点是错了不会有任何报错，只在用户眼前发生——所以必须有测试钉住。
  几条用例是**专门为了让别的用例变红**而存在的，例如端口收窄那条：把它整行删掉，
  必须有用例失败，否则说明它压根没被测住。
  两条杀进程路径曾分叉过一次（空 `engineDir` 护栏只补在了一条上，`Contains("")`
  恒为真会让关窗时把所有进程整树杀掉）——所以**改一条时记得连另一条一起看**。
- `更新启动器.bat`：标准更新入口，随时可执行（1.3.0 起 Web 会话不中断；首次迁移例外见
  「更新启动器本体」）。按映像名只杀启动器（不碰 node）、
  从 `%LOCALAPPDATA%\DeepSeekHarness\update-staging\` 取新 exe（有 `.sha256` 则校验）、
  覆盖 `bin\Release` 与脚本旁副本、拉起新实例（自动复用仍在运行的引擎；
  复用不可用时自动重拉并打开新认证链接）。
- `DeepSeekHarness.exe`：构建产物，约 155 MB，不入库。从 [GitHub Releases](https://github.com/AAAduck/deepseek-harness-launcher/releases) 下载，或自行构建。