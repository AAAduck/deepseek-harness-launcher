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
2. **第一次**点「开始」：自动把引擎装到固定目录，需要联网，约 1–2 分钟，界面有进度。
3. **之后**每次启动：窗口约 2–3 秒出现，引擎引导约 8–13 秒后浏览器自动打开认证链接
   （引擎引导实测：热缓存 8.2 秒、冷缓存 12.3 秒——这段是 DSH 引擎自己的开销，启动器只是等它）。
   插件更新在引擎起来**之后**才跑，不再拖慢控制台出现的时间。

窗口上的按钮：

- **开始 / 重启 / 停止**：管理引擎进程和端口。
- **升级引擎**：查 npm 最新版 → 装到 `engine.tmp` → **成功才替换**正式引擎；失败保留当前版本，不影响使用。
- **启动后更新插件**（复选框）：引擎启动后跑 `pnpm update`，改动在**下次启动**生效。同一自然日只自动跑一次（节流戳记见下表），要强制重跑就删掉 `lastPluginUpdate.txt`。
- **环境**：一键检查 Node / npm / pnpm / 引擎 / profile / 端口占用，缺什么直接给修复命令。

键盘：**回车** = 开始，**Esc** = 停止，**Tab** 可遍历所有控件。

### 生命周期与单实例

- **关闭窗口 = 结束引擎**。点 ✕ 会把启动器自己拉起来的 DSH 引擎一并结束（含进程树），
  不会留下占着 3080 的后台进程。想让引擎继续跑就别关窗口。
- **同时只能开一个启动器**。重复双击不会开出第二个实例，而是把已有窗口切到前台。
  这是刻意的：两个实例会互相把对方的引擎当残留进程杀掉，还会争同一个 `web-url.txt`。

## 几个常见问题

**有多份 Node 怎么办？** 启动器会逐个试 `--version`，自动选第一个 ≥ 18 的，npm 强制用同一目录那份。装在非标准位置时，把路径写进 `node-dir.txt` 即可。

Node 查找顺序：PATH → exe 同级的 `node\` → `%ProgramFiles%\nodejs` → `%APPDATA%\npm` → `%LOCALAPPDATA%\DeepSeekHarness\node-dir.txt`。

**引擎装在哪、会不会自动更新？** 引擎装在固定目录，启动时直接运行 `node_modules\@deepseek-ai\dsh\lib\bin.js web`，不走 npx、不查版本、不联网重装。所以它**不会**自动跟进新版，想升级就点「升级引擎」。

装引擎用的是与 `npx` 内部相同的标准做法：在私有目录里跑一次 `npm install` 再直接执行 `bin.js`。
区别在于版本是**钉死的**：安装时先查精确版本号，配合 `npm install --save-exact`，所以
`engine/package.json` 与 `engine/package-lock.json` 记录的版本始终一致，同一份 manifest
在任何日子重装都得到同一个版本（直接写 `"latest"` 会绕过锁文件、抓到当天最新版，无法复现）。

**npm 源跟随你的配置。** 引擎的安装与「升级引擎」的版本查询都会先读
`npm config get registry` 并显式使用它，所以 `npm config set registry https://registry.npmmirror.com`
对启动器**同样生效**。没配过才回落到官方源 `https://registry.npmjs.org/`。
读取到的源会做参数校验，含引号或空白的值不采用。

首次安装参考耗时：实测本机 537 个包约 **67 秒**（不含版本查询），视网络而定。

## 默认环境一览

| 项 | 位置 |
|---|---|
| DSH 引擎 | `%LOCALAPPDATA%\DeepSeekHarness\engine` |
| DSH profile | `%USERPROFILE%\.dsh\profiles\web` |
| 认证 URL | `%LOCALAPPDATA%\DeepSeekHarness\web-url.txt` |
| 更新日志 | `%LOCALAPPDATA%\DeepSeekHarness\update-log.txt`（超 256 KB 自动截断） |
| 插件更新节流戳记 | `%LOCALAPPDATA%\DeepSeekHarness\lastPluginUpdate.txt`（删掉 = 下次启动强制更新） |
| 启动异常日志 | `%LOCALAPPDATA%\DeepSeekHarness\startup-log.txt`、`crash-log.txt` |
| 自定义 Node 目录 | `%LOCALAPPDATA%\DeepSeekHarness\node-dir.txt`（可选） |
| Web 端口 | `3080` |

## 环境变量（可选）

| 变量 | 作用 |
|---|---|
| `HTTP_PROXY` / `HTTPS_PROXY` | 让 node/npm 走代理（Node 24+ 需配合内置的 `NODE_USE_ENV_PROXY`，启动器会自动加） |
| `DSH_FORCE_LOCAL_PROXY=1` | 强制探测本机 `127.0.0.1:7897` 代理。默认只在系统里存在任何代理线索时才探测——没有代理的机器上，这次探测会白等一个 150 ms 超时，所以默认跳过 |


## 构建

需要 .NET 8 SDK，发布参数（自包含、单文件、win-x64、无调试符号）已写进工程文件，直接跑：

```powershell
dotnet publish --configuration Release
```

产物在 `bin/Release/net8.0-windows/win-x64/publish/DeepSeekHarness.exe`。

## 已知限制

- **未代码签名**，SmartScreen 会提示；Defender 也可能问是否允许 node 监听本地端口，点允许。
  给引擎目录加一条 Defender 排除路径可以明显加快引擎引导（实测冷启动 12.3 → 8.2 秒），
  因为 `engine` 下有 25 000 多个文件会被实时扫描。
- **不自动跟进引擎版本**，升级是显式动作，免得每次启动都静默重装。
- **端口 3080 被占**时启动失败，报错会附 `netstat -ano | findstr :3080`；
  若该端口落在 Windows 动态端口范围内（Hyper-V / WSL / Docker Desktop 预留段），报错会额外给出释放方法。
- 首次装引擎中途被强行结束，会留下 `engine.tmp` 残留；下次安装会自动清理（「环境」检测也会提示）。
- 国内访问 npm 慢的话，先执行 `npm config set registry https://registry.npmmirror.com`。
- **token 有效性无法在启动器侧校验**：DSH 的 `/` 无论 token 对错都返回同样的页面，
  `/api/*` 对任何 HTTP 头形式（Bearer / Cookie / query）一律 401——认证是浏览器端握手。
  所以启动器只能确认"这台 3080 上跑的确实是 DSH"，无法确认链接里的 token 还有效。

## 文件说明

- `HarnessForm.cs`：界面、进程管理、端口检测、认证 URL 捕获、引擎安装与升级、插件更新节流。
- `Program.cs`：程序入口、单实例互斥、启动异常兜底（写 `crash-log.txt`）。
- `app.manifest`：应用清单（asInvoker 不提权、supportedOS 声明、DPI 模式、长路径感知）。
- `DeepSeekHarness.csproj`：.NET 8 构建配置（发布参数已内置）。
- `DeepSeekHarness.exe`：构建产物，约 155 MB，不入库。从 [GitHub Releases](https://github.com/AAAduck/deepseek-harness-launcher/releases) 下载，或自行构建。