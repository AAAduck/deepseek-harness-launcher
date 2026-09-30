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

键盘：**回车** = 启动/停止（随主按钮），**Esc** = 停止引擎，**Tab** 可遍历所有控件。

### 生命周期与单实例

- **引擎和启动器是同生共死的，两种死法不同**：
  - 点 ✕（优雅关闭）：`FormClosing` 里主动把引擎进程树杀干净，不留占 3080 的后台进程；
  - 强杀 / 崩溃：跳过主动清理，但引擎的 stdout/stderr **管道读端挂在启动器进程上**，
    管道一断，node 在下一次写日志时就被 EPIPE 带崩（本机实测：模拟启动器强杀后
    约 1 秒 `EXIT code=1`）。所以**没有任何方式能让引擎在启动器死后长期存活**——
    "关窗即断会话"是架构属性，不是可以绕过去的小毛病。
- **同时只能开一个启动器**。重复双击不会开出第二个实例，而是把已有窗口切到前台。
  这是刻意的：两个实例会互相把对方的引擎当残留进程杀掉，还会争同一个 `web-url.txt`。
- 下次启动时：旧实例的对话**历史在盘上，不丢**；新引擎起来后重新生成认证链接、
  自动开浏览器，接续原会话。

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
| `HTTP_PROXY` / `HTTPS_PROXY` | 让 node/npm 走代理（Node 24+ 需配合内置的 `NODE_USE_ENV_PROXY`，启动器会自动加） |
| `DSH_FORCE_LOCAL_PROXY=1` | 强制探测本机 `127.0.0.1:7897` 代理。默认只在系统里存在任何代理线索时才探测——没有代理的机器上，这次探测会白等一个 150 ms 超时，所以默认跳过 |


## 构建

需要 .NET 8 SDK，发布参数（自包含、单文件、win-x64、Release 无调试符号）已写进工程文件，直接跑：

```powershell
dotnet publish --configuration Release
```

产物在 `bin/Release/net8.0-windows/win-x64/publish/DeepSeekHarness.exe`。

> 旧启动器**正在运行时**这个默认路径会被锁住（报 MSB3027，锁文件的正是运行中的
> `DeepSeekHarness.exe`），此时改用独立输出目录，见下面「更新启动器本体」。

## 更新启动器本体（标准流程：空闲时段跑 `更新启动器.bat`）

每次优化启动器本体都按这个方式更新。先说清楚真实行为（本机实测，见「生命周期与单实例」）：
启动器被强杀后，引擎的 stdout 管道断裂，node 会在**下一次写日志时**随断管退出（约 1 秒内），
所以更新**必然短暂打断 Web UI**——脚本的价值不是"保住引擎"，而是：

- **时机由你定**：选空闲时段手动执行，打断成本为零（没有正在跑的对话会受影响）；
- **自动化收尾**：新实例起来后复用探针失败会自动走完整重启（清残留 → 拉引擎 →
  新认证链接自动开浏览器），全程不需要干预；**会话历史在盘上，接续不丢**；
- **精确可控**：只按映像名杀 `DeepSeekHarness.exe`，绝不碰 node.exe；失败分支不伤任何数据。

流程五步：

1. **升版本号**：`DeepSeekHarness.csproj` 的 `<Version>`，同步 `app.manifest` 的
   `assemblyIdentity version` 和 `更新启动器.bat` 开头 echo 的版本说明。
