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
  检测期间也占 busy：「启动/升级」变灰、Esc 不响应。理由见「已实现的增强」里「环境检测期间也占 busy」那条。

键盘：**回车** = 启动/停止（随主按钮），**Esc** = 停止引擎；启动/升级进行中点「停止」会先取消当前操作再杀引擎，不会静默吞掉。**Tab** 可遍历所有控件。

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
| 引擎输出日志 | `%LOCALAPPDATA%\DeepSeekHarness\engine-stdio.log`（引擎 stdout/stderr 全量；每次真正重新拉起引擎时截断到**末尾 8 MB**——不是清零，见「已知限制」。复用期间持续增长，量级为引擎自身日志量） |
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

> 工程里开着 `TreatWarningsAsErrors`（警告即错误）：nullable 在本项目里的约束力来自
> "编译器把每一个可能为 null 的地方都点出来"，而本项目出过的 bug 形状恰恰全是
> **不报编译错、只静默失效**的（探针认错身份、归档认领抢不到、恢复漏报失败……）。
> 默认只警告等于把"绝不能崩"降级成提示，所以把它钉回构建期。

> 旧启动器**正在运行时**这个默认路径会被锁住（报 MSB3027，锁文件的正是运行中的
> `DeepSeekHarness.exe`），此时改用独立输出目录，见下面「更新启动器本体」。

### 跑单测

```powershell
dotnet test tests\DeepSeekHarness.Tests\DeepSeekHarness.Tests.csproj
```

只测"判错了不报错"的那几处决策：杀哪些进程、要不要拍配置快照、恢复路径是否越界、
semver 范围怎么判、端口探针凭什么断定"这台 3080 上跑的确实是 DSH"、
tail 循环凭什么判定自己还是当前代。UI 与 IO 不测——那些靠肉眼和上面的布局自检。
改动这几个函数前先跑一遍，绿了再改。

覆盖的分支与用例是**一一对应**的，改匹配规则前先看注释里写的事故与误伤面。
本轮新增的这批判定（探针握手、tail 代际退役、版本槽名对认领副本的排除）同样按这条标准钉住，
不是"等有空再补"——它们的失效形态全是**静默**的，界面上看不出任何异常。

## 更新启动器本体（标准流程：随时跑 `更新启动器.bat`，Web 会话不中断）

每次优化启动器本体都按这个方式更新。1.3.0 起引擎 stdio 已文件化（见「生命周期与单实例」），
强杀启动器不再连带引擎：脚本杀掉旧启动器后，引擎与 3080 上的 Web 会话原样活着，
新实例自动探到 `web-url.txt` 的链接可用 → **复用引擎**、打开浏览器，全程十几秒内完成，
**不需要挑空闲时段**。唯一的例外是**首次**从管道耦合的旧版跨进 1.3.0 的那一次：
当时的旧引擎仍会随断管退出，新实例自动走完整重启（清残留 → 拉引擎 → 新链接开浏览器），
打断约十几秒、历史不丢——此后所有更新都无感。

流程五步：

1. **升版本号**：`DeepSeekHarness.csproj` 的 `<Version>`（必须 x.y.z 三段式，不是会构建失败），
   同步 `app.manifest` 的 `assemblyIdentity version`（写成 `x.y.z.0`）和 `更新启动器.bat` 开头 echo 的版本说明。
   三处漏改任何一处都会**直接构建失败**：csproj 的 `VerifyManifestAssemblyVersion` 目标在
   `PrepareForBuild` 之前拿 `<Version>` 去清单里找 `assemblyIdentity version="<版本>.0"`，
   再读一遍 `更新启动器.bat` 确认里面有「更新到 <版本>」那句 echo——找不到就 `Error`。
   版本号是手工写在三处的，不同步的后果是"exe 属性显示 1.4.3、
   Windows 看到的文件版本还是 1.4.2"，**没有任何地方会报错**——所以干脆让它变成构建失败。
