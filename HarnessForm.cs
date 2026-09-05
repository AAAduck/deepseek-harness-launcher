using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Drawing.Drawing2D;

namespace DeepSeekHarness;

internal sealed class HarnessForm : Form
{
    private const int DefaultPort = 3080;
    private const int ProxyPort = 7897;
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

    private Process? dshProcess;
    private string? authenticatedUrl;
    private bool isOn;
    private bool busy;
    private bool refreshing;
    private bool autoStarting;
    private int lastPort = DefaultPort;

    internal HarnessForm()
    {
        Text = "DeepSeek Harness 控制台";
        ClientSize = new Size(520, 220);
        MinimumSize = new Size(520, 220);
        MaximumSize = new Size(520, 220);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f);

        var panel = new Panel
        {
            Location = new Point(22, 20),
            Size = new Size(476, 118),
            BackColor = Color.FromArgb(246, 248, 252)
        };
        Controls.Add(panel);

        lamp.Location = new Point(28, 39);
        lamp.Size = new Size(36, 36);
        lamp.Paint += DrawLamp;
        panel.Controls.Add(lamp);

        status.Location = new Point(82, 21);
        status.Size = new Size(370, 30);
        status.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        status.Text = "检测中";
        panel.Controls.Add(status);

        info.Location = new Point(84, 55);
        info.Size = new Size(370, 22);
        info.ForeColor = Color.FromArgb(108, 114, 126);
        panel.Controls.Add(info);

        link.Location = new Point(83, 80);
        link.Size = new Size(370, 25);
        link.LinkClicked += (_, _) => OpenKnownUrl();
        panel.Controls.Add(link);

        startButton = NewButton("开始", 22, Color.FromArgb(34, 170, 85));
        restartButton = NewButton("重启", 143, Color.FromArgb(238, 148, 32));
        stopButton = NewButton("停止", 264, Color.FromArgb(224, 69, 62));
        var refreshButton = NewButton("刷新", 385, Color.FromArgb(58, 124, 240));
        Controls.AddRange(new Control[] { startButton, restartButton, stopButton, refreshButton });

