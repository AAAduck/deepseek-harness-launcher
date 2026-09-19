using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing.Drawing2D;

namespace DeepSeekHarness;

internal sealed class HarnessForm : Form
{
    private const int DefaultPort = 3080;
    private const int ProxyPort = 7897;
    private const int StartTimeoutSeconds = 120;
    private const int UpdateTimeoutSeconds = 300;
    private static readonly string NodeDir = @"D:\yule\node";
    private static readonly Color OkColor = Color.FromArgb(34, 170, 85);
    private static readonly Color WarnColor = Color.FromArgb(238, 148, 32);
    private static readonly Color IdleColor = Color.FromArgb(88, 94, 104);
    private static readonly Regex AuthUrlRegex = new(
        "https?://127\\.0\\.0\\.1:\\d+/\\?token=[^\\s\\\"'<>\\x1b]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HttpClient LocalHttp = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly Panel lamp = new();
    private readonly Label status = new();
    private readonly Label info = new();
    private readonly LinkLabel link = new();
    private readonly Button startButton;
    private readonly Button restartButton;
    private readonly Button stopButton;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly string urlFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness", "web-url.txt");
    private readonly string settingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness", "settings.txt");

    private Process? dshProcess;
    private CancellationTokenSource? startCts;
    private string? authenticatedUrl;
    private bool isOn;
    private bool busy;
    private bool refreshing;
    private bool closing;
    private int lastPort = DefaultPort;
    private readonly CheckBox autoUpdateCheckbox = new();

    internal HarnessForm()
    {
        Text = "DeepSeek Harness 控制台";
        ClientSize = new Size(560, 265);
        MinimumSize = new Size(560, 265);
        MaximumSize = new Size(560, 265);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f);
        ApplyWindowIcon();

        var panel = new Panel
        {
            Location = new Point(22, 20),
            Size = new Size(424, 138),
            BackColor = Color.FromArgb(246, 248, 252)
        };
        Controls.Add(panel);

        lamp.Location = new Point(28, 39);
        lamp.Size = new Size(36, 36);
        lamp.Paint += DrawLamp;
        panel.Controls.Add(lamp);

        status.Location = new Point(82, 21);
        status.Size = new Size(320, 30);
        status.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        status.Text = "检测中";
        panel.Controls.Add(status);

        info.Location = new Point(84, 55);
        info.Size = new Size(320, 22);
        info.ForeColor = Color.FromArgb(108, 114, 126);
        panel.Controls.Add(info);

        link.Location = new Point(83, 80);
        link.Size = new Size(320, 25);
        link.AutoEllipsis = true;
        link.LinkClicked += (_, _) => OpenKnownUrl();
        panel.Controls.Add(link);

        autoUpdateCheckbox.Text = "启动前自动更新 DSH 与插件";
        autoUpdateCheckbox.Checked = LoadAutoUpdateSetting();
        autoUpdateCheckbox.AutoSize = true;
        autoUpdateCheckbox.Location = new Point(28, 110);
        autoUpdateCheckbox.ForeColor = Color.FromArgb(88, 94, 104);
        autoUpdateCheckbox.CheckedChanged += (_, _) => SaveAutoUpdateSetting();
        panel.Controls.Add(autoUpdateCheckbox);

        startButton = NewButton("开始", 22, Color.FromArgb(34, 170, 85));
        restartButton = NewButton("重启", 124, Color.FromArgb(238, 148, 32));
        stopButton = NewButton("停止", 226, Color.FromArgb(224, 69, 62));
        var refreshButton = NewButton("刷新", 328, Color.FromArgb(58, 124, 240));
        var envButton = NewButton("环境", 430, Color.FromArgb(114, 122, 143));
        Controls.AddRange(new Control[] { startButton, restartButton, stopButton, refreshButton, envButton });

        startButton.Click += async (_, _) => await RunStartAsync(startButton, "启动中", reuseExisting: true);
        restartButton.Click += async (_, _) => await RunStartAsync(restartButton, "重启中", reuseExisting: false);
        stopButton.Click += async (_, _) => await StopClickedAsync();
        refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        envButton.Click += async (_, _) => await RunEnvCheckAsync();
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            Activate();
            await RefreshStatusAsync();
            _ = RunStartAsync(startButton, "启动中", reuseExisting: true);
            refreshTimer.Start();
        };
        FormClosing += (_, _) =>
        {
            closing = true;
            refreshTimer.Stop();
            CancelPendingStart();
            try { dshProcess?.Dispose(); } catch { }
            dshProcess = null;
        };
        UpdateButtons();
    }

    // ---- 启动 / 重启 / 停止 -------------------------------------------------

    private async Task RunStartAsync(Button active, string busyText, bool reuseExisting)
    {
        if (busy || closing || IsDisposed) return;

        var cts = new CancellationTokenSource();
        CancelPendingStart();
        startCts = cts;
        EnterBusy(active, busyText);

        try
        {
            if (reuseExisting)
            {
                var known = await ResolveUsableUrlAsync();
                if (cts.IsCancellationRequested) return;
                if (known is not null)
                {
                    OpenBrowser(known);
                    return;
                }
            }

            await StopHarnessProcessesAsync();
            if (cts.IsCancellationRequested) return;
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(5), cts.Token);
            if (cts.IsCancellationRequested) return;
            if (autoUpdateCheckbox.Checked)
            {
                await UpdatePluginsAsync(cts.Token);
                if (cts.IsCancellationRequested) return;
                status.Text = busyText;
                status.ForeColor = WarnColor;
            }
            await StartHarnessAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!closing && !IsDisposed) ShowError("启动失败", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(startCts, cts)) EndBusy();
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    private async Task StopClickedAsync()
    {
        if (busy) return;
        EnterBusy(stopButton, "停止中");
        try
        {
            CancelPendingStart();
            await StopHarnessProcessesAsync();
            authenticatedUrl = null;
            TryDeleteUrlFile();
        }
        catch (Exception ex)
        {
            if (!closing && !IsDisposed) ShowError("停止失败", ex.Message);
        }
        finally
        {
            EndBusy();
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    private void CancelPendingStart()
    {
        var cts = startCts;
        startCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private async Task StartHarnessAsync(CancellationToken ct)
    {
        var npx = ResolveNpxPath();
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "web");
        // dsh 首次运行会自动初始化缺失的 profile（loadProfile 对无 package.json 的
        // 内置 profile 执行 initProfile）；这里只保证 WorkingDirectory 存在即可。
        var profileReady = File.Exists(Path.Combine(profile, "package.json"));
        var workDir = profileReady
            ? profile
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!profileReady)
            SetInfo("首次运行：正在初始化 profile（需联网），请稍候...");

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"\"{npx}\" --yes @deepseek-ai/dsh@latest web --no-open --host 127.0.0.1 --port {DefaultPort}\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["DSH_HOME"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        var path = psi.Environment["PATH"] ?? string.Empty;
        var nodeDir = Path.GetDirectoryName(npx);
        if (!string.IsNullOrWhiteSpace(nodeDir) && !path.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(nodeDir, StringComparer.OrdinalIgnoreCase))
            psi.Environment["PATH"] = nodeDir + ";" + path;
        ConfigureOptionalProxy(psi);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleProcessLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleProcessLine(e.Data);
        process.Exited += (_, _) =>
        {
            if (closing || IsDisposed) return;
            if (ReferenceEquals(dshProcess, process))
            {
                try { BeginInvoke(UpdateButtons); } catch { }
            }
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 npx。");
        dshProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(StartTimeoutSeconds);
        var portAppeared = false;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return;
            if (authenticatedUrl is not null) return;

            if (ProcessHasExited(process))
                throw new InvalidOperationException(
                    $"DeepSeek Harness 立即退出（代码 {SafeExitCode(process)}）。请检查 Node / npx 安装。");

            if (!portAppeared && IsPortListening(DefaultPort)) portAppeared = true;

            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return; }
        }

        if (ct.IsCancellationRequested) return;

        throw new TimeoutException(portAppeared
            ? $"端口 {DefaultPort} 已监听，但 {StartTimeoutSeconds} 秒内未捕获到认证链接。\n请点击“重启”重试。"
            : $"等待 DeepSeek Harness Web 服务超时（{StartTimeoutSeconds} 秒）。\n" +
              "首次运行需要用 npx 拉取 @deepseek-ai/dsh，速度取决于网络。\n请点击“重启”重试。");
    }

    private void HandleProcessLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = AuthUrlRegex.Match(line);
        if (!match.Success) return;

        var url = match.Value.TrimEnd('.', ',', ';', ')', ']', '\x1b');
        authenticatedUrl = url;
        lastPort = ExtractPort(url) ?? DefaultPort;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(urlFile)!);
            File.WriteAllText(urlFile, url, new UTF8Encoding(false));
        }
        catch { }

        if (closing || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (closing || IsDisposed) return;
                link.Text = "打开 DeepSeek Harness 控制台";
                UpdateButtons();
                OpenBrowser(url);
            });
        }
        catch { }
    }

    // ---- 状态刷新 -----------------------------------------------------------

    private async Task<string?> ResolveUsableUrlAsync()
    {
        var candidates = new[] { authenticatedUrl, TryReadUrlFile() };
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await ProbeUrlAsync(candidate!))
            {
                authenticatedUrl = candidate;
                lastPort = ExtractPort(candidate!) ?? DefaultPort;
                return candidate;
            }
        }
        return null;
    }

    private static async Task<bool> ProbeUrlAsync(string url)
    {
        try
        {
            using var response = await LocalHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch { return false; }
    }

    private static async Task<bool> ProbeServerAsync(int port)
    {
        try
        {
            using var response = await LocalHttp.GetAsync($"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseHeadersRead);
            var body = await response.Content.ReadAsStringAsync();
            return response.StatusCode == HttpStatusCode.OK ||
                   (response.StatusCode == HttpStatusCode.Unauthorized && body.Contains("dsh web authentication required", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private async Task RefreshStatusAsync()
    {
        if (refreshing || closing || IsDisposed) return;
        refreshing = true;
        try
        {
            var serverOn = await ProbeServerAsync(DefaultPort);
            var ownOn = dshProcess is not null && !ProcessHasExited(dshProcess);
            isOn = serverOn || ownOn;
            if (serverOn) lastPort = DefaultPort;
            if (busy) return;

            if (serverOn && authenticatedUrl is not null)
            {
                status.Text = "运行中";
                status.ForeColor = OkColor;
                info.Text = $"端口: {lastPort}    已获取认证链接";
                link.Text = "打开 DeepSeek Harness 控制台";
            }
            else if (serverOn)
            {
                status.Text = "已运行";
                status.ForeColor = WarnColor;
                info.Text = "端口 3080 正在运行，但认证链接不可用";
                link.Text = "点击“开始”刷新认证链接";
            }
            else if (ownOn)
            {
                status.Text = "启动中";
                status.ForeColor = WarnColor;
                info.Text = "正在等待 Harness Web 服务";
                link.Text = "认证链接生成后会自动打开";
            }
            else
            {
                status.Text = "未运行";
                status.ForeColor = IdleColor;
                info.Text = "web profile 已就绪，点击“开始”启动";
                link.Text = "启动后自动打开认证链接";
            }
            lamp.Invalidate();
            UpdateButtons();
        }
        finally { refreshing = false; }
    }

    // ---- 进程管理 -----------------------------------------------------------

    private async Task StopHarnessProcessesAsync()
    {
        var records = GetProcessRecords();
        var seeds = records.Values.Where(IsHarnessCommand).Select(x => x.Id).ToHashSet();
        if (dshProcess is not null && !ProcessHasExited(dshProcess))
        {
            try { seeds.Add(dshProcess.Id); } catch { }
        }
        var all = new HashSet<int>(seeds);
        var queue = new Queue<int>(seeds);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in records.Values.Where(x => x.ParentId == parent).Select(x => x.Id))
                if (all.Add(child)) queue.Enqueue(child);
        }

        foreach (var id in all.OrderByDescending(x => x))
        {
            try { Process.GetProcessById(id).Kill(entireProcessTree: true); } catch { }
        }
        try { dshProcess?.Dispose(); } catch { }
        dshProcess = null;
        await Task.CompletedTask;
    }

    private static Dictionary<int, ProcessRecord> GetProcessRecords()
    {
        var result = new Dictionary<int, ProcessRecord>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process");
            foreach (ManagementObject item in searcher.Get())
            {
                var id = Convert.ToInt32(item["ProcessId"]);
                var parent = Convert.ToInt32(item["ParentProcessId"]);
                result[id] = new ProcessRecord(id, parent, item["Name"] as string ?? string.Empty, item["CommandLine"] as string ?? string.Empty);
            }
        }
        catch { }
        return result;
    }

    private static bool IsHarnessCommand(ProcessRecord p)
    {
        if (p.Name.Equals("DeepSeekHarness.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var c = p.CommandLine;
        return c.Contains("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("@deepseek-ai\\dsh", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(c, @"(?i)(^|[\\/\s])dsh(?:\.cmd)?(?:[\\/](?:lib|bin))?\s+(?:web|--profile\s+web)\b") ||
               Regex.IsMatch(c, @"(?i)\bnpx(?:\.cmd)?\b.*\b(?:@deepseek-ai[\\/]dsh|dsh)\b");
    }

    private static async Task WaitForPortToCloseAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsPortListening(port)) return;
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static bool IsPortListening(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port); }
        catch { return false; }
    }

    private static string ResolveNpxPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(NodeDir, "npx.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npx.cmd")
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => Path.Combine(x.Trim('"'), "npx.cmd")));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) throw new FileNotFoundException($"找不到 npx.cmd。已检查 {NodeDir} 和系统 PATH。", "npx.cmd");
        return found;
    }

    // ---- 自动更新 -----------------------------------------------------------

    private static string? ResolvePnpmPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(NodeDir, "pnpm.cmd"),
            Path.Combine(NodeDir, "pnpm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "pnpm.cmd")
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), "pnpm.cmd")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveCorepackPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(NodeDir, "corepack.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "corepack.cmd")
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), "corepack.cmd")));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 构建 pnpm update 的启动参数。优先真实 pnpm；找不到时回退 corepack，
    /// 此时参数必须带 "pnpm" 前缀（corepack 是 shim，不直接接受 update 子命令）。
    /// 不带 --latest：git 依赖本就拉默认分支最新 commit，
    /// 带 semver 范围的注册表依赖则应留在声明的范围内。
    /// </summary>
    private static ProcessStartInfo? NewPnpmUpdateStartInfo(string profile)
    {
        string fileName;
        string arguments;
        var pnpm = ResolvePnpmPath();
        if (pnpm is not null)
        {
            fileName = pnpm;
            arguments = "update --no-frozen-lockfile --reporter=append-only";
        }
        else
        {
            var corepack = ResolveCorepackPath();
            if (corepack is null) return null;
            fileName = corepack;
            arguments = "pnpm update --no-frozen-lockfile --reporter=append-only";
        }
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = profile,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    /// <summary>
    /// 找出 profile 里路径已失效的 link: 依赖（pnpm 遇到断链会整体失败，
    /// 提前检测并给出具体路径，比笼统的"更新失败"更可定位）。
    /// </summary>
    private static List<string> FindBrokenLinkDeps(string profile)
    {
        var broken = new List<string>();
        try
        {
            var json = File.ReadAllText(Path.Combine(profile, "package.json"));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)) return broken;
            foreach (var dep in deps.EnumerateObject())
            {
                var value = dep.Value.GetString();
                if (value is null || !value.StartsWith("link:", StringComparison.OrdinalIgnoreCase)) continue;
                var linkPath = value.Substring(5);
                if (!Directory.Exists(linkPath)) broken.Add($"{dep.Name} -> {linkPath}");
            }
        }
        catch { }
        return broken;
    }

    private async Task UpdatePluginsAsync(CancellationToken ct)
    {
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "profiles", "web");
        if (!Directory.Exists(profile))
        {
            SetInfo("web profile 不存在，跳过插件更新");
            return;
        }
        if (!File.Exists(Path.Combine(profile, "package.json")))
        {
            SetInfo("profile 无 package.json，跳过插件更新");
            return;
        }

        var broken = FindBrokenLinkDeps(profile);
        if (broken.Count > 0)
        {
            SetInfo($"本地插件路径失效：{broken[0]}");
            AppendUpdateLog($"跳过更新：本地插件路径失效\n{string.Join("\n", broken)}\n");
            return;
        }

        var psi = NewPnpmUpdateStartInfo(profile);
        if (psi is null)
        {
            SetInfo("pnpm 未找到，跳过插件更新");
            return;
        }

        status.Text = "更新中";
        status.ForeColor = WarnColor;
        SetInfo("正在更新 DSH 插件...");
        lamp.Invalidate();

        using var updateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        updateCts.CancelAfter(TimeSpan.FromSeconds(UpdateTimeoutSeconds));

        var output = new StringBuilder();
        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) output.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) output.AppendLine(e.Data);
            };

            if (!proc.Start())
            {
                SetInfo("更新失败：无法启动 pnpm");
                return;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(updateCts.Token);

            if (updateCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                SetInfo("更新超时，已跳过");
            }
            else if (proc.ExitCode == 0)
            {
                SetInfo("插件已更新到最新");
            }
            else
            {
                SetInfo($"更新部分失败（{proc.ExitCode}），继续启动");
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消（关窗/新一次启动）：必须杀掉 pnpm，否则进程泄漏到后台
            if (proc is not null)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
            AppendUpdateLog(output.ToString());
        }
    }

    private void AppendUpdateLog(string content)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "update-log.txt"),
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{content}\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    private bool LoadAutoUpdateSetting()
    {
        try { return !File.Exists(settingsFile) || File.ReadAllText(settingsFile).Trim() != "0"; }
        catch { return true; }
    }

    private void SaveAutoUpdateSetting()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            File.WriteAllText(settingsFile, autoUpdateCheckbox.Checked ? "1" : "0", new UTF8Encoding(false));
        }
        catch { }
    }

    private void SetInfo(string text)
    {
        if (closing || IsDisposed) return;
        try { BeginInvoke(() => { if (!closing && !IsDisposed) info.Text = text; }); } catch { }
    }

    // ---- 环境检测 -----------------------------------------------------------

    private async Task RunEnvCheckAsync()
    {
        if (closing || IsDisposed) return;
        status.Text = "检测中";
        status.ForeColor = WarnColor;
        SetInfo("正在检测环境...");
        lamp.Invalidate();
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("【环境检测报告】");
            sb.AppendLine();

            sb.AppendLine(Environment.Is64BitOperatingSystem
                ? "✓ 系统：Windows 10/11 x64（符合要求）"
                : "✗ 系统：32 位 Windows（不支持，请换 64 位系统）");

            var nodeExe = TryFindNodeExe();
            if (nodeExe is null)
            {
                sb.AppendLine("✗ Node.js：未找到");
                sb.AppendLine("    解决：到 https://nodejs.org 下载安装 LTS（≥ 18）");
            }
            else
            {
                var v = await GetToolVersionAsync(nodeExe, "--version");
                sb.AppendLine($"✓ Node.js：{v?.Trim() ?? "未知"}（{Path.GetDirectoryName(nodeExe)}）");
            }

            string? npxPath = null;
            try { npxPath = ResolveNpxPath(); } catch { }
            if (npxPath is null)
            {
                sb.AppendLine("✗ npx：未找到（随 Node.js 安装，重装 Node 即可）");
            }
            else
            {
                var v = await GetToolVersionAsync(npxPath, "--version");
                sb.AppendLine($"✓ npx：{v?.Trim() ?? "?"}（{npxPath}）");
            }

            var pnpm = ResolvePnpmPath();
            if (pnpm is null)
            {
                var corepack = ResolveCorepackPath();
                sb.AppendLine("✗ pnpm：未找到（可选，仅影响插件更新）");
                sb.AppendLine(corepack is null
                    ? "    解决：npm install -g pnpm"
                    : "    解决：corepack enable pnpm（已检测到 corepack）");
            }
            else
            {
                var v = await GetToolVersionAsync(pnpm, "--version");
                sb.AppendLine($"✓ pnpm：{v?.Trim() ?? "?"}（{pnpm}）");
            }

            var profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "profiles", "web");
            sb.AppendLine(File.Exists(Path.Combine(profile, "package.json"))
                ? "✓ profile：已初始化"
                : "✗ profile：未初始化（首次点击「开始」会自动初始化）");

            if (IsPortListening(DefaultPort))
                sb.AppendLine($"⚠ 端口 {DefaultPort} 已被占用，可能已有 DSH 在运行");

            MessageBox.Show(sb.ToString(), "环境检测", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowError("环境检测失败", ex.Message);
        }
        finally
        {
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    private static string? TryFindNodeExe()
    {
        string? npx = null;
        try { npx = ResolveNpxPath(); } catch { }
        if (npx is not null)
        {
            var dir = Path.GetDirectoryName(npx);
            if (dir is not null)
            {
                var exe = Path.Combine(dir, "node.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), "node.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<string?> GetToolVersionAsync(string exe, string args)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(cts.Token);
            var text = (await outTask) + (await errTask);
            var first = text.Split('\n').FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch { return null; }
    }

    private static void ConfigureOptionalProxy(ProcessStartInfo psi)
    {
        var http = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var https = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        if (!string.IsNullOrWhiteSpace(http) || !string.IsNullOrWhiteSpace(https)) return;
        if (!IsTcpOpen("127.0.0.1", ProxyPort)) return;
        psi.Environment["NODE_USE_ENV_PROXY"] = "1";
        psi.Environment["HTTP_PROXY"] = $"http://127.0.0.1:{ProxyPort}";
        psi.Environment["HTTPS_PROXY"] = $"http://127.0.0.1:{ProxyPort}";
    }

    private static bool IsTcpOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(150) && client.Connected;
        }
        catch { return false; }
    }

    private static bool ProcessHasExited(Process process)
    {
        // 进程对象可能已被 StopHarnessProcessesAsync / FormClosing 释放，
        // 此时 HasExited 会抛 InvalidOperationException("No process is associated with this object.")。
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    // ---- 链接与界面 ---------------------------------------------------------

    private Button NewButton(string text, int x, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, 168),
            Size = new Size(94, 46),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void ApplyWindowIcon()
    {
        // 让窗口图标与 DeepSeekHarness.exe 的图标一致（含标题栏和任务栏）。
        try
        {
            var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (exeIcon is not null) Icon = exeIcon;
        }
        catch { }
    }

    private void OpenKnownUrl()
    {
        var url = authenticatedUrl ?? TryReadUrlFile();
        if (url is null)
        {
            MessageBox.Show("当前没有可用的认证链接，请点击“开始”获取。", "DeepSeek Harness");
            return;
        }
        OpenBrowser(url);
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"无法打开浏览器：{ex.Message}", "DeepSeek Harness"); }
    }

    private string? TryReadUrlFile()
    {
        try
        {
            if (!File.Exists(urlFile)) return null;
            var value = File.ReadAllText(urlFile).Trim();
            return AuthUrlRegex.IsMatch(value) ? AuthUrlRegex.Match(value).Value : null;
        }
        catch { return null; }
    }

    private void TryDeleteUrlFile()
    {
        try { if (File.Exists(urlFile)) File.Delete(urlFile); } catch { }
    }

    private void EnterBusy(Button active, string text)
    {
        busy = true;
        startButton.Enabled = restartButton.Enabled = stopButton.Enabled = false;
        active.Text = text;
        status.Text = text;
        status.ForeColor = WarnColor;
        lamp.Invalidate();
    }

    private void EndBusy()
    {
        busy = false;
        startButton.Text = "开始";
        restartButton.Text = "重启";
        stopButton.Text = "停止";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (busy) return;
        startButton.Enabled = true;
        restartButton.Enabled = true;
        stopButton.Enabled = isOn;
    }

    private void DrawLamp(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(isOn ? Color.FromArgb(34, 170, 85) : Color.FromArgb(178, 182, 190));
        e.Graphics.FillEllipse(brush, 4, 4, 28, 28);
    }

    private void ShowError(string title, string message) => MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static int? ExtractPort(string url)
    {
        try { return new Uri(url).Port; } catch { return null; }
    }

    private sealed record ProcessRecord(int Id, int ParentId, string Name, string CommandLine);
}