2. **发布单文件包**（旧启动器在跑，用独立输出目录绕开 `bin\` 锁）：
   ```powershell
   dotnet publish DeepSeekHarness.csproj -c Release -p:OutDir=D:\tmp\dsh-pub\bin\ -o D:\tmp\dsh-pub\out
   ```
3. **放进暂存目录**（脚本从这里取件；同时生成 SHA256 校验文件，脚本覆盖前会比对）：
   ```powershell
   Copy-Item D:\tmp\dsh-pub\out\DeepSeekHarness.exe "$env:LOCALAPPDATA\DeepSeekHarness\update-staging\DeepSeekHarness.exe" -Force
   certutil -hashfile "$env:LOCALAPPDATA\DeepSeekHarness\update-staging\DeepSeekHarness.exe" SHA256 | Select-Object -Skip 1 -First 1 | ForEach-Object { $_.Replace(' ','') } | Out-File "$env:LOCALAPPDATA\DeepSeekHarness\update-staging\DeepSeekHarness.exe.sha256" -Encoding ascii
   ```

   `.sha256` 文件**请勿用记事本"UTF-8"格式保存**——那会引入 BOM（EF BB BF），脚本读取时会把 BOM 当十六进制字符，校验永远失败。用上面的 `-Encoding ascii` 命令生成即可。
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
  它只在下次真正重新拉起引擎时才被截断到末尾 8 MB（不足 8 MB 时原样保留，**永远不会归零**），
  长期复用期间会缓慢增长。
- **端口 3080 被占**时启动失败，报错会附 `netstat -ano | findstr :3080`；
  若该端口落在 Windows 动态端口范围内（Hyper-V / WSL / Docker Desktop 预留段），报错会额外给出释放方法。
- 首次装引擎中途被强行结束，会留下 `engine.tmp` 残留；下次安装会自动清理（「环境」检测也会提示）。
- 国内访问 npm 慢的话，先执行 `npm config set registry https://registry.npmmirror.com`。
- **token 有效性无法在启动器侧校验**：DSH 的 `/` 无论 token 对错都返回同样的页面，
  `/api/*` 对任何 HTTP 头形式（Bearer / Cookie / query）一律 401——认证是浏览器端握手。
  所以启动器只能确认"这台 3080 上跑的确实是 DSH"，无法确认链接里的 token 还有效。
  而"确实是 DSH"这句本身也不是看状态码就算的：HTTP 200 也必须带 SPA 首页的
  `<title>DeepSeek Harness</title>` 标记（见「已实现的增强」里「端口探针验身份」那条）。

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
  改为只认十六进制行。**1.4.1 再修**：字符类补上空格，且必须加 `/c:`——老式
  certutil 按空格分隔字节对输出哈希（`ab cd ef …`），不含空格的正则会把哈希行
  整行滤掉，校验在那些机器上**永远中止**（fail-closed，但表现为"别人机器上更新
  不了"）；而 `findstr /r` 会把带空格的引号串拆成多个模式（实测中文表头反而被
  误命中），`/c:` 才让整串作为**一个**正则生效。改回来时先拿
  "故意写坏的 .sha256"验收一次。
- **代理探测正负分开缓存**：探到 7897 开着 → 永久缓存，之后零成本；探不到 → 负缓存
  60 秒后重探。四条调用路径（启动/装引擎/升级/查版本）在一次启动内不会重复探测，
  而"启动器开着才打开代理"的用户最多多等 60 秒。
- **低频路径异常统一日志**：配置快照、恢复、引擎归档、孤儿锁清理等低频关键路径的
  静默 `catch { }` 改为经 `Swallow.Quiet(ex, context)` 记录到 startup-log，
  同一异常源每小时只记一条。磁盘满、权限被撤这类系统性问题不再完全无信号。
- **端口探针验身份，不只看状态码**：`ProbeServerAsync`（状态栏与启动前预检）与
  `ProbeUrlAsync`（复用路径）共用 `IsDshHandshake` 这一个纯函数判定。两个标记都取自
  **引擎自身产物**，不是本启动器的约定：未认证时引擎回 401 + `dsh web authentication required`，
  带对 token 时回 200 + SPA 首页，其 `<title>DeepSeek Harness</title>` 恒落在第一个
  4 KB 缓冲内（分块读、命中即返回，跨块边界用滑动窗口补住）。
  此前是 200 一律放行，于是 3080 上坐着任意本机 dev server 时（本项目自己的 web profile
  就跑在 Vite 上，对 `/` 回 200 是再正常不过的事）：状态栏误报"运行中"、启动前那道
  "端口被别的程序占用"的预检被一并绕过（用户最后看到的是引擎 EADDRINUSE 的原始报错，
  正是那道预检要避免的结局）；而复用路径那一条更重——它是唯一会把 token 作为 query
  发出去的调用点，等于把浏览器**和 token 一起**送到不相干的服务上、token 进了它的访问日志。
  ⚠ 失手方向刻意选"宁可说不是 DSH"：认错身份要把 token 递出去，认不成只是多一次重新拉起。
  探针那个 HttpClient 还开着 gzip/deflate 自动解压——DSH 的 web server 带 gzip 中间件，
  不解压就只拿到压缩流、标记永远读不到，于是 200 分支会恒为假。
