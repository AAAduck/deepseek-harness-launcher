# DeepSeek Harness 启动器

这是一个 Windows 启动器项目，用于启动本机 DeepSeek Harness 的 `web` profile。

## 文件说明

- `DeepSeekHarness.exe`：可直接运行的自包含主程序。
- `HarnessForm.cs`：启动器界面、进程管理、端口检测和认证 URL 处理。
- `Program.cs`：Windows Forms 程序入口。
- `DeepSeekHarness.csproj`：.NET 8 Windows 构建配置。

## 默认环境

- Node.js / npx：优先使用 `D:\yule\node\npx.cmd`，也会回退到系统 PATH。
- DeepSeek Harness profile：`%USERPROFILE%\.dsh\profiles\web`。
- Web 端口：`3080`。
- 认证 URL：保存到 `%LOCALAPPDATA%\DeepSeekHarness\web-url.txt`。

## 构建

需要 .NET 8 SDK。在项目目录执行：

```powershell
dotnet publish --configuration Release --runtime win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false
```

发布结果位于 `bin/Release/net8.0-windows/win-x64/publish/DeepSeekHarness.exe`。
