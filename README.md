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
| 配置快照 | `%LOCALAPPDATA%\DeepSeekHarness\config-backups\`（保留最近 8 份；凭据经 DPAPI 加密成 `.credentials.yaml.dpapi`，但**仍跨凭据轮换留存**，目录勿外传） |
| 崩溃日志 | `%LOCALAPPDATA%\DeepSeekHarness\crash-log.txt`（超过 256 KB 自动截尾） |
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

## 更新启动器本体（标准流程：随时跑 `更新启动器-更新到<版本>.bat`，Web 会话不中断）

每次优化启动器本体都按这个方式更新。1.3.0 起引擎 stdio 已文件化（见「生命周期与单实例」），
强杀启动器不再连带引擎：脚本杀掉旧启动器后，引擎与 3080 上的 Web 会话原样活着，
新实例自动探到 `web-url.txt` 的链接可用 → **复用引擎**、打开浏览器，全程十几秒内完成，
**不需要挑空闲时段**。唯一的例外是**首次**从管道耦合的旧版跨进 1.3.0 的那一次：
当时的旧引擎仍会随断管退出，新实例自动走完整重启（清残留 → 拉引擎 → 新链接开浏览器），
打断约十几秒、历史不丢——此后所有更新都无感。

> **脚本文件名带版本号**：`更新启动器-更新到1.4.3.bat`。资源管理器里常同时躺着好几份
> 历史脚本，功能完全相同、差别只在"装的是哪一版"，内容上无从分辨该双击哪一个——
> 名字上写清楚即可一眼选对。前缀保持不变，是为了让"搜 更新启动器"仍然只命中最新那份。
> 脚本开头还会自查一次文件名与 `TARGET_VERSION` 是否一致，不一致就当场中止，
> 免得装完才发现装的不是自己以为的那一版。

流程五步：

1. **升版本号**（写在**四处**，漏改任何一处都会**直接构建失败**）：
   | 位置 | 写成 |
   |---|---|
   | `DeepSeekHarness.csproj` 的 `<Version>` | `1.4.3`（必须 x.y.z 三段式） |
   | `app.manifest` 的 `assemblyIdentity version` | `1.4.3.0`（Windows 只认四段） |
   | 脚本**文件名** | `更新启动器-更新到1.4.3.bat`（用 `git mv` 改名以保留历史） |
   | 脚本内的 `TARGET_VERSION` | `1.4.3` |

   csproj 的 `VerifyManifestAssemblyVersion` 目标在 `PrepareForBuild` 之前会：拿 `<Version>`
   去清单里找 `assemblyIdentity version="<版本>.0"`；确认新名脚本存在且旧名
   `更新启动器.bat` 已消失；读一遍脚本内容确认有 `set "TARGET_VERSION=<版本>"`。
   任何一条不满足就 `Error`。
   版本号是手工写的，不同步的后果是"exe 属性显示 1.4.3、Windows 看到的文件版本还是 1.4.2"，
   **没有任何地方会报错**——所以干脆让它变成构建失败。
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
4. **双击 `更新启动器-更新到<版本>.bat`，按任意键**。脚本依次：核对脚本名与目标版本 →
   按映像名强杀旧启动器（绝不碰 node）→ 覆盖 `bin\Release\net8.0-windows\win-x64\`
   与脚本旁的桌面副本 → 启动新实例。
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
  自动升级为加密格式，用户无感。
  ⚠ 加密**永不降级**：DPAPI 临时不可用或写入瞬时失败时，宁可保留原有的密文（读侧
  解不开就当"没有链接"，这轮不复用引擎、重新拉一个即可），也绝不把已加密文件静默
  替换成明文——失败原因与内容无关，降级写只会把一次可恢复的失败变成不可撤销的暴露。
- **配置快照里的凭据也加密**：`.credentials.yaml` 在快照中原为明文，而快照默认留 8 份、
  且**跨凭据轮换留存**——轮换 token 是为了吊销旧凭据，把旧密钥原样摊 8 份正是在抵消它。
  现在快照里存的是 `.credentials.yaml.dpapi`（DPAPI CurrentUser），用「恢复配置」按钮
  会自动解开还原；解密失败（换了用户/换了机器）会**跳过并明确报出来**，不会盖一份
  解不开的垃圾进 `$DSH_HOME`。提示不变：**这两个目录勿贴进截图或共享给别人**。
- **`更新启动器-更新到<版本>.bat` SHA256 校验**：把 exe 放进 `update-staging\` 时同时放一个
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
- **busy 期间按 Esc 不会再把整窗锁死**：忙碌态的**所有权标记**（`busyOwner`）与 `startCts`
  刻意分开，`EndBusy` 判的是前者。此前两处 `finally` 用
  `ReferenceEquals(startCts, cts)` 判"我还是这次忙碌态的主人"，而 `StopClickedAsync`
  的忙碌分支第一件事就是 `CancelPendingStart()`——**取消恰恰会把 `startCts` 置 null**，
  于是被取消的操作永远等不到 `EndBusy`；而 `busy` 全文件只有 `EndBusy` 一处复位。
  结果：启动/升级（最长 15 分钟）期间按一次 Esc，全部按钮永久禁用，只能重启启动器。
  ⚠ 注释曾写"它们的 finally 会自行复位"——这句话在代码不成立，正是它把回归掩盖了。
- **升级查版本的三道闸**：`ParseNpmVersionOutput` 现在**只看 stdout**、要求退出码为 0、
  且取到的末行必须是 semver 形态。此前 stdout 与 stderr 拼接取末行，于是三条路都能把垃圾
  喂进来：npm 失败时 stdout 的错误摘要、`npm WARN config …` 之类走 stderr 的警告行、
  以及完全不像版本号的任何文本。垃圾 `latest` 会**先杀掉正在跑的引擎**、再到
  `IsSafeVersionToken` 才失败——精心设计的"保持当前引擎"降级路在网络受限环境基本不可达。
  stderr 现在只进 startup-log。`RunEngineUpgradeAsync` 在停引擎**之前**还多卡一道
  `IsSafeVersionToken`，让任何取版本路径的异常都到不了那一步。
- **半截安装不再被当成能用的引擎**："可用"的判据从"版本可读 + bin.js 在"扩到
  **dsh 声明的直接依赖是否都已落地**（`IsCompleteEngineInstall`）。npm 逐包解包、
  没有事务性，中断留下的"dsh 本体在、依赖缺一半"的目录此前会被 `RecoverEngineSwap`
  提升为活动引擎、被 `EnsureEngineAsync` 判定"已安装"，随后启动永远 `Cannot find module`、
  升级说"已是最新"、版本管理又按"使用中"拒绝删除——UI 内无解的死局。现在它一律不被提升，
  活动引擎若不完整则 `EnsureEngineAsync` 自动重装修复；安装替换前也再验一次 staging，
  半截的绝不顶上，且**不完整**的在位引擎不再占用 `engine.old` 这个"可回退副本"的位置。
- **安装替换也进了 `migrateGate`**：切版本、删槽、归档都串行化同一批目录，唯独安装替换是
  裸奔的。交错时轻则 Move 失败、重则"升级假成功"（把旧引擎顶上去却报"已就绪：新版本"）。
  现在替换三步全程持闸，并在闸内**复查活动版本**——`migrateGate` 只管本进程，跨会话时
  这一道比对才拦得住另一台启动器的改动。
- **升级成功但重启失败时不再谎称"引擎未被改动"**：失败提示改成三态（没换过 / 换过但启动失败 /
  引擎目录不存在）。中间那一态此前按 `Directory.Exists` 判成了第一种，而磁盘上**已经是新版**，
  用户会据此以为一切照旧，新引擎却在下次启动静默生效。
- **用户目录按段比较而不是子串**：`C:\Users\Dan` 是 `C:\Users\Daniel` 的前缀，
  子串写法在同机存在相近账户名时必然误判——而误判方向是"把别人会话的引擎判成本启动器的
  残留并整树杀掉"，这一分支甚至不要求端口匹配。`ClearOrphanProfileLock` 借用的也是这个
  判定，于是同样会永久拒绝对活锁动手。
- **孤儿锁清理改用"可能持锁"判据**：此前借用 `IsHarnessCommand`，而那条判据为了"不误杀"
  明确排除了桌面客户端——可这把锁**恰恰就是客户端的引擎在引导时持有的**。客户端正持锁
  引导时双击本启动器，活锁被删，两个引擎并发写同一份 profile。新判据方向相反：
  宁可多认（漏认 = 删掉活锁，是这里唯一不可逆的错误），代价只是"这轮不清锁"。
- **`TrimLogTail` 不再把整份日志清空**：兜底分支原来写成
  `tail[(tail.IndexOf('\n') + 1)..]`，当尾段里唯一的换行正好落在最后一个字符上
  （也就是日志以 `\n` 收尾的**常规**形态）时切成空串——实测 300 KB 输入 → **0 字节输出**，
  恰好丢掉最需要排查的超长记录与全部历史，且零报错。现在切点必须落在"换行之后还剩内容"上。
- **`DispatchEngineLogText` 的长行护栏补全**：原先只护"从未见换行"那一支，
  于是"换行 + 后面接一大段没有换行的内容"（pnpm 裸 `\r` 进度串的典型形态）
  仍能把 `Remainder` 顶到无界增长。换行**之后**的尾巴现在同样过护栏。
- **退场排空的停止条件改成"没读到新字节"**：原先以"本轮分发行数 == 0"为准，
  而引擎崩溃时最后一行往往**没有结尾换行**——那正是最该进"启动失败"摘要的一行，
  读侧对它返回 0，于是排空在第一轮就 break，报错行永远进不了摘要。
- **登录会话的 dsh 引擎进程会被正确识别**（`MayHoldProfileLock`）：Electron 形态的
  客户端宿主此前完全不在进程匹配的视野里。
- **零散加固**：`AuthTokenRedactRegex` 补 `IgnoreCase`（此前 `AuthUrlRegex` 认得
  `?Token=` 却被脱敏正则放过，同一个 token 会明文落盘）；空白候选项（`">=2.0.0 || "`）
  判为"无法判定"而不是"恒满足"；`CreateSnapshot` 一个文件都没复制成功时返回 null
  （此前返回非 null 会让"留底失败即中止"的防线形同虚设）并清理空目录；`LatestSnapshot`
  只认有 `backup-info.txt` 的完整快照，撕裂快照不再成为"最新一份"；切换/安装入口不再
  无条件删 `engine.tmp`（里面可能是"死在两次改名之间"的唯一旧版副本）；`RecoverEngineSwap`
  遍历认领目录补上 30 分钟新鲜度守卫（与 `RestoreStaleClaims` 对称）；
  `ActivateEngineVersionAsync` 归档前先删同名槽（与 `ArchiveClaimedDir` 同一口径，
  不再产生 `broken-` 怪条目）；能被拼进 cmd 命令行的工具路径拒绝含 `%`（防 `%VAR%` 展开）；
  `IsTcpOpen` 在回环**快速拒绝**时也观察任务异常（最常见的负例此前正是未观察异常退场）；
  按钮无障碍名称提成常量，消除构造 / `ApplyAccessibility` / `EndBusy` 三处真相源打架。
- **恢复动作的纵深防御补上"链接"这一半**：`IsWithinRoot` 只做词法比较，而
  `Path.GetFullPath` **不解析重解析点**——`$DSH_HOME\profiles\web` 本身是个指向别处的
  junction 时，路径字符串完全落在根内、防线放行，实际写入却在根外。源侧同理：快照里
  放一个指向外部的链接文件，枚举出的 `src` 字符串"合法"，读到的却是别处的内容。
  新增 `IsSafeRestoreTarget`（纯函数内核 + 单测）：沿途每一段都不得带
  `ReparsePoint`，源侧与目的侧各查一次。**边界**：只查根**之下**，不查根自己——
  把 `$DSH_HOME` 整个重定向到别的盘是用户的正当选择，那道 junction 正是他要的。
  另：恢复不再把 `.tmp` 残骸带进配置目录（它们是写入中途被杀留下的垃圾）。
- **DPAPI 的降级不再静默**：类头与 README 都承诺"只有同一个 Windows 用户能解开"。
  DPAPI 被企业策略禁用时会降级写明文，而此前**一个字都不留**——用户看到的是一份明文
  token 文件，所有信号都指向"已加密"，威胁模型失效了却完全无从知道。现在每次降级都写
  startup-log，且「环境检测」会把"`web-url.txt` 当前是明文"作为**问题**报出来
  （降级是持续状态，日志里一条不够，用户不会去翻日志）。
- **子窗体的高 DPI 布局是真的**：`AutoScaleMode = AutoScaleMode.Dpi` 在没有
  `AutoScaleDimensions`、也没有 `PerformAutoScale` 的情况下是**空操作**——WinForms 按
  默认 96/96 算缩放因子，120/144 DPI 上得到的仍是 1.0，于是所有布局常量其实全是
  96-DPI 像素。改为 `AutoScaleMode.None` + 自己按 `DeviceDpi` 换算（与主窗体同一纪律，
  一套机制，不与 WinForms 的自动缩放打架），全部常量从 `Px()` 派生。
  按钮宽度缓存也改为**以 DeviceDpi 为键**：构造期句柄还没建、`DeviceDpi` 还报着设计值，
  而 GDI 量文字用的正是设备上下文，125% 下同样一段文字要宽 25%——只按首次结果缓存、
  之后永不重测，高 DPI 上按钮文字会被 `AutoEllipsis` 截掉。
- **极矮窗口下列表不再压到提示上**：原来 `Math.Max(40, …)` 那个下限会**盖过**上限，
  于是"窗口比提示+按钮所需还矮"时列表底边压到提示之下——与两个窗体都写着
  "控件不重叠"的承诺直接矛盾。现在上限是真实可用空间，下限只在它之上生效。
- **快照清单原子写入**：`last-hashes.txt` 是整个去重机制的唯一依据，却是原地覆写。
  写到一半被杀 → 半截内容 → 读侧把没有制表符的行静默丢弃 → 那些键"缺席" →
  `NeedsSnapshot` 每轮都判要拍，8 个快照名额被无意义的快照轮流挤掉。
  现在改用与快照内容、Restore 同一条 `AtomicWrite` 纪律，半截行显式留痕。
  临时文件名改为每次唯一（固定 `.tmp` 在并发写入时会互相 Move）。
- **快照按创建时间排序而不是按名字**：名字里的时间戳是**本地时钟**，NTP 回拨 / 夏令时
  跳变 / 手动改时间都会让排序与真实先后脱钩——于是 `LatestSnapshot` 可能挑中更旧的一份
  去恢复，`PruneOldSnapshots` 可能把刚拍的那份删掉。改用 `CreationTimeUtc` 主排序、
  目录名作同刻兜底。
- **版本管理的互斥与忙碌态**：`migrateGate` 本来就同时罩住切版本 / 删槽 / 归档 /
  安装替换——UI 的 `busy` 只是防重入，**不是**并发互斥（`ConfirmClose` 允许关窗后台
  继续跑，新旧两个对话框实例的 `busy` 互不相识）。这一点补进了注释，并补上了真正的
  缺口：**版本列举此前不进闸**，会与"递归删 2.5 万文件"交错，读到撕裂的列表
  （条目凭空消失 / 大小只统计到一半）。`ActivateSelectedCore` 与 `DeleteSelectedCore`
  的 `busy` 复位改为 `try/finally`，与 `ReloadAsync` 同一纪律。
- **切换/删除的失败提示不再被刷新抹掉**：原先先设 `hint.Text` 再 `await ReloadAsync()`，
  而刷新自己会把 hint 改成"正在读取…"——用户看到列表刷了一下，错误却消失得无影无踪。
  现在文案排在刷新**之后**再设。异常也按类型 + 堆栈留痕（只留 `Message` 的话，
  "Cannot find a directory" 与"拒绝访问"在这里长得一模一样）。
- **「相关目录」不再把无权限当成不存在**：`File.Exists` / `Directory.Exists` 对
  访问被拒一律返回 false，与"不存在"完全无法区分。权限被撤、Defender 锁住、
  OneDrive 占位符没下载这类**最需要排查**的位置，恰恰从这个"排查入口"里消失了。
  现在对 Exists 返回 false 的候选项再用会抛的 `GetAttributes` 复核一遍——
  "存在但读不了"照样列出来（说明列里点明），提示行也报总数。
- **剪贴板**：重试条件补上 `ThreadStateException`（当前线程不是 STA）。只认
  `ExternalException` 时它会落进兜底 catch，弹出"内容超出限制"这种与真实原因
  毫无关系的话，把用户引向错误的排查方向。失败提示也因此分成两种说法。
  「复制路径」的按钮恢复统一走 `UpdateButtons`，不再"无条件下 enable"。
- **单实例**：拿不到互斥体时留痕（此前静默放行，单实例保护失效却一个字都没有）；
  释放失败也留痕（跨线程 `ReleaseMutex` 抛的 `ApplicationException` 曾被直接吞掉）。
  激活已有窗口时**核对可执行文件路径**（只按进程名会把同名的另一个程序的窗口提到前台），
  且 3 秒内没提起来会弹一句说明——原来静默退出，用户的体感与"程序崩了"完全一致。
- **崩溃日志**：三个异常钩子会并发写同一个文件，加锁；截断复用 `TrimLogTail`
  （原实现对"单条记录本身就超限"无效——`head >= 0` 几乎恒成立，于是每一轮都要把整份
  文件读进来拼一次写一次、而长度不减，一个反复抛的大异常会把日志目录先撑爆、
  死因反而最先被挤出去）。
- **`FormatSize`**：不足 1 MB 的版本槽原先一律显示 "0 MB"，看起来像"这个版本是空的"、
  实则只是没统计到。1 MB 以下给一位小数。
- **项目结构**：`HarnessForm` 原先是**一个 5000+ 行的文件**，把界面、进程管理、引擎安装升级、
  semver 判定、插件更新、环境检测全塞在一起——改任何一处都要在几千行里定位，
  "这个判据属于哪块"只能靠猜。现按它内部原有的 `// ---- 分段 ----` 分隔线拆成
  **10 个 `partial class` 文件**（见「文件说明」的对照表）。**拆分只搬位置、
  不改任何一行成员代码**——拆前拆后 267 条成员/段标记逐行比对一致，
  构建 0 警告 0 错误、391 条测试全绿、布局自检 8 种尺寸 0 重叠。跨段字段
  （`startCts` / `busyOwner` / `dshProcess` / `engineLogCursor`）刻意留在主文件里，
  让"这个状态由谁写"有个明确的家。