- **`engine.migrating` 定时归档**：`RefreshStatusAsync`（每 1.5 秒）挂一个每小时一次的
  节流器，长期只复用不重启的用户也能在 1 小时内自动归档 `engine.migrating`，
  不再需要点「重启」才能释放那 214 MB。⚠ 这条与点「启动」那次归档是**两个执行者**，
  收尾路因此也得自己认领（见「文件说明」里 `ConfigBackup.cs` 那条）。
- **环境检测期间也占 busy**：`RunEnvCheckAsync` 同样走 `EnterBusy(envButton, "检测中")`。
  此前它只判 `busy` 不设 busy，而探测段是 4 个候选 × 10 秒级的等待——这期间「启动/升级」
  全亮，用户可以点「启动」把引擎拉起来，再在随后弹出的报告里点「是」恢复配置：
  启动器自己把"启动前/恢复前拍快照"这件事要防的后果给诱发了。
  占 busy 同时也解决报告弹窗与进行中的启动互相覆盖状态文案。
- **配置恢复如实报告部分失败**：`ConfigBackup.Restore` 现在返回 `RestoreResult`
  （成功数 + 失败清单）。此前逐文件 `catch { }` 吞掉异常、只把成功数带回去，于是
  "引擎正在运行、正占着这些文件"这个**最常见**的失败场景走的恰恰是成功分支：
  用户看到"已恢复 N 个文件"，而 `$DSH_HOME` 实际停在新旧混合态——比整体失败更难排查。
  现在清单在弹窗里逐条列出（最多 12 条，其余只报数量），一个都没写回时另有专门的报错弹窗
  并提示先点「停止」。另外恢复前那份"留底快照"的返回值**必须看**：`CreateSnapshot`
  的 null 是二义的（配置无变化无需拍 / 真拍失败了），而"这一步本身可逆"是刚刚对用户
  做出的承诺，不能靠猜——1.4.2 起返回 null 至少记一条 startup-log；
  1.4.3 进一步收紧：留底失败**直接中止恢复**（当前配置零改动，弹窗说明原因）。
  此前"记日志后照常恢复、覆盖完才在结果框里补一句警告"的写法，在备份目录写不进
  而配置目录写得进的机器上，恰恰会执行它要防的那种不可逆覆盖。
- **子窗体只在用户自己点关闭时询问**：`EngineVersionsForm` 与 `FoldersForm` 的关闭确认
  都以 `e.CloseReason == CloseReason.UserClosing` 为前提，其余关闭理由直接放行。
  系统关机 / 注销 / 任务管理器结束同样会走 `FormClosing`，而此刻 `busy` 的概率最高
  （正在扫 2.5 万个文件）：弹模态框 + `e.Cancel = true` 在 Windows 看来就是
  "此应用阻止关机"，用户只能强杀，连日志都留不下。那种场景下"操作会不会跑完"
  根本不是用户需要做决定的事，放行的代价至多是关窗早于列举完成——而列举本来就在后台线程。
- **递归删除与日志截断一律后台线程**：`InstallEngineAsync` 的替换三步（清 `engine.old`、
  现行目录改名、staging 顶上）与失败回退时的 staging 清理，以及 `TruncateEngineLog`，
  都经 `Task.Run` 挪出 UI 线程。此前它们跑在 UI 线程上：2.5 万个文件 + 只读属性全树枚举
  + 超过 8 MB 时的同步读写，确定性冻结界面数秒到数十秒——与本项目自订的
  "2.5 万文件的递归删除不能挂 UI 线程"（见「文件说明」里两个子窗体那条）自相矛盾。
