# DeepSeek Harness 启动器

> **非官方项目**：本项目为第三方个人作品，与 DeepSeek 官方无隶属、赞助或背书关系。
> 启动器仅在你本机调用官方发布的 npm 包 `@deepseek-ai/dsh`，不捆绑、不修改其代码。

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
3. **pnpm**（可选，推荐）。两种装法：
   - `npm install -g pnpm`（推荐）
   - `corepack enable pnpm`（Node ≥ 16.9 自带 corepack，无需额外安装）
   不装也能运行，只是跳过插件更新
4. 首次运行在打开的 Web 界面「设置 → 模型」中配置自己的 **DeepSeek API Key**

> 启动器窗口右下角有「环境」按钮，点开即可一键检测本机 Node / npx / pnpm / profile 是否就绪，并给出缺失项的修复命令。

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