- **更新脚本文件名带版本号**：`更新启动器-更新到1.4.3.bat`。资源管理器里常同时躺着
  好几份历史脚本，功能完全相同、差别只在"装的是哪一版"，内容上无从分辨该双击哪一个。
  前缀保持不变是为了让"搜 更新启动器"仍只命中最新那份。脚本开头自查文件名与
  `TARGET_VERSION` 是否一致（不一致就当场中止），csproj 也对照 `<Version>` 校验文件名
  并**拒绝旧名残留**——忘了改名是构建失败，不再是"双击了装错版本"。
- **WMI 部分快照不再被当全量缓存**：枚举半途抛异常时，此前那一**半截**结果照样被缓存
  1 秒并当"全量进程表"用——停止/清扫会漏掉没被枚举到的残留（静默漏杀、无痕），
  孤儿锁清理会把"没看到持锁者"读成"没人持锁"。空快照本来就有守卫（跳过并留痕），
  半截快照却绕过了它（`Count != 0`）。现在**只有枚举完整才发布缓存**；不缓存的代价只是
  下次调用重跑一次 WMI（本机约 140 ms），与"漏杀 + 误删锁"不可比。逐条 try 让一条坏记录
  不再让整份快照作废。
- **进程快照缓存的内存序**：`processRecordCacheAt` 从 `DateTime` 改成 **UTC ticks（long）**。
  那个字段是跨线程读的（关窗清扫在 UI 线程、停止与锁清理在后台线程），而 `DateTime`
  是 8 字节结构、读写**不保证原子**，撕裂读会得出一个既不是旧值也不是新值的时刻；
  `Volatile.Read/Write` 又只接受引用类型。两头都够不着时唯一的正确做法就是拆成 long
  用 `Interlocked`——与本项目对 `lastInstallInfoAtTicks` 已确立的纪律一致。