- **启动流程在 `process.Start()` 之前补了取消检查点**：此前最后一个取消检查点在端口预检处。
  关窗时 `FormClosing` 先跑完 `CancelPendingStart + StopEngineForExit`，而消息泵仍可能分发
  已排队的续延（点 ✕ 落在 `await Task.Run(ClearOrphanProfileLock)` 或端口预检的那几百毫秒里
  就会这样）：引擎被拉起、`dshProcess` 被赋值，然后才 return——那时 `StopEngineForExit`
  早已执行完，没人再管它，窗口关了、3080 上留着一个没人接管的孤儿。
  检查点必须**紧贴** `process.Start()`，往后再挪一步就白挪。
- **PID 复用 StartTime 校验**：`StopHarnessProcessesCore` 与 `StopEngineForExit` 杀进程前，
  先比对 `Process.StartTime` 与 WMI 快照里的 `CreationDate`。PID 被系统复用时
  StartTime 必然不同，直接跳过——宁可漏掉一个残留，也不能误杀同 PID 的新进程。
  ⚠ 1.4.0 修复：比对必须带 **1ms 容差**（`IsSameProcessStart`，有单测）。WMI 的
  `CreationDate` 是 DMTF 微秒精度、`Process.StartTime` 是 100ns 精度，同一进程的
  两个读数恒差 0–0.9 µs——严格相等在实测样本里只命中约 1/7，旧写法让「停止」
  大概率空转且不报错。改回严格相等前先看 `ProcessStartToleranceTests`。
  ⚠ 1.4.1 再修：**WMI 快照为空不再静默空转**。`GetProcessRecords` 失败时返回的是
  **空字典**而不是抛异常，于是整个杀进程循环一个都杀不掉、界面回到"未运行"、
  日志里一个字都没有——这正是"界面说停了、引擎其实还占着 3080"那种故障。
  现在手里握着活句柄的 `dshProcess` **直接 `Kill`**：这个 `Process` 对象是我们自己
  `Start` 出来的，内核句柄一直指着那个进程，既不会被 PID 复用骗到、也不需要 WMI 快照
  来证明同一性，所以它不必等快照里有对应条目。快照为空时另记一条启动日志——
  这条路径必须有痕迹。
- **semver 预发布门槛**（1.4.0）：caret/tilde 范围只放行与基准同
  `major.minor.patch` 三元组的预发布候选（npm 规则，`PrereleaseAllowedInRange`）。
  此前 `0.1.5-rc.9` 会被判满足 `^0.1.0`——升级护栏在**漏报警**的方向出错。
  1.4.3 把这道门槛补齐到**裸比较器**上（npm 同一条规则的集合级形态，
  `PrereleaseAdmittedByComparatorSet`）：候选带预发布时，一个比较器集合只有在
  "集合里至少有一个比较器的基准带预发布且与候选同三元组"时才放行——此前
  `0.3.0-rc.1` 会被判满足 `>=0.2.0`（npm 实际会拒绝安装，peer 不满足）。
  裁决必须放在比较器**集合**层面（SatisfiesRange）：逐 token 各自设门槛会把
  `>=0.1.7-rc.1 <0.3.0-0` 配 `0.1.9` 这类"集合里有同三元组预发布基准"的合法
  组合误判成不满足，凭空多警告。
  范围 token 里基准本身带预发布标识时（`^0.1.5-rc.1`），对**不低于基准**的候选维持旧语义、
  返回 null"未能判定"；低于基准的候选仍返回明确的 false——那是一条确定的"不满足"，
  把它降级成"未知"恰恰是护栏漏报的方向。caret 与 tilde 两条分支对称（此前只有 caret 有这道守卫）。
- **引擎日志截断保留尾部**：每次真启动时把 `engine-stdio.log` 截断到末尾 8 MB
  （在第一个完整换行符处切开，避免截断多字节 UTF-8 字符），日志体量从此有界。
  截断本身走后台线程——超过 8 MB 时那是整整 8 MB 的同步读写，留在 UI 线程上是一次
  可感知的停顿（同「递归删除与日志截断一律后台线程」那条）。
