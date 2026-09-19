# DeepSeek Harness 启动器

这是一个 Windows 启动器项目，用于启动本机 DeepSeek Harness 的 `web` profile。

## 文件说明

- `HarnessForm.cs`：启动器界面、进程管理、端口检测和认证 URL 处理。
- `Program.cs`：Windows Forms 程序入口。
- `DeepSeekHarness.csproj`：.NET 8 Windows 构建配置。

> `DeepSeekHarness.exe`（自包含单文件，约 155 MB）为构建产物，不入库，请从 [GitHub Releases](https://github.com/AAAduck/deepseek-harness-launcher/releases) 下载或按下方「构建」自行生成。

## 运行环境（分发给同学）

启动器本身是 **.NET 8 自包含**构建，同学**不需要安装 .NET**；只需：

1. **Windows 10/11 x64**
2. **Node.js ≥ 18**（含 npx）—— 安装后 npx 会自动出现在 `%APPDATA%\npm`，启动器优先找 `D:\yule\node\npx.cmd`（本机路径），找不到会自动回退 `%APPDATA%\npm` 与 PATH
3. **pnpm**（可选，推荐）：`npm install -g pnpm`。不装也能运行，只是跳过插件更新
4. 首次运行在打开的 Web 界面「设置 → 模型」中配置自己的 **DeepSeek API Key**

> ⚠️ 首次运行前需要先初始化 profile（只做一次）：
> ```powershell
> npx --yes @deepseek-ai/dsh web
> ```
> 看到端口监听后 Ctrl+C 关闭，再打开启动器。这是当前版本的已知限制，后续版本会改为自动初始化。

## 默认环境

- Node.js / npx：优先使用 `D:\yule\node\npx.cmd`，也会回退到系统 PATH。
- DeepSeek Harness profile：`%USERPROFILE%\.dsh\profiles\web`。
- Web 端口：`3080`。
- 认证 URL：保存到 `%LOCALAPPDATA%\DeepSeekHarness\web-url.txt`。
- 自动更新：勾选"启动前自动更新 DSH 与插件"时，每次冷启动先 `npx @deepseek-ai/dsh@latest` 拉最新引擎、再在 profile 目录执行 `pnpm update`；更新日志追加到 `%LOCALAPPDATA%\DeepSeekHarness\update-log.txt`。

## 构建

需要 .NET 8 SDK。在项目目录执行：

```powershell
dotnet publish --configuration Release --runtime win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false
```

发布结果位于 `bin/Release/net8.0-windows/win-x64/publish/DeepSeekHarness.exe`。