- **扫杀循环不再全静默**：`catch { }` 改为留痕；`CreationDate` 缺失（回退成
  `DateTime.MinValue`）时那个进程原本**永远杀不掉、且无任何痕迹**，现在跳过并留痕；
  PID 复用导致的跳过也留痕——否则"「停止」似乎没起作用"无从排查。
- **tail 游标在提交任务时捕获**：原先是在 `EngineTailLoopAsync` 函数体第一行
  `var cursor = engineLogCursor`，而那行跑到线程池线程上才执行。线程池饥饿叠加
  "这几毫秒里又完成了一次完整启动"时，上一代循环会读到**新一代**的游标，两代共享同一个
  `LogCursor`、`Pos += len` 与 `Remainder` 被两个线程同时改写——而这两处**不受代际令牌
  守卫约束**（守卫只管"要不要分发这一行"）。现在游标作为参数显式传入。
- **排空后把无结尾换行的残余喂出去**：崩溃的最后一句（最可能是报错行）此前永远留在
  `Remainder` 里没人管。排空结束后补一次 flush，守卫照旧。
- **`engineTailToken` 的两处自增统一成 `Interlocked.Increment`**（原先一处 `++`、
  一处 `Interlocked`）。当前都在 UI 线程无实竞态，但"这次没出事"不等于纪律满足。