- **引擎日志里的认证 token 落盘前脱敏（1.4.2）**：`web-url.txt` 的 token 走 DPAPI 加密，
  但同一个 token 也被引擎打进 `engine-stdio.log` 明文落盘、且跨启动长期保留——加密就被
  同目录这个文件绕开了。现在每次真启动清理日志时，把历史日志里的 `?token=…` 统一改写成
  `<redacted>` 再落盘；盘上只会留有当前会话正在用的那一个，重启即被清掉。
  当前会话的行不受影响（tail 读取路径必须看到明文才能捕获认证链接），
  文件变小（≤8 MB）时同样执行清理——旧 token 不再一直躺在盘上。
- **引擎日志重启不回放（1.4.1）**：tail 游标改为从截断后的文件末尾起步（此前从 0 读，
  而 cmd 是 `>>` 追加——第二次起的「重启」会把历史日志整体回放：浏览器弹出旧会话的
  死 token 链接、启动等待循环在引擎就绪前就提前判"成功"），且分发处**逐行**核对代际
  令牌、令牌 volatile——换代瞬间正卡在读取中途的旧循环，攥着上一会话的 token 行也
  一行不发。三处守卫（循环顶 ×2、逐行分发 ×1）都走同一个 `TailGenerationAlive`，
  不能各写各的——多写一次就多一处能被"顺手改坏"的地方。
  ⚠ 1.4.1 再修：**杀引擎后令牌也要退役**（`RetireEngineTail`，两条杀进程路径——点「停止」
  与关窗清扫——都调）。此前只有"新引擎起跑"才换令牌，于是"杀掉引擎"到"新引擎起跑"之间的
  那段窗口里，旧 tail 循环的退场排空（最多 4×150 ms）仍拿着**当前**令牌通过逐行守卫：
  刚被删掉的 `web-url.txt` 被重写成过期 token、`authenticatedUrl` 复活，紧接着的等待循环
  （`authenticatedUrl is not null → return`）在新引擎还没输出任何日志时就提前判"启动成功"。
  令牌只增不减、从不复用，所以"退役"就是加一：旧循环下一次读就发现自己过期。
  旧的「每次重新拉起时重开」说法（README/注释）从未实现——实际发生的是
  "截断到末尾 8 MB"，现已按实际行为改写。
   ⚠ 1.4.3 再修两处残余：① **写入前二次核对代际**——逐行守卫通过之后、
   `HandleProcessLine` 真正写 `authenticatedUrl`/`web-url.txt` 之前，tail 线程
   可能被调度延迟到「杀引擎 → 退役令牌 → 清链接」之后，把刚清掉的死 token 又写
   回去、让重启等待循环在新引擎起跑前误判"成功"（正是上面那个复活形状剩下的
   check-then-act 缝）；现在写入块与 OpenBrowser 回调各有一次 `TailGenerationAlive`
   复核。② **认证链接按端口归属认领**——引擎固定以 `--port 3080` 拉起，日志里
   指向其他端口的 token URL（第三方插件的输出等）不再被当成认证链接，与探针
   同一失手方向。同轮顺手把「最后输出」摘要缓冲（recentOutput）改成随引擎换代
   清空：新引擎零输出即崩时，弹窗里不再躺着上一代引擎的日志行。

- **杀进程匹配收窄到"引擎入口"（1.4.3）**：两条杀进程路径（「停止」与关窗清扫）此前
  对"命令行含引擎目录"的目录子串**对任意进程名**生效——用编辑器打开 bin.js 这么普通的
  动作，就会让该编辑器在「停止」/关窗时被连树带孙杀掉。现在匹配必须同时核对
  **进程形态**（node / cmd / npx，两条路径共用 `IsEngineProcessShape`）与
  **引擎内的 dsh 包目录**（`EnginePackageDirUnder`，比"引擎目录"更精确一档的锚点）。
  失手方向仍是"宁可漏杀"：真残留占着端口有启动前报错兜底；而引擎本体（node + cmd
  包装层）的命令行必然同时满足两个条件，不会漏。测试两侧各钉了编辑器与
  "node 跑用户自己放在引擎目录下的脚本"两条误伤面用例。