2. **发布单文件包**（旧启动器在跑，用独立输出目录绕开 `bin\` 锁）：
   ```powershell
   dotnet publish DeepSeekHarness.csproj -c Release -p:OutDir=D:\tmp\dsh-pub\bin\ -o D:\tmp\dsh-pub\out
   ```
3. **放进暂存目录**（脚本从这里取件）：
   ```powershell
   Copy-Item D:\tmp\dsh-pub\out\DeepSeekHarness.exe "$env:LOCALAPPDATA\DeepSeekHarness\update-staging\DeepSeekHarness.exe" -Force
   ```
4. **确认空闲**（浏览器里没有正在跑的会话）**双击 `更新启动器.bat`，按任意键**。
   脚本依次：强杀旧启动器 → 覆盖 `bin\Release\net8.0-windows\win-x64\` 与脚本旁的桌面副本
   → 启动新实例 → 新实例自动重拉引擎并打开认证链接。
5. **验收**：新窗口出现、浏览器自动开回 3080；点「环境」确认版本与状态；
   回到会话页确认历史接续。

注意：

- 打断窗口约十几秒（引擎重启）。别在跑长任务/生成中的会话时执行；
- 复制失败分支（目标仍被占用）不伤数据：确认旧进程退干净后重跑脚本即可；
- 引擎版本要变的话，更新完在新窗口点「升级」或「重启」；
- 单实例互斥保证新旧实例不会重叠争抢引擎与 `web-url.txt`（脚本先杀后起，顺序天然正确）。

## 已知限制

- **未代码签名**，SmartScreen 会提示；Defender 也可能问是否允许 node 监听本地端口，点允许。
  给引擎目录加一条 Defender 排除路径可以明显加快引擎引导（实测冷启动 12.3 → 8.2 秒），
  因为 `engine` 下有 25 000 多个文件会被实时扫描。
- **不自动跟进引擎版本**，升级是显式动作，免得每次启动都静默重装。
- **引擎无法脱离启动器独活**：它的 stdout/stderr 管道读端在启动器进程上，启动器一死
  （关窗/强杀/崩溃都一样），引擎最多再活到下一次写日志。想要"真·无感更新"，
  需要把 stdio 从管道改重定向到日志文件、URL 捕获改文件 tail——属代码改造，未做。
- **端口 3080 被占**时启动失败，报错会附 `netstat -ano | findstr :3080`；
  若该端口落在 Windows 动态端口范围内（Hyper-V / WSL / Docker Desktop 预留段），报错会额外给出释放方法。
- 首次装引擎中途被强行结束，会留下 `engine.tmp` 残留；下次安装会自动清理（「环境」检测也会提示）。
- 国内访问 npm 慢的话，先执行 `npm config set registry https://registry.npmmirror.com`。
- **token 有效性无法在启动器侧校验**：DSH 的 `/` 无论 token 对错都返回同样的页面，
  `/api/*` 对任何 HTTP 头形式（Bearer / Cookie / query）一律 401——认证是浏览器端握手。
  所以启动器只能确认"这台 3080 上跑的确实是 DSH"，无法确认链接里的 token 还有效。

## 文件说明

- `HarnessForm.cs`：主界面、进程管理、端口检测、认证 URL 捕获、引擎安装与升级、插件兼容性检查、配置备份恢复。
- `EngineVersionsForm.cs`：引擎版本管理窗口（列表 / 切换 / 删除）。列举与删除都放后台线程，
  窗口因此能在遍历 2.5 万个 `node_modules` 文件时保持响应。
- `FoldersForm.cs`：相关目录一览窗口。
- `ConfigBackup.cs`：配置快照（启动前与升级前自动拍摄、按文件哈希去重、一键恢复）。
- `LayoutDump.cs`：布局自检（`DSH_LAYOUT_DUMP=1` / `DSH_LAYOUT_TEST=1` 时把真实几何写入 `layout-dump.txt`）。
  自检只在 `Program.cs` 的 `DSH_LAYOUT_TEST=1` 入口里驱动，两个对话框不再各自挂 `Shown` 处理器。
- `Program.cs`：程序入口、单实例互斥、启动异常兜底（写 `crash-log.txt`）。
- `app.manifest`：应用清单（asInvoker 不提权、supportedOS 声明、长路径感知）。
  DPI 感知不在清单里声明——它由 csproj 的 `ApplicationHighDpiMode` 给出（取值 `SystemAware`），
  与 `UseWindowsForms` 生成的 `ApplicationConfiguration.Initialize()` 保持单一来源。
- `DeepSeekHarness.csproj`：.NET 8 构建配置（发布参数已内置）。
- `更新启动器.bat`：标准更新入口，**空闲时段**手动执行。按映像名只杀启动器（不碰 node）、
  从 `%LOCALAPPDATA%\DeepSeekHarness\update-staging\` 取新 exe 覆盖 `bin\Release` 与脚本旁副本、
  自动拉起新实例（会重拉引擎并打开认证链接，会话历史从盘上接续）。
  实测说明与完整流程见「更新启动器本体」。
- `DeepSeekHarness.exe`：构建产物，约 155 MB，不入库。从 [GitHub Releases](https://github.com/AAAduck/deepseek-harness-launcher/releases) 下载，或自行构建。