- **junction 不再穿透两处递归遍历**：`DirectorySizeBytes` 与 `ClearReadOnlyAttributes`
  的 `EnumerateFiles` 默认**跟随** junction。两个后果：指向引擎目录外部的链接会让前者
  把外部文件的大小算进来、后者**改掉外部文件的只读属性**；而**环形** junction
  （a → 父目录 → a）会让枚举永不终止——挂在后台线程上的死循环，busy 随之永不解除。
  两处都加了 `AttributesToSkip |= ReparsePoint`，与 ConfigBackup 的同款防护对齐。
- **`TryReadUrlFile` 补上端口归属校验**：`HandleProcessLine` 捕获引擎日志里的 URL 时
  强制 `urlPort == DefaultPort`，而读回路径只跑了 `AuthUrlRegex`（接受任意端口）。
  不补的后果很具体：文件里躺着一个指向别的端口的认证链接时，它会被当候选，
  探测通过后**把 token 作为 query 发给那台不相干的服务**。
- **`TruncateEngineLog` 读满 + 原子回写**：`FileStream.Read` 的契约允许短读，
  按单次结果算就会**静默把尾部截短**——而尾部恰是最新、最该留住的行；回写原先是
  `File.WriteAllBytes` 原地覆写，写到一半被杀就留下半截引擎日志（而它正是崩溃后唯一的现场）。
  两处都改成循环读满 + `tmp`+`Move`，与 `DpapiFile.WriteAtomic` 同一纪律。