- **恢复前留底失败即中止（1.4.3）**：见「配置恢复如实报告部分失败」那条的更新。
- **断链预检按 pnpm 语义解析 link: 相对路径（1.4.3）**：相对目标以 **profile 包目录**
  为基准（`ResolveLinkTarget`，有单测），不再按启动器的当前工作目录——有效的
  `link:../plugin` 不再被误判成断链、整轮插件更新被跳过。
- **版本切换归档失败不再删除副本（1.4.3）**：把换下的活动引擎归档进版本槽失败、
  挪进 broken- 槽也失败时，此前会 `ForceDeleteDirectory(engine.tmp)`——删掉的恰恰是
  切换前的完整引擎（新版本此刻已顶上成功），等于把唯一回退副本丢掉。现在原样保留
  并在对话框里报告位置（标题"切换完成（有警告）"，以
  `ActivateSwitchedWithWarningPrefix` 标记区分成功与失败——切换本体确实成功了）；
  「环境」检测会提示 engine.tmp 残留，下次安装引擎时才会被清理。

## 文件说明

- `HarnessForm.cs`：主界面、进程管理、端口检测、认证 URL 捕获（engine-stdio.log 增量 tail）、
  引擎安装与升级、插件兼容性检查、配置备份恢复。
- `EngineVersionsForm.cs`：引擎版本管理窗口（列表 / 切换 / 删除）。列举与删除都放后台线程，
  窗口因此能在遍历 2.5 万个 `node_modules` 文件时保持响应——这条纪律同样要求启动器自己那几处
  递归删除与日志截断不许挂 UI 线程。关闭确认只在 `CloseReason.UserClosing` 时弹，
  免得关机时被模态框挡成"此应用阻止关机"。
- `FoldersForm.cs`：相关目录一览窗口。关闭确认与上面同一条纪律。
- `DpapiFile.cs`：DPAPI（CurrentUser 作用域）文件加密/解密。认证链接（`web-url.txt`）含完整
  token，明文落盘时任何能读 `%LOCALAPPDATA%` 的进程都能拿到；加密后只有同一 Windows 用户
  能解开。旧版明文文件读取时自动升级为加密格式，用户无感。DPAPI 不可用（企业策略禁用等）
  时降级写明文——暴露面回到旧版，好过"链接无法持久化"让引擎复用静默失效。
- `Swallow.cs`：低频路径异常统一日志（`Swallow.Quiet(ex, context)`）。同一异常源
  每小时只写一条 startup-log，磁盘满/权限消失等系统性问题不再完全静默；
  高频路径（刷新、tail 循环）直接用普通 `catch { }`（本身就是零开销，
  无需包装函数——曾经有个无调用点的 `Silent()` 占位，已删）。