        startButton.Click += async (_, _) => await StartClickedAsync();
        restartButton.Click += async (_, _) => await RestartClickedAsync();
        stopButton.Click += async (_, _) => await StopClickedAsync();
        refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            Activate();
            await RefreshStatusAsync();
            _ = AutoStartAsync();
            refreshTimer.Start();
        };
        FormClosing += (_, _) =>
        {
            refreshTimer.Stop();
            dshProcess?.Dispose();
        };
        UpdateButtons();
    }

    private async Task AutoStartAsync()
    {
        if (autoStarting || busy || IsDisposed) return;
        autoStarting = true;
        try
        {
            var known = await ResolveUsableUrlAsync();
            if (known is not null)
            {
                OpenBrowser(known);
                return;
            }

            BeginBusy(startButton, "启动中");
            await StopHarnessProcessesAsync();
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(5));
            await StartHarnessAsync();
        }
        catch (Exception ex)
        {
            ShowError("启动失败", ex.Message);
        }
        finally
        {
            EndBusy();
            autoStarting = false;
            await RefreshStatusAsync();
        }
    }

    private Button NewButton(string text, int x, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, 148),
            Size = new Size(113, 46),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private async Task StartClickedAsync()
    {
        if (busy) return;
        BeginBusy(startButton, "启动中");
        try
        {
            var known = await ResolveUsableUrlAsync();
            if (known is not null)
            {
                OpenBrowser(known);
                return;
            }

            await StopHarnessProcessesAsync();
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(5));
            await StartHarnessAsync();
        }
        catch (Exception ex)
        {
            ShowError("启动失败", ex.Message);
        }
        finally
        {
            EndBusy();
            await RefreshStatusAsync();
        }
    }

    private async Task RestartClickedAsync()
    {
        if (busy) return;
        BeginBusy(restartButton, "重启中");
        try
        {
            await StopHarnessProcessesAsync();
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(5));
            await StartHarnessAsync();
        }
        catch (Exception ex)
        {
            ShowError("重启失败", ex.Message);
        }
        finally
        {
            EndBusy();
            await RefreshStatusAsync();
        }
    }

    private async Task StopClickedAsync()
    {
        if (busy) return;
        BeginBusy(stopButton, "停止中");
        try
        {
            await StopHarnessProcessesAsync();
            authenticatedUrl = null;
            TryDeleteUrlFile();
        }
        catch (Exception ex)
        {
            ShowError("停止失败", ex.Message);
        }
        finally
        {
            EndBusy();
            await RefreshStatusAsync();
        }
    }

    private async Task StartHarnessAsync()
    {
        var npx = ResolveNpxPath();
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "web");
        if (!Directory.Exists(profile))
            throw new InvalidOperationException($"找不到 web profile:\n{profile}");

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"\"{npx}\" --yes @deepseek-ai/dsh web --no-open --host 127.0.0.1 --port {DefaultPort}\"",
            WorkingDirectory = profile,
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
            if (ReferenceEquals(dshProcess, process))
            {
                try { BeginInvoke(UpdateButtons); } catch { }
            }
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 npx。");
        dshProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (authenticatedUrl is not null) return;
            if (process.HasExited)
            {
                var code = process.ExitCode;
                throw new InvalidOperationException($"DeepSeek Harness 立即退出（代码 {code}）。请检查 Node / npx 安装。");
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("等待 DeepSeek Harness Web 服务超时。\n请点击“重启”重试，或查看 DSH profile 是否存在。");
    }

    private void HandleProcessLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = AuthUrlRegex.Match(line);
        if (!match.Success) return;
        authenticatedUrl = match.Value.TrimEnd('.', ',', ';', ')', ']', '\x1b');
        lastPort = ExtractPort(authenticatedUrl) ?? DefaultPort;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(urlFile)!);
            File.WriteAllText(urlFile, authenticatedUrl, new UTF8Encoding(false));
            BeginInvoke(() =>
            {
                link.Text = $"{authenticatedUrl}   (点击打开控制台)";
                UpdateButtons();
                OpenBrowser(authenticatedUrl);
            });
        }
        catch { }
    }

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
        if (refreshing || IsDisposed) return;
        refreshing = true;
        try
        {
            var serverOn = await ProbeServerAsync(DefaultPort);
            var ownOn = dshProcess is { HasExited: false };
            isOn = serverOn || ownOn;
            if (serverOn) lastPort = DefaultPort;
            if (busy) return;

            if (serverOn && authenticatedUrl is not null)
            {
                status.Text = "运行中";
                status.ForeColor = Color.FromArgb(34, 170, 85);
                info.Text = $"端口: {lastPort}    已获取认证链接";
                link.Text = $"{authenticatedUrl}   (点击打开控制台)";
            }
            else if (serverOn)
            {
                status.Text = "已运行";
                status.ForeColor = Color.FromArgb(238, 148, 32);
                info.Text = "端口 3080 正在运行，但认证链接不可用";
                link.Text = "点击“开始”刷新认证链接";
            }
            else if (ownOn)
            {
                status.Text = "启动中";
                status.ForeColor = Color.FromArgb(238, 148, 32);
                info.Text = "正在等待 Harness Web 服务";
                link.Text = "认证链接生成后会自动打开";
            }
            else
            {
                status.Text = "未运行";
                status.ForeColor = Color.FromArgb(88, 94, 104);
                info.Text = "web profile 已就绪，点击“开始”启动";
                link.Text = "http://127.0.0.1:3080 (启动后需使用认证链接)";
            }
            lamp.Invalidate();
            UpdateButtons();
        }
        finally { refreshing = false; }
    }

    private async Task StopHarnessProcessesAsync()
    {
        var records = GetProcessRecords();
        var seeds = records.Values.Where(IsHarnessCommand).Select(x => x.Id).ToHashSet();
        if (dshProcess is { HasExited: false }) seeds.Add(dshProcess.Id);
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
        dshProcess?.Dispose();
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

    private static async Task WaitForPortToCloseAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsPortListening(port)) return;
            await Task.Delay(100);
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
            @"D:\yule\node\npx.cmd",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npx.cmd")
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => Path.Combine(x.Trim('"'), "npx.cmd")));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) throw new FileNotFoundException("找不到 npx.cmd。已检查 D:\\yule\\node 和系统 PATH。", "npx.cmd");
        return found;
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

    private void BeginBusy(Button active, string text)
    {
        busy = true;
        startButton.Enabled = restartButton.Enabled = stopButton.Enabled = false;
        active.Text = text;
        status.Text = text;
        status.ForeColor = Color.FromArgb(238, 148, 32);
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