- **`ClearOrphanProfileLock` 补 `IsPathFullyQualified` 守卫**：`USERPROFILE` 为空时
  `lockPath` 会落到相对路径，可能命中 exe 目录下的同名文件并把它删掉——与 `LocalAppDir`
  （解析不到直接 throw）、`ConfigBackup` 的同款护栏统一口径。
- **探针的 body 改为异步读**：`BodyContainsAsync` 原先在 UI 线程上同步 `reader.Read`，
  而它挂在 1.5 秒一轮的状态刷新上——3080 上坐着"慢发 body"或"body 很大"的不相干服务时，
  界面每 1.5 秒被同步阻塞最长约 2 秒。改 `ReadAsync` + `ConfigureAwait(false)`
  （只换 Read 不改上下文捕获，阻塞只是挪到下一帧，等于没修）。
- **关窗的残留引擎清扫后台化 + 限时**：`dshProcess == null` 的复用路径（1.3.0 起的常态）
  原先在关窗的 UI 线程上同步跑全量 WMI，与该方法"这里必须快"的意图直接矛盾。
  现在后台跑、限时 2 秒等：正常机器照旧瞬间消失，受损机器则"让窗口消失"优先。
- **`SatisfiesSingle` 的预发布判定改用 `VersionPrerelease`**：原先 `basis.Contains('-')`
  会把 **build 元数据**里的连字符当成预发布——`^1.2.3+b-1` 因此恒为"无法判定"，
  而 npm 对它有明确答案。方向安全（不误判成满足），但白白丢能力，报出来的还是
  "未能判定"这种用户没法排查的话。