- `ConfigBackup.cs`：配置快照（启动前与升级前自动拍摄、按文件哈希去重、一键恢复）。
  快照判定（`NeedsSnapshot`）与恢复路径守卫（`IsWithinRoot`）是纯函数，有单测。
  `engine.old` 的归档不在这个窗口里做——那是**写操作**，而列举版本是只读操作，
  挂在只读路径上会让两个并发的后台扫描同时对同一个 `engine.<版本>` 槽"删除 + 改名"。
  归档因此移到了点「启动/重启」的主流程里（而不是引擎安装那条支路上：
  引擎随启动器存活时启动流程会走复用分支直接返回，挂在支路上等于形同虚设）。
  跨进程互斥靠"同卷目录改名是原子的"：先 `engine.old` → `engine.migrating` 认领，
  抢到的人才做删除+归档。原先两个会话的启动器会各自删一次同名槽，交错起来会把
  对方刚归档好的那份一起删掉。
  ⚠ 1.4.1 再修：**收尾路（`ArchiveMigratingLeftover`）原先根本没有认领**，直接对
  `engine.migrating` 做「删同名槽 + Move」——而收尾有两个执行者（点「启动」那次与每小时
  那次定时归档）。T1 归档成功后 T2 的 `ForceDeleteDirectory(slot)` 正好把**刚归档好的那一槽**
  整棵删掉，自己的 Move 再因源已不在而失败被吞，`engine.old` 与 `engine.migrating` 双双消失，
  上一版本就此丢失：这等于把上面那条认领刚修死的事故在收尾路上重新开了一条缝。
  现在收尾路先把 `engine.migrating` 原子改名成 `engine.migrating.<8 位十六进制>`
  （`ClaimMigratingDir`）抢到独占权再归档，抢不到就空转一轮；同进程再加一把 `lock`，
  因为点「启动」那轮（`Task.Run` 后不等待）与定时那轮（`_ =` 丢出去）本来就可能并跑。
  认领副本的代价与 `engine.migrating` 完全一样，所以一并处理：版本槽列举**排除**它
  （`IsEngineSlotName`，否则界面上会冒出一个叫 `engine.migrating.1a2b3c4d` 的怪条目）、
  `RecoverEngineSwap` 也把它当恢复源（进程死在"认领之后、归档之前"时它就是上一版本，
  不认就只剩重装 214 MB 一条路；1.4.3 起把 `engine.tmp` 也列为**最后**恢复源——进程死在
  「切换版本」两次改名之间时它就是完整的上一活动版本，只认验过完整的，半截安装残骸
  照旧交还重装路径清理）。而陈旧的认领目录**不删**：`RestoreStaleClaims` 把创建
  超过 30 分钟的那份挪回 `engine.migrating` 走正常归档（仍可能被别人持有的用创建时间挡开，
  归档是秒级动作）——它里面装的就是可回退副本，删掉等于丢版本。
  ⚠ 1.4.3 再修两处：① **认领直接落私有名**——`engine.old` 的认领不再借道共享的
  `engine.migrating`，而是直接原子改名成 `engine.migrating.<8 位十六进制>`：共享名会被
  另一会话的收尾路当"无主残留"无条件抢走，抢的人不知道本进程正拿着它删槽/搬移，最坏
  交错会把对方刚填好的槽再整棵删掉（三个目录全部消失）；私有名只有认领者自己知道落点。
  ② **认领改名后立刻刷新目录创建时间**——NTFS 同卷改名**保留**创建时间（=当初装引擎的
  时刻），不改写的话每个刚创建的活认领都会立刻被判"陈旧"、可能被另一会话挪走，
  30 分钟守卫此前从未真正生效过。
- `LayoutDump.cs`：布局自检（`DSH_LAYOUT_DUMP=1` / `DSH_LAYOUT_TEST=1` 时把真实几何写入 `layout-dump.txt`）。
  自检只在 `Program.cs` 的 `DSH_LAYOUT_TEST=1` 入口里驱动，两个对话框不再各自挂 `Shown` 处理器。
- `Program.cs`：程序入口、单实例互斥、启动异常兜底（写 `crash-log.txt`）。
- `app.manifest`：应用清单（asInvoker 不提权、supportedOS 声明、长路径感知）。
  DPI 感知不在清单里声明——它由 csproj 的 `ApplicationHighDpiMode` 给出（取值 `SystemAware`），
  与 `UseWindowsForms` 生成的 `ApplicationConfiguration.Initialize()` 保持单一来源。
- `DeepSeekHarness.csproj`：.NET 8 构建配置（发布参数已内置）。开着
  `TreatWarningsAsErrors`（理由见「构建」）并带 `VerifyManifestAssemblyVersion` 目标
  （版本号一致性，见「更新启动器本体」第 1 步）。含 `InternalsVisibleTo`：
  只对配套测试工程开放几个 internal 纯函数；同时用 `DefaultItemExcludes` 把整个 `tests\`
  从默认通配里摘掉（只挡 `.cs` 的话，测试工程的 bin/obj 产物仍会被逐个求值，
  将来谁在 `tests\` 下放个 `.resx` 还会被编进启动器资源）。
- `tests\DeepSeekHarness.Tests\`：xunit 单测（248 条）。刻意只覆盖"判错了不报错"的决策：
  两条杀进程路径（点「停止」与关窗清扫）、PID 复用 StartTime 容差、端口收窄、快照判定、
  恢复路径守卫、版本号白名单、版本归档的规划顺序、semver 范围判定（含预发布门槛——
  caret/tilde 的基准三元组门槛与 1.4.3 补上的比较器集合级门槛、build 段连字符不是
  预发布标识）、
  引擎换代 tail 游标的起点钳制（1.4.1，防"重启回放旧 token 行"）、tail 代际令牌退役后的
  守卫是否仍拦得住旧循环、版本槽名白名单对认领副本 `engine.migrating.<8 位十六进制>`
  的排除。
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