- **`FindBrokenLinkDeps` 扫全三段**：此前只扫 `dependencies`，于是 `devDependencies`
  里的断链照样让 `pnpm update` 整体失败，而预检说"没发现问题"。
- **registry 的闸门设在拼接点**（`GuardRegistryForCommandLine`）：靠"三个调用方都记得
  先校验"是纪律不是结构。闸门必须在字符串被塞进 `cmd /d /s /c "npm … --registry …"` 的那一步。
- **`UpdatePluginsAsync` 在 `process.Start()` 前补取消检查点**（解析 Node 的 OCE 被
  有意吞掉后，取消态会一路走到启动）。
- **定时归档的异常观察**：`_ = Task.Run(MigrateEngineOldToSlot)` 之前无人 await，
  抛出去的异常会在 GC 时触发未观察异常处理器、写成一条与崩溃无关的 crash-log。
- **摘要"是否为空"改用结构化标志**：`summary.Contains("未输出任何日志")` 把措辞和语义
  绑死了——改一次文案那个分支就静默失效，且没有任何测试会发现。改为返回 `(文本, 空?)`。
- **`ConfigBackup.Restore` 端到端测试**（这是全套件唯一一条"覆盖写用户配置"的路径，
  此前**零测试**）：拆出可测内核 `RestoreInto(snapshotDir, dshHome)`——生产入口
  `Restore` 就是 `RestoreInto(snapshotDir, DshHome)`，语义一字未变，而整条链
  （相对路径怎么算、快照文件名怎么落回 `$DSH_HOME`、`backup-info.txt` / `.tmp` 会不会被
  当配置盖进去、失败怎么记账、凭据密文能不能解开还原）都能在临时目录里钉住，
  **测试绝不碰开发者自己的 `~/.dsh`**。⚠ 这组测试上线当天就抓到一个真 bug：
  新加的 `IsSafeRestoreTarget` 对**尚不存在**的目的文件读属性会抛，于是**每一件都恢复不了**——
  这只有端到端测试看得见，注入假探针的纯函数测试永远发现不了。
- **进程匹配的排除项各自独立钉住**：`dsh-desktop-host` 与 `app.asar` 此前只有一条
  测试，而那条命令行**同时含两者**——把两条排除整行删掉，套件照样全绿。这违反
  套件自述的"删掉任何排除项必须红"。现在各补一条只含其中一项的用例，
  并加了反向钉住（去掉该标识后**必须命中**，否则说明那条是靠别的排除项蒙对的）。
- **测试套件不再被 UI 静态状态绑架**（M-4）：`HarnessForm` 的类型初始化器里原本有
  三个 `Font` 构造（要过 GDI+）与 `LOCALAPPDATA` 解析（可能抛）。而类型初始化器会在
  **任何**静态成员首次被访问时执行——哪怕那个成员只是 `ParseVersion` 这样一个纯字符串
  函数。于是无 GUI 的 Windows Server Core / 容器 CI 上，整套测试会被一条**与被测逻辑
  毫无关系**的 `TypeInitializationException` 炸成全红。现在 `LocalAppDir`、三个字体、
  以及链在 `LocalAppDir` 上的五个路径字段全部改成惰性属性；抛异常的语义一字未变，
  只是从"类型首次加载时"推迟到"这条路径首次使用时"。新增 `StaticCouplingTests`
  断言这些名字**不再是静态字段**（已反向验证：把 `UiFont` 改回 `static readonly`
  恰好 1 条转红）。
- **`InvariantGlobalization`**：整套套件的存活不该取决于测试机上装了什么语言包。
  .NET 默认启用 ICU，而 ICU 的解析按当前 culture——区域设置成了测试结果的输入。
- **WMI `CreationDate` 注释订正（实测）**：它**不是 UTC**。`ManagementDateTimeConverter
  .ToDateTime` 返回 `Kind=Unspecified` 的**本地挂钟时间**（DMTF 串尾部带 `+480`
  这类本地偏移，本机实测样本 `"20261002101841.683036+480"`），另一侧
  `Process.StartTime` 是 `Kind=Local`——两边 Ticks 可比靠的是"都是本地挂钟"。
  原注释写成"Utc Kind"是错的，而按它去"修正"的人会补一句 `ToUniversalTime()`，
  那会把差值推到 8 小时量级，让**每一个**真正目标的 StartTime 比对都失败——
  「停止」静默空转。两处注释已订正，并补了反向用例。
- **npm/pnpm 的输出排干**：`OutputDataReceived` 是**异步**投递的，进程退出不等于管道里
  剩下的行已经派发完。此前在 `WaitForExitAsync(token)` 之后立刻取摘要，失败时最容易丢的
  **npm error 那一行**往往不在里面——用户拿到的是一段不完整的报错上下文。
  两处（安装引擎、更新插件）都补了"退出了再无超时等一次"。
  （审查原报告把它指在 `GetLatestEngineVersionAsync`，那里用的是 `ReadToEndAsync`
  且**本就**在读 ExitCode 前显式排干过——前提不对，竞态实际在另外两处。）
- **升级 warning 路径不再谎称"完全成功"**：旧版本归档失败而挪进 `broken-` 槽时，
  此前直接 `return null`，用户看到的是"切换完成"，而「版本管理」里那个他记得的旧版本
  条目不见了、取而代之的是一个看着像垃圾的名字——真相只在 startup-log 的一行里。
  现在返回带警告前缀的文案并报出**真实落点**（`BrokenSlotDirFor()` 带时间戳，
  必须在 Move 前取一次复用，否则提示里会指向另一个不存在的目录）。
- **「目录」窗口两处纪律对齐**：空路径护栏从 `IsNullOrWhiteSpace` 改成
  `IsPathFullyQualified`（与 LocalAppDir / ConfigBackup / 孤儿锁三处统一；且它是唯一一处
  还要拿结果去**开文件**的）；打开前临用前复核存在性——`/select` 分支的
  `Process.Start` 只要 explorer 起来就返回成功，路径没了会静默打开空窗口，
  而目录分支会抛、用户看得到报错，两个分支的反馈此前完全不对称。
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

### `HarnessForm` 的分段

主窗体原先是**一个 5000+ 行的文件**，把界面、进程管理、引擎安装升级、semver 判定、
插件更新、环境检测全塞在一起——任何一次改动都要在几千行里定位，而"这个判据属于哪块"
只能靠猜。现按它内部原有的 `// ---- 分段 ----` 分隔线拆成 10 个 `partial class` 文件。
**拆分只搬位置、不改任何一行成员代码**（拆前拆后 267 条成员/段标记逐行比对一致），
partial 之间共享全部字段与成员，行为完全不变。

| 文件 | 内容 | 大致行数 |
|---|---|---|
| `HarnessForm.cs` | 字段、常量、静态资源（字体/正则/HttpClient）、构造与底部按钮行布局、`EnterBusy`/`EndBusy`、无障碍 | ~470 |
| `HarnessForm.Lifecycle.cs` | 启动 / 重启 / 停止，引擎 stdout 的 tail 读取与逐行分发 | ~635 |
| `HarnessForm.Status.cs` | 状态刷新（1.5 秒轮询）与端口/身份探针 | ~186 |
| `HarnessForm.Processes.cs` | WMI 进程快照、残留进程匹配、停止与退出清扫、孤儿锁清理 | ~856 |
| `HarnessForm.Engine.cs` | 引擎安装与升级，含「切换到此版本」 | ~532 |
| `HarnessForm.EngineSlots.cs` | 版本槽、`engine.old` 认领协议、归档与崩溃恢复 | ~767 |
| `HarnessForm.Semver.cs` | 插件兼容性检查与 semver 判定 | ~756 |
| `HarnessForm.Plugins.cs` | 插件自动更新（pnpm / corepack） | ~366 |
| `HarnessForm.EnvCheck.cs` | 「环境」检测报告 | ~525 |
| `HarnessForm.Links.cs` | 认证链接读写、引擎日志截尾与脱敏 | ~239 |

改代码时**落在语义所属的那一段里**；跨段的字段（`startCts` / `busyOwner` / `dshProcess`
/ `engineLogCursor` 等）刻意留在 `HarnessForm.cs`，让"这个状态由谁写"有个明确的家。

### 其余文件

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
- `tests\DeepSeekHarness.Tests\`：xunit 单测（425 条）。刻意只覆盖"判错了不报错"的决策：
  两条杀进程路径（点「停止」与关窗清扫）、**两条排除项各自独立钉住**（`app.asar` 与
  `dsh-desktop-host` 此前共用一条测试、删掉任一条都全绿）、PID 复用 StartTime 容差、
  端口收窄、引擎包目录精确匹配**早于**端口收窄这条既定语义的显式钉住、用户目录
  **段级**比较（前缀绕过：Dan / Daniel）、孤儿锁的"可能持锁"判据（**含桌面客户端**，
  与"杀进程"判据故意相反）、快照判定、恢复路径守卫、**`ConfigBackup.RestoreInto`
  的端到端测试**（临时目录，绝不碰真实 `~/.dsh`）、恢复路径的**链接/联接点**防线
  （含"该段尚不存在不等于不安全"这条——它是恢复路径能否工作的前提）、版本号白名单、
  版本归档的规划顺序、semver 范围判定（含预发布门槛——caret/tilde 的基准三元组门槛与
  1.4.3 补上的比较器集合级门槛、build 段连字符不是预发布标识、**基准的 build 段不算预发布**、
  **`>` / `<=` / `=` 三个比较器各有可分辨边界**——op 映射互换时它们是仅有的护栏）、
  **semver 地基的直接测试**（`ParseVersion` / `CompareVersionStrings` /
  `PrereleaseAllowedInRange` / `PrereleaseAdmittedByComparatorSet` / `EnginePackageDirUnder`
  ——这五个 internal 函数的注释都写着"要被单测钉住"，此前却只有经上层的间接覆盖；
  它们的注释里还留着"条件永真""预发布无条件比较"两处历史 bug）、
  npm 查版本输出的三道闸（退出码 / 只认 stdout / semver 形态）、日志截断
  **不得把整份日志清空**、半截安装的依赖完整性、cmd 工具路径不得含 `%VAR%`、
  空白候选项判不出来、版本大小显示（小于 1 MB 不再是 "0 MB"）、
  引擎换代 tail 游标的起点钳制（1.4.1，防"重启回放旧 token 行"）、tail 代际令牌退役后的
  守卫是否仍拦得住旧循环、版本槽名白名单对认领副本 `engine.migrating.<8 位十六进制>`
  的排除。
- `StaticCouplingTests`：断言 `HarnessForm` 的三个字体与八个数据目录路径**不再是静态字段**。
  这类断言看着别扭——`TypeInitializationException` 有什么可测的？但它是整套套件能不能在
  无 GUI 机器上跑的前提（见上「测试套件不再被 UI 静态状态绑架」），不是"某输入该得某输出"，
  只能用反射直接断言类型形状。已反向验证：把 `UiFont` 改回 `static readonly`，
  恰好 1 条转红。
  这几处的共同点是错了不会有任何报错，只在用户眼前发生——所以必须有测试钉住。
  几条用例是**专门为了让别的用例变红**而存在的，例如端口收窄那条：把它整行删掉，
  必须有用例失败，否则说明它压根没被测住。
  两条杀进程路径曾分叉过一次（空 `engineDir` 护栏只补在了一条上，`Contains("")`
  恒为真会让关窗时把所有进程整树杀掉）——所以**改一条时记得连另一条一起看**。
- `更新启动器-更新到<版本>.bat`：标准更新入口，随时可执行（1.3.0 起 Web 会话不中断；
  首次迁移例外见「更新启动器本体」）。**文件名末尾标明目标版本**——资源管理器里同时
  躺着好几份历史脚本时功能完全相同、只有版本不同，名字是唯一的分辨依据；前缀固定，
  "搜 更新启动器"仍只命中最新那份。脚本开头自查文件名与 `TARGET_VERSION` 是否一致，
  csproj 也对照 `<Version>` 校验文件名（并拒绝旧名残留）。
  按映像名只杀启动器（不碰 node）、
  从 `%LOCALAPPDATA%\DeepSeekHarness\update-staging\` 取新 exe（有 `.sha256` 则校验）、
  覆盖 `bin\Release` 与脚本旁副本、拉起新实例（自动复用仍在运行的引擎；
  复用不可用时自动重拉并打开新认证链接）。
- `DeepSeekHarness.exe`：构建产物，约 155 MB，不入库。从 [GitHub Releases](https://github.com/AAAduck/deepseek-harness-launcher/releases) 下载，或自行构建。