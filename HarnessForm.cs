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
using System.Windows.Forms.Automation;

namespace DeepSeekHarness;

internal sealed class HarnessForm : Form
{
    private const int DefaultPort = 3080;
    private const int ProxyPort = 7897;
    // 引擎首次安装到固定目录实测约 91 秒（慢网络会更久），120 秒会把正常安装误判成失败。
    private const int StartTimeoutSeconds = 600;
    private const int UpdateTimeoutSeconds = 600;
    private const int EngineInstallTimeoutSeconds = 900;
    private const int EngineQueryTimeoutSeconds = 60;
    private const int RecentOutputLines = 10;
    private static readonly string LocalAppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness");
    private static readonly string EnginePackageName = "@deepseek-ai/dsh";
    /// <summary>npm 官方源。用户改过 npm 配置时以用户配置为准（见 ResolveNpmRegistry）。</summary>
    private const string DefaultRegistry = "https://registry.npmjs.org/";
    private static readonly Color OkColor = Color.FromArgb(34, 170, 85);
    private static readonly Color WarnColor = Color.FromArgb(238, 148, 32);
    private static readonly Color IdleColor = Color.FromArgb(88, 94, 104);

    // 字体与颜色一样是"常量"，用静态字段共享。此前在构造函数和 NewButton 里
    // 各 new 了一份，同一个窗体里出现 7 个内容完全相同的 Font 对象；
    // 它们是实例字段，只能等窗体被 GC 才回收（本程序窗体活到进程结束，等于不回收）。
    // 静态共享把 7 份合成 3 份，且只分配一次。
    private static readonly Font UiFont = new("Microsoft YaHei UI", 9f);
    private static readonly Font BoldFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    private static readonly Font StatusFont = new("Microsoft YaHei UI", 14f, FontStyle.Bold);
    private static readonly Font ButtonFont = new("Microsoft YaHei UI", 10f, FontStyle.Bold);
    private static readonly Regex AuthUrlRegex = new(
        "https?://127\\.0\\.0\\.1:\\d+/\\?token=[^\\s\\\"'<>\\x1b]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnsiRegex = new(
        "\u001b\\[[0-9;?]*[ -/]*[@-~]",
        RegexOptions.Compiled);
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
    private readonly Button upgradeButton;
    private readonly Button refreshButton;
    private readonly Button envButton;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly string urlFile = Path.Combine(LocalAppDir, "web-url.txt");
    private readonly string settingsFile = Path.Combine(LocalAppDir, "settings.txt");
    /// <summary>
    /// 引擎固定安装目录。此前引擎装在 npx 的哈希缓存
    /// （%LOCALAPPDATA%\npm-cache\_npx\&lt;hash&gt;）里，三个后果都很难受：
    /// ① 每次启动 npx 都要先向 registry 查一次 @latest，有新版本就整棵静默重装
    ///   （实测 91 秒、零输出，界面像死住）；
    /// ② 被中断的半成品无法原地修复（npm 只信 .package-lock.json，不会补半截包），
    ///   只能整个删掉重下；
    /// ③ 目录名是哈希，位置不可预测。
    /// 现在改为：装到固定目录 → 启动直接跑 bin.js（零网络、零重装、不查版本）；
    /// 升级只在你点「升级引擎」时发生，且先装到 engine.tmp，成功才替换正式目录。
    /// </summary>
    private static readonly string engineDir = Path.Combine(LocalAppDir, "engine");
    private static readonly string engineStageDir = Path.Combine(LocalAppDir, "engine.tmp");
    private static readonly string engineOldDir = Path.Combine(LocalAppDir, "engine.old");
    /// <summary>web profile 目录。此前这个路径在多处各写了一遍，容易写歪，统一到这里。</summary>
    private static readonly string webProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "web");

    private Process? dshProcess;
    private CancellationTokenSource? startCts;
    private readonly List<string> recentOutput = new();
    private string? authenticatedUrl;
    private bool isOn;
    private bool busy;
    private bool refreshing;
    private bool closing;
    private int lastPort = DefaultPort;
    private DateTime lastInstallInfoAt = DateTime.MinValue;
    private readonly CheckBox autoUpdateCheckbox = new();
    private readonly ToolTip tooltip = new();
    internal HarnessForm()
    {
        // 所有控件的坐标都是按 96 DPI 硬编码的像素值。没有这一行时，在 125%/150%
        // 缩放的机器上（同学的笔记本很常见）窗体会被整体拉伸而不是按比例重排，
        // 文字与控件会错位/被裁。Dpi 模式让 WinForms 按当前 DPI 重算尺寸。
        // 注意：本机是 96 DPI / 100% 缩放，这条改动在这里无法验证，只能保证方向正确。
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "DeepSeek Harness 控制台";
        ClientSize = new Size(560, 265);
        MinimumSize = new Size(560, 265);
        MaximumSize = new Size(560, 265);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.White;
        Font = UiFont;
        ApplyWindowIcon();

        var panel = new Panel
        {
            Location = new Point(22, 20),
            Size = new Size(424, 138),
            BackColor = Color.FromArgb(246, 248, 252),
            TabIndex = 0
        };
        Controls.Add(panel);

        lamp.Location = new Point(28, 39);
        lamp.Size = new Size(36, 36);
        lamp.Paint += DrawLamp;
        panel.Controls.Add(lamp);

        status.Location = new Point(82, 21);
        status.Size = new Size(320, 30);
        status.Font = StatusFont;
        status.Text = "检测中";
        status.TabIndex = 0;
        panel.Controls.Add(status);

        info.Location = new Point(84, 55);
        info.Size = new Size(320, 22);
        info.ForeColor = Color.FromArgb(108, 114, 126);
        info.TabIndex = 1;
        panel.Controls.Add(info);

        link.Location = new Point(83, 80);
        link.Size = new Size(320, 25);
        link.AutoEllipsis = true;
        link.TabIndex = 2;
        link.AccessibleName = "打开 DeepSeek Harness 控制台";
        link.LinkClicked += (_, _) => OpenKnownUrl();
        // 键盘用户：Tab 到链接后回车/空格也能打开，不能只依赖鼠标点。
        link.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Space)) return;
            e.Handled = true;
            OpenKnownUrl();
        };
        panel.Controls.Add(link);

        // 文案必须与真实行为一致：插件更新现在发生在引擎启动「之后」（并行/后台），
        // 且改的是 profile 的 node_modules，所以生效时机是下一次启动。
        autoUpdateCheckbox.Text = "启动后更新插件（下次生效）";
        autoUpdateCheckbox.Checked = LoadAutoUpdateSetting();
        autoUpdateCheckbox.AutoSize = true;
        autoUpdateCheckbox.Location = new Point(28, 110);
        autoUpdateCheckbox.ForeColor = Color.FromArgb(88, 94, 104);
        autoUpdateCheckbox.TabIndex = 3;
        autoUpdateCheckbox.CheckedChanged += (_, _) => SaveAutoUpdateSetting();
        panel.Controls.Add(autoUpdateCheckbox);

        // 引擎不再自动跟随最新版：升级只在这个按钮被点击时发生。
        // 按钮放在面板内，避免动到已排好的 5 个主按钮几何（窗体尺寸固定 560×265）。
        upgradeButton = new Button
        {
            Text = "升级引擎",
            Location = new Point(320, 105),
            Size = new Size(94, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(114, 122, 143),
            ForeColor = Color.White,
            Font = BoldFont,
            Cursor = Cursors.Hand
        };
        upgradeButton.FlatAppearance.BorderSize = 0;
        upgradeButton.AccessibleName = "升级 DSH 引擎到 npm 上的最新版本";
        upgradeButton.TabIndex = 2;
        upgradeButton.Click += async (_, _) => await RunEngineUpgradeAsync();
        panel.Controls.Add(upgradeButton);

        startButton = NewButton("开始", "启动 Harness 引擎并打开控制台。回车键等效", 22, Color.FromArgb(34, 170, 85));
        restartButton = NewButton("重启", "结束当前引擎并重新启动", 124, Color.FromArgb(238, 148, 32));
        stopButton = NewButton("停止", "停止引擎并释放端口", 226, Color.FromArgb(224, 69, 62));
        refreshButton = NewButton("刷新", "重新检测引擎状态", 328, Color.FromArgb(58, 124, 240));
        envButton = NewButton("环境", "检测 Node、npm、pnpm、引擎与端口", 430, Color.FromArgb(114, 122, 143));
        Controls.AddRange(new Control[] { startButton, restartButton, stopButton, refreshButton, envButton });

        startButton.Click += async (_, _) => await RunStartAsync(startButton, "启动中", reuseExisting: true);
        restartButton.Click += async (_, _) => await RunStartAsync(restartButton, "重启中", reuseExisting: false);
        stopButton.Click += async (_, _) => await StopClickedAsync();
        refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        envButton.Click += async (_, _) => await RunEnvCheckAsync();
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();

        // 键盘可达性：回车 = 开始，Esc = 停止（破坏性最小的那个）。
        AcceptButton = startButton;
        CancelButton = stopButton;
        ApplyAccessibility();
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
            // 之前这里只有 dshProcess?.Dispose()——Dispose 不结束进程，于是关窗后
            // 引擎继续在后台活着、占着端口，下次启动还能被"复用"到，泄漏被完全掩盖。
            // 关窗 = 退出，就把引擎一起结束；想让它留着跑请用「停止」之外的场景自行启动。
            StopEngineForExit();
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

        // 在动任何东西之前先给配置拍一份快照。实测 DSH 的 settings 迁移会丢掉
        // 不匹配 profile 条目 id 的配置段（jet-hub 账号、llm-pi-ai 供应商都中过招），
        // 备份必须发生在"可能被改写"之前，事后再备份就晚了。
        // 内容没变化时不会重复生成，所以正常启动几乎零成本。
        ConfigBackup.CreateSnapshot(reuseExisting ? "启动前" : "重启前");

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

            // 顺序很重要：先把引擎拉起来、把浏览器打开，再更新插件。
            // 此前是「pnpm update → 等它跑完 → 才启动引擎」，于是启动被硬生生推迟
            // 一整个 pnpm 往返（本机实测热 store 3.2 秒、GitHub 依赖超时 24 秒、
            // 首次拉依赖 3 分 37 秒），而这段时间用户盯着的只是一个没有任何进展的窗口。
            // 现在引擎启动与插件更新并行，插件的生效时机是「下次启动」——
            // 本来 pnpm update 也要重启才生效，所以这里没有语义损失。
            var startTask = StartHarnessAsync(cts.Token);
            var pluginTask = UpdatePluginsAfterStartAsync(cts.Token);
            await startTask;
            if (cts.IsCancellationRequested) return;

            if (autoUpdateCheckbox.Checked)
            {
                status.Text = "更新中";
                status.ForeColor = WarnColor;
                SetInfo("引擎已就绪，正在后台更新 DSH 插件…");
                lamp.Invalidate();
                var finished = await AwaitQuietlyAsync(pluginTask);
                if (cts.IsCancellationRequested) return;
                if (!finished) SetInfo("插件更新未正常结束，不影响本次使用");
                status.Text = busyText;
                status.ForeColor = WarnColor;
            }
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

    /// <summary>
    /// 插件更新任务：刻意不在 RunStartAsync 开头就 await，而是让它在引擎启动的同时
    /// 先跑起来（详见 RunStartAsync 里的顺序说明）。
    /// 未勾选复选框时立刻返回，避免无谓的 pnpm 往返。
    /// </summary>
    private async Task UpdatePluginsAfterStartAsync(CancellationToken ct)
    {
        if (!autoUpdateCheckbox.Checked) return;

        var last = ReadPluginUpdateStamp();
        if (last is not null)
        {
            var age = DateTime.Now - last.Value;
            if (age >= TimeSpan.Zero && age < TimeSpan.FromHours(PluginUpdateCooldownHours))
            {
                var hours = (int)age.TotalHours;
                SetInfo(hours >= 1
                    ? $"插件 {hours} 小时前已更新过，跳过（删 lastPluginUpdate.txt 可强制重跑）"
                    : "插件刚刚更新过，跳过");
                return;
            }
        }

        await UpdatePluginsAsync(ct);
    }

    /// <summary>
    /// 等待插件更新收敛：true = 正常结束，false = 抛了异常（已记录，不再上抛）。
    /// 后台更新失败绝不能让已经启动成功的引擎显示成"启动失败"，
    /// 所以这里把异常吃掉，只留一条信息提示。
    /// </summary>
    private static async Task<bool> AwaitQuietlyAsync(Task task)
    {
        try { await task; return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            try { AppendStartupLog("插件更新异常：" + ex.Message); } catch { }
            return false;
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
        var node = await ResolveNodeAsync(ct);
        if (ct.IsCancellationRequested) return;
        await EnsureEngineAsync(node, ct);
        if (ct.IsCancellationRequested) return;

        var profile = webProfileDir;
        // dsh 首次运行会自动初始化缺失的 profile（loadProfile 对无 package.json 的
        // 内置 profile 执行 initProfile）；这里只保证 WorkingDirectory 存在即可。
        var profileReady = File.Exists(Path.Combine(profile, "package.json"));
        var workDir = profileReady
            ? profile
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!profileReady)
            SetInfo("首次运行：正在初始化 profile（需联网），请稍候...");

        // 上一次启动若在持有 profiles\node_modules.lock 期间被杀，锁会残留成孤儿，
        // 之后每次启动都会在 2 秒后抛 "timed out waiting for the writer lock" 并退出。
        ClearOrphanProfileLock();

        // 端口被别的程序占用时，node 只会报 EADDRINUSE 然后立刻退出，
        // 界面最终显示的是"立即退出（代码 1）"——同学完全无从判断。
        // 这里在启动前先说清楚（走到这一步，本启动器自己的实例已被停干净）。
        if (IsPortListening(DefaultPort) && !await ProbeServerAsync(DefaultPort))
        {
            var hint = DescribeDynamicPortRange(DefaultPort);
            throw new InvalidOperationException(
                $"端口 {DefaultPort} 已被其他程序占用（不是本程序启动的 DSH）。\n" +
                $"请在命令行执行：netstat -ano | findstr :{DefaultPort}\n" +
                "找到占用进程后关闭它，再点「重启」。" +
                (hint is null ? string.Empty : "\n\n" + hint));
        }

        var psi = new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{EngineEntryScript}\" web --no-open --host 127.0.0.1 --port {DefaultPort}",
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
        var nodeDir = Path.GetDirectoryName(node);
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
        if (!process.Start()) throw new InvalidOperationException("无法启动 DSH 引擎。");
        dshProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var started = DateTime.UtcNow;
        var deadline = started + TimeSpan.FromSeconds(StartTimeoutSeconds);
        var portAppeared = false;
        var lastHint = started;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return;
            if (authenticatedUrl is not null) return;

            if (ProcessHasExited(process))
            {
                // 进程刚退出的瞬间，stderr/stdout 的异步读取常常还没送达，
                // 直接取摘要会得到"未输出任何日志"——把真正有用的报错丢掉。
                // 这里等一小会儿让管道排空，同时把"我们自己的进程被释放了"这种情况
                // 与"引擎真的崩了"区分开。
                try { await Task.Delay(300, ct); } catch (OperationCanceledException) { }
                var code = SafeExitCode(process);
                var codeText = code >= 0
                    ? $"代码 {code}"
                    : "未知（进程句柄已被释放——通常是启动器自身在清理该进程，而不是引擎崩溃）";
                var summary = RecentOutputSummary();
                if (summary.Contains("未输出任何日志"))
                {
                    summary += "\n（提示：若引擎是被杀掉的，它来不及输出；可点「环境」查看引擎与 Node 状态，" +
                              $"或手动执行 node \"{EngineEntryScript}\" web --no-open --port {DefaultPort} 复现）";
                }
                throw new InvalidOperationException(
                    $"DeepSeek Harness 立即退出（{codeText}）。\n{summary}\n请检查 Node 与引擎安装（可点「环境」自检）。");
            }

            if (!portAppeared && IsPortListening(DefaultPort)) portAppeared = true;

            // 引擎在固定目录里，正常启动十几秒即可；慢通常来自 DSH 自身重建
            // profiles\node_modules 链接（首次或引擎刚升级时约几十秒）。
            if (!portAppeared && DateTime.UtcNow - lastHint >= TimeSpan.FromSeconds(5))
            {
                lastHint = DateTime.UtcNow;
                var waited = (int)(DateTime.UtcNow - started).TotalSeconds;
                SetInfo($"正在启动…已等待 {waited} 秒（首次或引擎刚升级时 DSH 需重建模块链接）");
            }

            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return; }
        }

        if (ct.IsCancellationRequested) return;

        throw new TimeoutException(portAppeared
            ? $"端口 {DefaultPort} 已监听，但 {StartTimeoutSeconds} 秒内未捕获到认证链接。\n{RecentOutputSummary()}\n请点击“重启”重试。"
            : $"等待 DeepSeek Harness Web 服务超时（{StartTimeoutSeconds} 秒）。\n" +
              $"{RecentOutputSummary()}\n请点击“重启”重试。");
    }

    private static bool IsNoiseLine(string text)
    {
        foreach (var ch in text)
            if (ch is not ('.' or '-' or '\u00b7' or '\u2022') && !char.IsWhiteSpace(ch)) return false;
        return true;
    }

    private string RecentOutputSummary()
    {
        lock (recentOutput)
        {
            if (recentOutput.Count == 0) return "（DSH 未输出任何日志）";
            return "最后输出：\n" + string.Join("\n", recentOutput);
        }
    }

    private void HandleProcessLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = AuthUrlRegex.Match(line);
        if (!match.Success)
        {
            // 非认证链接行：界面留一份进度、内部留一份尾部日志。
            // 之前这些行一律被丢弃，导致 npx 冷装或 DSH 报错期间界面完全看不出在干什么。
            var text = AnsiRegex.Replace(line, string.Empty).Trim();
            if (text.Length == 0) return;
            lock (recentOutput)
            {
                recentOutput.Add(text);
                while (recentOutput.Count > RecentOutputLines) recentOutput.RemoveAt(0);
            }
            if (busy && !IsNoiseLine(text))
                SetInfo(text.Length > 96 ? text[..96] + "…" : text);
            return;
        }

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
                var engine = ReadEngineVersion(engineDir);
                info.Text = engine is null
                    ? "引擎未安装，点击“开始”会自动安装"
                    : $"引擎 {engine} · 点击“开始”启动";
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

    /// <summary>
    /// 关窗退出时结束引擎。与「停止」按钮的区别：这里必须**快**，因为它在窗体的
    /// 关闭路径上，阻塞会让窗口卡住不消失。
    /// 先直接杀掉已知的进程树（不查 WMI），再补一次定向清扫——只扫
    /// **命令行里带本启动器引擎目录**的 node，用来兜住在开始菜单/别处启动、
    /// 或本实例没记录到的残留。锁文件不存在（绝大多数情况）时连这次清扫都跳过。
    /// </summary>
    private void StopEngineForExit()
    {
        try
        {
            if (dshProcess is not null && !ProcessHasExited(dshProcess))
                dshProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { AppendStartupLog("退出时结束引擎失败：" + ex.Message); }
        try { dshProcess?.Dispose(); } catch { }
        dshProcess = null;

        try
        {
            foreach (var record in GetProcessRecords().Values.Where(IsEngineProcess))
            {
                try { Process.GetProcessById(record.Id).Kill(entireProcessTree: true); }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 只认「命令行里带本启动器引擎目录」的进程，用于退出清扫。
    /// 与 IsHarnessCommand 同样必须先排除桌面客户端：它的引擎宿主命令行里
    /// 也含 @deepseek-ai/dsh，但那是客户端自己的，不是我们启动的。
    /// </summary>
    private bool IsEngineProcess(ProcessRecord p)
    {
        if (p.Name.Equals("DeepSeekHarness.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.CommandLine.Contains("app.asar", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.CommandLine.Contains("dsh-desktop-host", StringComparison.OrdinalIgnoreCase)) return false;
        return p.CommandLine.Contains(engineDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 全进程快照（含命令行）。WMI 带 CommandLine 的全量查询在本机实测约 140 ms，
    /// 而一次启动里 StopHarnessProcessesAsync 与 ClearOrphanProfileLock 会各要一份；
    /// 缓存 1 秒即可让两者共用同一次查询，又不至于让快照过期到影响"找出残留进程"的准确性。
    /// </summary>
    private static Dictionary<int, ProcessRecord>? processRecordCache;
    private static DateTime processRecordCacheAt = DateTime.MinValue;

    private static Dictionary<int, ProcessRecord> GetProcessRecords()
    {
        var now = DateTime.UtcNow;
        if (processRecordCache is not null && (now - processRecordCacheAt) < TimeSpan.FromSeconds(1))
            return processRecordCache;

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
        processRecordCache = result;
        processRecordCacheAt = now;
        return result;
    }

    private static bool IsHarnessCommand(ProcessRecord p)
    {
        if (p.Name.Equals("DeepSeekHarness.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var c = p.CommandLine;

        // —— 绝不能杀的目标：DSH 桌面客户端（Electron）自己的引擎宿主 ——
        // 客户端不是 node.exe，而是用它自己的可执行文件跑宿主：
        //   "…\DeepSeek Harness.exe" --expose-internals
        //   …\resources\app.asar\dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\index.js
        // 这条命令行里同时含 `app.asar\dsh` 和 `\dsh\`+空白+web，会被下面的宽泛正则
        // 误判成"残留的 dsh web 进程"，然后整树杀掉——而宿主下挂着客户端的 renderer，
        // 于是用户正开着的客户端直接白屏崩溃（实测复现：crash-*-renderer.log 两份）。
        // 启动器只需要管自己拉起的引擎，客户端的引擎归客户端管。
        if (c.Contains("app.asar", StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Contains("dsh-desktop-host", StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Contains("dsh-subprocess", StringComparison.OrdinalIgnoreCase)) return false;

        // 本启动器自己安装的引擎：命令行里必然带这个目录。这是最精确的一条。
        if (c.Contains(engineDir, StringComparison.OrdinalIgnoreCase)) return true;

        // 其余宽松匹配只对 node / npx 生效。此前对任意进程名都套用，
        // 一个恰好含 "dsh web" 字样的非 node 进程也会被整树杀掉。
        var isNode = p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase);
        var isNpx = p.Name.Equals("npx.cmd", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals("npx.exe", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
        if (!isNode && !isNpx) return false;

        if (c.Contains("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("@deepseek-ai\\dsh", StringComparison.OrdinalIgnoreCase)) return true;

        return Regex.IsMatch(c, @"(?i)(^|[\\/\s])dsh(?:\.cmd)?(?:[\\/](?:lib|bin))?\s+(?:web|--profile\s+web)\b") ||
               Regex.IsMatch(c, @"(?i)\bnpx(?:\.cmd)?\b.*\b(?:@deepseek-ai[\\/]dsh|dsh)\b");
    }

    /// <summary>
    /// dsh 用独占创建（wx）的 `profiles\node_modules.lock` 串行化共享层修复写入，
    /// 默认只等 2000ms，并且明确不回收孤儿锁（"orphan recovery is an operator action"）。
    /// 持锁进程被杀（本启动器的停止/重启/超时 kill 都会）就会残留锁文件，
    /// 此后每次启动都会在 2 秒后抛 "timed out waiting for the writer lock" 并退出，
    /// 表现为"一直在加载然后失败"。启动器就是那个 operator：确认没有存活的
    /// dsh/npx 进程后清掉它。
    /// </summary>
    private static void ClearOrphanProfileLock()
    {
        try
        {
            var lockPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "profiles", "node_modules.lock");
            // 绝大多数启动这里根本没有锁文件：先判存在再决定要不要付出全量进程扫描的代价。
            if (!File.Exists(lockPath)) return;
            if (GetProcessRecords().Values.Any(IsHarnessCommand)) return;
            File.Delete(lockPath);
        }
        catch { }
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

    /// <summary>
    /// 端口是否已被监听。此前用 GetActiveTcpListeners()：它在启动等待循环里每 250 ms
    /// 被调用一次，每次都分配并枚举整张 TCP 表（本机实测 22 ms / 29 个端点，
    /// 进程一多就更贵）。改成对回环地址做一次定向连接探测，成本低一个数量级。
    /// 副作用与旧实现一致：只关心确有进程在该端口 accept。
    /// </summary>
    private static bool IsPortListening(int port) => IsTcpOpen("127.0.0.1", port, 200);

    /// <summary>
    /// 复刻旧实现的返回结构，供「环境」按钮列出当前监听端口。
    /// 注意本方法会分配整张 TCP 表，只在手动检测路径上调用，不要放进轮询循环。
    /// </summary>
    private static IEnumerable<int> ListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Select(x => x.Port).Distinct().ToArray();
        }
        catch { return Array.Empty<int>(); }
    }

    /// <summary>
    /// Windows 在 Hyper-V / WSL / Docker Desktop 启用后会预留一大段动态端口范围
    /// （典型 49152–65535）。出网连接会随机占用这段里的端口，同时保持 4 分钟
    /// TIME_WAIT，于是"端口莫名被占"且 netstat 里找不到可疑程序。
    /// 报错信息里点出这一层，能省掉大量排查时间。
    /// </summary>
    private static string? DescribeDynamicPortRange(int port)
    {
        var range = GetDynamicPortRange();
        if (range is null) return null;
        var (start, count) = range.Value;
        if (port < start || port >= start + count) return null;
        return $"注意：{port} 落在 Windows 动态端口范围内（{start}–{start + count - 1}），" +
               "启用 Hyper-V / WSL / Docker Desktop 后这段会被系统预留，" +
               "出网连接可能随机占用其中端口。\n" +
               "可执行 netsh int ipv4 show excludedportrange protocol=tcp 查看预留段，" +
               "或先执行 net stop winnat & net start winnat 释放。";
    }

    private static (int Start, int Count)? dynamicPortRange;
    private static bool dynamicPortRangeRead;

    private static (int Start, int Count)? GetDynamicPortRange()
    {
        if (dynamicPortRangeRead) return dynamicPortRange;
        dynamicPortRangeRead = true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"netsh int ipv4 show dynamicport tcp\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var text = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var startMatch = Regex.Match(text, @"起始端口\s*:\s*(\d+)|Start Port\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            var countMatch = Regex.Match(text, @"端口数\s*:\s*(\d+)|Number of Ports\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (!startMatch.Success || !countMatch.Success) return null;
            var start = int.Parse(startMatch.Groups[1].Success ? startMatch.Groups[1].Value : startMatch.Groups[2].Value);
            var count = int.Parse(countMatch.Groups[1].Success ? countMatch.Groups[1].Value : countMatch.Groups[2].Value);
            dynamicPortRange = (start, count);
            return dynamicPortRange;
        }
        catch { return null; }
    }

    /// <summary>
    /// Node 工具目录（node / npm / pnpm / corepack 都在这里）的查找顺序：
    /// ① 进程 PATH（系统里已装好的优先，最符合直觉）
    /// ② 启动器旁边的 node\（便携式分发：把 Node 目录跟 exe 放一起就能用）
    /// ③ 标准安装位置 %ProgramFiles%\nodejs 与 %APPDATA%\npm（npm 全局命令装在这）
    /// ④ 可选的用户自定义目录（%LOCALAPPDATA%\DeepSeekHarness\node-dir.txt）
    ///
    /// 这里刻意不含任何"本机专属"路径：分发给别人时不需要改代码，
    /// 也不会在别人的机器上凭空去探一个不存在的盘符目录。
    /// </summary>
    private static IEnumerable<string> ToolSearchDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length > 0 && seen.Add(dir)) yield return dir;
        }

        var fixedDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "node"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
        };
        foreach (var dir in fixedDirs)
            if (!string.IsNullOrWhiteSpace(dir) && seen.Add(dir)) yield return dir;

        var custom = ReadNodeDirOverride();
        if (custom is not null && seen.Add(custom)) yield return custom;
    }

    /// <summary>Node 装在非标准目录时，用户可以把那个目录写进 node-dir.txt。</summary>
    private static string? ReadNodeDirOverride()
    {
        try
        {
            var file = Path.Combine(LocalAppDir, "node-dir.txt");
            if (!File.Exists(file)) return null;
            var dir = File.ReadAllText(file).Trim();
            return dir.Length > 0 && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private static string? FindTool(string exeName) => FindAllTools(exeName, 1).FirstOrDefault();

    /// <summary>
    /// 收集所有候选（按 ToolSearchDirs 的优先级），最多 limit 个。
    /// 用于"机器上有多份 Node，挑一个够新的"这种场景。
    /// </summary>
    private static List<string> FindAllTools(string exeName, int limit)
    {
        var hits = new List<string>();
        foreach (var dir in ToolSearchDirs())
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate) && !hits.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    hits.Add(candidate);
                if (hits.Count >= limit) break;
            }
            catch { }
        }
        return hits;
    }

    /// <summary>优先取与被选中 node.exe 同目录的工具，保证 node/npm/pnpm 是同一套安装。</summary>
    private static string? FindToolBeside(string toolPath, string exeName)
    {
        try
        {
            var dir = Path.GetDirectoryName(toolPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var candidate = Path.Combine(dir, exeName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch { return null; }
    }

    private static int ParseMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return -1;
        var text = version.Trim().TrimStart('v', 'V');
        return int.TryParse(text.Split('.')[0], out var major) ? major : -1;
    }

    /// <summary>
    /// 选一个可用的 node.exe。别人的机器上常常同时存在多份 Node（以前装过的旧版 +
    /// 后来装的新版，PATH 里排前面的未必是新的），所以这里按优先级逐个试版本号，
    /// 取第一个 ≥ 18 的；全都不够新时给出明确提示，而不是让 DSH 以难懂的方式失败。
    /// 读不到版本号（非标准安装）时不阻塞，直接采用。
    /// </summary>
    private static async Task<string> ResolveNodeAsync(CancellationToken ct)
    {
        var candidates = FindAllTools("node.exe", 4);
        if (candidates.Count == 0)
            throw new FileNotFoundException(
                "未找到 node.exe。请先安装 Node.js LTS（18 或更高，https://nodejs.org），" +
                "安装完成后重新打开本程序。\n" +
                $"若 Node 装在非标准目录，可把该目录写进：{Path.Combine(LocalAppDir, "node-dir.txt")}",
                "node.exe");

        string? tooOldPath = null;
        string? tooOldVersion = null;
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) return candidates[0];
            var version = await GetToolVersionAsync(candidate, "--version");
            var major = ParseMajorVersion(version);
            if (major < 0) return candidate;          // 读不到版本：当作可用
            if (major >= 18) return candidate;
            tooOldPath ??= candidate;
            tooOldVersion ??= version;
        }

        throw new InvalidOperationException(
            $"找到的 Node.js 版本过低（{tooOldVersion?.Trim()}，路径 {tooOldPath}）。\n" +
            "DeepSeek Harness 需要 Node.js 18 或更高版本。\n" +
            "请到 https://nodejs.org 安装最新 LTS；若机器上有多个 Node，\n" +
            $"可把新版所在目录写进：{Path.Combine(LocalAppDir, "node-dir.txt")}");
    }

    private static string ResolveNpmPath(string? nodePath = null)
    {
        var found = (nodePath is not null ? FindToolBeside(nodePath, "npm.cmd") : null) ?? FindTool("npm.cmd");
        if (found is not null) return found;
        throw new FileNotFoundException(
            "未找到 npm.cmd。npm 随 Node.js 一起安装，请重装 Node.js LTS（18 或更高）。", "npm.cmd");
    }

    private static string? ResolvePnpmPath(string? nodePath = null) =>
        (nodePath is not null ? FindToolBeside(nodePath, "pnpm.cmd") : null) ?? FindTool("pnpm.cmd");

    /// <summary>corepack 是随 Node 附带的包管理 shim（Node ≥ 16.9），没有真实 pnpm 时退到它。</summary>
    private static string? ResolveCorepackPath(string? nodePath = null) =>
        (nodePath is not null ? FindToolBeside(nodePath, "corepack.cmd") : null) ?? FindTool("corepack.cmd");

    // ---- 引擎安装与升级 -----------------------------------------------------

    private string EngineEntryScript => Path.Combine(
        engineDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

    private static string? ReadEngineVersion(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(manifest)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 保证固定目录里有一份可用的引擎。只做本地检查：不联网、不查最新版、不重装。
    /// 引擎由「升级引擎」按钮显式升级，因此这里在正常启动路径上几乎零成本。
    /// </summary>
    private async Task EnsureEngineAsync(string node, CancellationToken ct)
    {
        RecoverEngineSwap();
        if (File.Exists(EngineEntryScript) && ReadEngineVersion(engineDir) is not null) return;

        SetInfo("首次运行：正在把 DSH 引擎装到固定目录（只此一次，之后启动不再联网）");

        // 版本锁优先：写了 engine-version.txt 就只装那个版本，永不跟随最新版。
        // 这是"以插件为主"的开关——插件只在某个引擎版本上验证过时，把它钉住。
        var pinned = ReadPinnedEngineVersion();
        if (pinned is not null)
        {
            SetInfo($"检测到引擎版本锁，安装指定版本 {pinned}");
            await InstallEngineAsync(node, pinned, ResolveNpmRegistry(node, ct), ct);
            return;
        }

        // 否则先查一次精确版本号再装。原因：写 "latest" 的话重装会忽略锁文件、抓当天最新版，
        // 同一份 manifest 在不同日子装出不同版本，出了问题无法复现。
        // 查询用独立的短超时（20 秒）——实测安装本身要 67 秒，不能在查询上再赔 60 秒；
        // 查不到只是退回 latest，不影响安装成功。
        string? latest = null;
        try
        {
            using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            queryCts.CancelAfter(TimeSpan.FromSeconds(20));
            latest = await GetLatestEngineVersionAsync(node, queryCts.Token);
        }
        catch (OperationCanceledException) { }
        if (ct.IsCancellationRequested) return;
        if (latest is null) SetInfo("查不到版本号，按 latest 安装（网络受限时的降级路径）");
        await InstallEngineAsync(node, latest ?? "latest", ResolveNpmRegistry(node, ct), ct);
    }

    private async Task<string?> ActivateEngineVersionAsync(string version)
    {
        try
        {
            var slot = EngineSlotDirFor(version);
            if (!Directory.Exists(slot))
                return $"找不到版本 {version} 的目录：\n{slot}";
            if (!File.Exists(Path.Combine(slot, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")))
                return $"版本 {version} 的目录不完整（缺少 bin.js），无法切换。\n{slot}";

            var active = ReadEngineVersion(engineDir);
            if (string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
                return null;

            // 运行中的实例占着引擎文件，先停干净再改名。
            await StopHarnessProcessesAsync();
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(8), CancellationToken.None);

            ForceDeleteDirectory(engineStageDir);
            if (Directory.Exists(engineDir)) Directory.Move(engineDir, engineStageDir);
            Directory.Move(slot, engineDir);
            // 原活动版本搬到它的版本槽；这步失败就把新版本退回去，不留下"没有引擎"的状态。
            try
            {
                if (active is not null) Directory.Move(engineStageDir, EngineSlotDirFor(active));
            }
            catch (Exception ex)
            {
                AppendStartupLog($"切换后归档旧版本失败（{active}）：{ex.Message}");
                ForceDeleteDirectory(engineStageDir);
            }
            AppendStartupLog($"引擎版本已切换到 {version}");
            return null;
        }
        catch (Exception ex)
        {
            AppendStartupLog($"切换引擎版本失败：{ex.Message}");
            return "切换失败：" + ex.Message;
        }
    }

    private void DeleteEngineVersion(string version)
    {
        try
        {
            var active = ReadEngineVersion(engineDir);
            if (string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("不能删除正在使用的版本。请先切换到其他版本。",
                    "删除引擎版本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var slot = EngineSlotDirFor(version);
            if (!Directory.Exists(slot)) return;
            SetInfo($"正在删除引擎 {version}…");
            ForceDeleteDirectory(slot);
            AppendStartupLog($"已删除引擎版本 {version}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("删除失败：" + ex.Message, "删除引擎版本", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- 引擎版本槽 ---------------------------------------------------------

    private const string EngineSlotPrefix = "engine.";

    private static string EngineSlotDirFor(string version) => Path.Combine(LocalAppDir, EngineSlotPrefix + version);

    /// <summary>目录占用，MB 粒度。用 EnumerateFiles 避免一次性把所有 FileInfo 建出来。</summary>
    private static long DirectorySizeBytes(string dir)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 把升级后留下的 engine.old 提升为一个正式的版本槽。
    /// 升级流程仍然用 engine.old 作中转（改名失败可以靠它恢复），
    /// 这里在下次启动时把它归档成 engine.&lt;版本号&gt;，于是旧版本不会被动丢掉了。
    /// </summary>
    private static void MigrateEngineOldToSlot()
    {
        try
        {
            if (!Directory.Exists(engineOldDir)) return;
            // 活动版本缺失时这本该由 RecoverEngineSwap 处理，别在这里抢着归档。
            if (!Directory.Exists(engineDir)) return;

            var oldVersion = ReadEngineVersion(engineOldDir);
            if (oldVersion is null)
            {
                // 读不出版本（装了一半）：不能确定它属于哪个槽，保守地留着让用户自己决定。
                AppendStartupLog("engine.old 无法读出引擎版本，保留原样未归档");
                return;
            }

            if (string.Equals(ReadEngineVersion(engineDir), oldVersion, StringComparison.OrdinalIgnoreCase))
            {
                // 与活动版本同一个版本号，留两份纯属浪费磁盘。
                ForceDeleteDirectory(engineOldDir);
                return;
            }

            var slot = EngineSlotDirFor(oldVersion);
            ForceDeleteDirectory(slot);
            Directory.Move(engineOldDir, slot);
            AppendStartupLog($"已把上一版本 {oldVersion} 归档为可切换版本");
        }
        catch (Exception ex)
        {
            AppendStartupLog("归档 engine.old 失败：" + ex.Message);
        }
    }

    internal IReadOnlyList<EngineVersionEntry> GetInstalledEngineVersions()
    {
        MigrateEngineOldToSlot();
        var result = new List<EngineVersionEntry>();

        var activeVersion = ReadEngineVersion(engineDir);
        if (activeVersion is not null)
        {
            result.Add(new EngineVersionEntry(
                activeVersion, engineDir, true,
                DirectorySizeBytes(engineDir),
                Directory.GetCreationTime(engineDir)));
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(LocalAppDir, EngineSlotPrefix + "*"))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith(EngineSlotPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var version = name[EngineSlotPrefix.Length..];
                if (version.Length == 0) continue;
                // "engine.old" / "engine.tmp" 不是版本槽，跳过。
                if (version is "old" or "tmp") continue;
                if (!File.Exists(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"))) continue;
                result.Add(new EngineVersionEntry(
                    version, dir, false,
                    DirectorySizeBytes(dir),
                    Directory.GetCreationTime(dir)));
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 上次替换若在两步之间被打断（engine 已改名、staging 还没顶上），
    /// 这里把 engine.old 改回来，避免出现"引擎凭空消失"。
    /// 注意顺序：必须先跑这个，再让 MigrateEngineOldToSlot 把 engine.old 归档成版本槽，
    /// 否则"活动目录缺失"的中间态会被归档动作掩盖掉。
    /// </summary>
    private void RecoverEngineSwap()
    {
        try
        {
            if (!Directory.Exists(engineDir) && Directory.Exists(engineOldDir))
                Directory.Move(engineOldDir, engineDir);
        }
        catch { }
    }

    /// <summary>
    /// 把引擎装到 staging 目录，全部成功后才替换正式目录。
    /// staging 的意义：安装被中断或失败时，正在能用的那一份永远不受影响
    /// （npx 那条路做不到——半成品残骸原地修不好，只能整体删掉重下）。
    /// 替换用"旧目录先改名、staging 顶上、再删旧目录"三步，任一步失败都能恢复。
    /// </summary>
    private async Task InstallEngineAsync(string node, string versionSpec, string registry, CancellationToken ct)
    {
        var npm = ResolveNpmPath(node);
        ForceDeleteDirectory(engineStageDir);
        Directory.CreateDirectory(engineStageDir);

        // 预置最小 package.json：npm 在清单齐全的目录里会写 package-lock.json，
        // 配合 --prefer-offline 命中本地 cacache，重装基本不重新下载。
        // 这里写的 spec 就是最终 spec（调用方传精确版本号，不是 latest/caret），
        // 再配合下面的 --save-exact，manifest 与 lock 才会真正一致 → 装出来的版本可复现。
        File.WriteAllText(
            Path.Combine(engineStageDir, "package.json"),
            "{\n  \"name\": \"dsh-engine\",\n  \"private\": true,\n  \"version\": \"0.0.0\",\n  \"dependencies\": {\n    \"" +
            EnginePackageName + "\": \"" + versionSpec + "\"\n  }\n}\n",
            new UTF8Encoding(false));

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"\"{npm}\" install --prefer-offline --no-audit --no-fund --save-exact --registry {registry} --loglevel=http\"",
            WorkingDirectory = engineStageDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var nodeDir = Path.GetDirectoryName(node);
        var path = psi.Environment["PATH"] ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(nodeDir) && !path.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(nodeDir, StringComparer.OrdinalIgnoreCase))
            psi.Environment["PATH"] = nodeDir + ";" + path;
        ConfigureOptionalProxy(psi);

        using var installCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        installCts.CancelAfter(TimeSpan.FromSeconds(EngineInstallTimeoutSeconds));

        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => TrackInstallLine(e.Data);
            proc.ErrorDataReceived += (_, e) => TrackInstallLine(e.Data);
            if (!proc.Start()) throw new InvalidOperationException("无法启动 npm。");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(installCts.Token);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"引擎安装失败（npm 退出码 {proc.ExitCode}）。\n{RecentOutputSummary()}");

            var staged = ReadEngineVersion(engineStageDir);
            if (staged is null)
                throw new InvalidOperationException(
                    "安装结束但引擎清单不可读，已放弃替换（当前引擎未受影响）。\n" + RecentOutputSummary());

            // 替换三步：清掉上上次的 engine.old → 现行目录改名 → staging 顶上。
            // 刻意**不删**换下来的 engine.old：升级后发现插件不兼容时，
            // 把 engine 删掉、engine.old 改名回来即可完整回退到上一个能用的版本。
            // 代价是引擎目录占双份（实测每份约 214 MB），换来的是可回退。
            ForceDeleteDirectory(engineOldDir);
            if (Directory.Exists(engineDir)) Directory.Move(engineDir, engineOldDir);
            Directory.Move(engineStageDir, engineDir);
            SetInfo($"引擎已就绪：{staged}（上一版本保留在 engine.old，可回退）");
        }
        catch (OperationCanceledException)
        {
            // 取消/超时：必须杀掉 npm 整棵树，否则它会留在后台继续装
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            ForceDeleteDirectory(engineStageDir);
            throw;
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
        }
    }

    /// <summary>npm 安装期间的进度：同时喂给界面提示和"最后 N 行"错误摘要。</summary>
    private void TrackInstallLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var text = AnsiRegex.Replace(line, string.Empty).Trim();
        if (text.Length == 0) return;
        lock (recentOutput)
        {
            recentOutput.Add(text);
            while (recentOutput.Count > RecentOutputLines) recentOutput.RemoveAt(0);
        }
        // npm --loglevel=http 每行都很密，节流到 200ms 一次，避免频繁跨线程刷新界面。
        if ((DateTime.UtcNow - lastInstallInfoAt).TotalMilliseconds < 200) return;
        lastInstallInfoAt = DateTime.UtcNow;
        SetInfo(text.Length > 96 ? text[..96] + "…" : text);
    }

    /// <summary>
    /// 引擎安装/查询用哪个 registry。
    /// 之前完全依赖 npm 自己的默认值，于是国内用户即使系统里已经配了镜像
    /// （npm config set registry https://registry.npmmirror.com），
    /// 安装与「升级引擎」的版本查询仍可能走 registry.npmjs.org，
    /// 实测查询会打满 EngineQueryTimeoutSeconds（60 秒）才失败。
    /// 这里读出用户的实际配置并显式传给 npm，让"配了镜像就真的生效"。
    /// </summary>
    private static string? effectiveRegistry;

    private static string ResolveNpmRegistry(string node, CancellationToken ct)
    {
        if (effectiveRegistry is not null) return effectiveRegistry;
        try
        {
            var npm = ResolveNpmPath(node);
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c \"\"{npm}\" config get registry\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var text = proc.StandardOutput.ReadToEnd();
                if (proc.WaitForExit(5000))
                {
                    var value = text.Trim().Split('\n').Last().Trim();
                    if (IsSafeNpmValue(value)) effectiveRegistry = value;
                }
                else
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { }
        return effectiveRegistry ?? DefaultRegistry;
    }

    /// <summary>
    /// 需要拼进 cmd 命令行的值必须校验：含引号/空白/换行都可能把命令行拆坏。
    /// registry 是用户可改的 npm 配置，不能无条件信任。
    /// </summary>
    private static bool IsSafeNpmValue(string value) =>
        value.Length is > 0 and < 512 &&
        !value.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '&' or '|' or '<' or '>' or '^' or '%');

    /// <summary>
    /// 引擎版本锁。写了这个文件，启动器就只装/只用该版本，永不自动跟进新版——
    /// 对应"以插件为主、软件迁就插件"的需求：插件只在某个引擎版本上验证过时，
    /// 把该版本写进去即可把软件钉死。清空该文件即恢复跟随最新版。
    /// </summary>
    private static readonly string engineVersionPinFile = Path.Combine(LocalAppDir, "engine-version.txt");

    private static string? ReadPinnedEngineVersion()
    {
        try
        {
            if (!File.Exists(engineVersionPinFile)) return null;
            var value = File.ReadAllText(engineVersionPinFile).Trim();
            return value.Length > 0 ? value : null;
        }
        catch { return null; }
    }

    // ---- 引擎升级前的插件兼容性检查 ----------------------------------------
    //
    // 背景（实测本机）：profile 只声明 6 个插件依赖，@deepseek-ai/* 一个都不声明，
    // 全部由引擎提供。所以升级引擎不会动插件文件，但插件声明的 peer 要求
    // 是针对特定引擎版本写的——例如皮肤插件要求 @deepseek-ai/dsh >=0.1.7-rc.1，
    // 而当时引擎是 0.1.5-rc.2，属于"能跑但插件不被满足"的状态。
    // 升级前把这类不满足列出来，用户才知道点下去会不会让插件失效。

    /// <summary>某插件对某个 DSH 包的版本要求；没有声明则为 null。</summary>
    private sealed record PluginRequirement(string Plugin, string Package, string Range);

    private static List<PluginRequirement> CollectPluginRequirements(string profileDir)
    {
        var found = new List<PluginRequirement>();
        var modules = Path.Combine(profileDir, "node_modules");
        var scopes = new[] { "@deepseek-ai", "@dsh-external", "" };
        foreach (var scope in scopes)
        {
            var dir = scope.Length == 0 ? modules : Path.Combine(modules, scope);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> entries;
            try
            {
                entries = scope.Length == 0
                    ? Directory.GetDirectories(dir).Where(d => Path.GetFileName(d).StartsWith("dsh-", StringComparison.OrdinalIgnoreCase))
                    : Directory.GetDirectories(dir);
            }
            catch { continue; }

            foreach (var entry in entries)
            {
                var manifest = Path.Combine(entry, "package.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (!doc.RootElement.TryGetProperty("peerDependencies", out var peers)) continue;
                    var fallbackName = Path.GetFileName(entry);
                    foreach (var peer in peers.EnumerateObject())
                    {
                        // 只看"由引擎提供"的那批包：@deepseek-ai/* 以及 React 这类外部 peer 不算。
                        if (!peer.Name.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (peer.Value.ValueKind != JsonValueKind.String) continue;
                        var range = peer.Value.GetString();
                        if (string.IsNullOrWhiteSpace(range)) continue;
                        var pluginName = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? fallbackName
                            : fallbackName;
                        found.Add(new PluginRequirement(pluginName, peer.Name, range));
                    }
                }
                catch { }
            }
        }
        return found;
    }

    /// <summary>把 "0.2.0-rc.2" 解析成可比较的四段版本；解析不了返回 null。</summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) core = core[..cut];
        var parts = core.Split('.');
        if (parts.Length is < 3 or > 4) return null;
        var nums = new int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i])) return null;
        return new Version(nums[0], nums[1], nums[2], nums.Length > 3 ? nums[3] : 0);
    }

    /// <summary>S1 &gt; S2 → 1；相等 → 0；S1 &lt; S2 → -1；无法比较 → null。</summary>
    private static int? CompareVersionStrings(string a, string b)
    {
        var va = ParseVersion(a);
        var vb = ParseVersion(b);
        if (va is null || vb is null) return null;

        // 先比数字段。只有数字段完全相同、才需要看预发布标识——
        // semver 里预发布只影响"数字段相同"时的排序（0.1.5-rc.2 < 0.1.7-rc.1 就是因为 5 < 7，
        // 与 rc 无关）。之前把预发布比较写成了无条件分支，导致只要两边预发布标识不同
        // 就返回"无法判定"，于是 >=0.1.7-rc.1 这类判断全部落空、兼容性检查会漏报。
        var core = va.CompareTo(vb);
        if (core != 0) return Math.Sign(core);

        // 数字段相同：正式版 > 预发布版；两边都是预发布且标识不同时，返回 null。
        // 预发布标识符的逐段比较（rc.1 vs rc.2 vs beta）规则繁琐且本工具用不上，
        // 拿不准就说拿不准，返回 null 让调用方按"未知"处理，绝不猜。
        static string? Pre(string s)
        {
            var i = s.IndexOf('-');
            if (i < 0) return null;
            var rest = s[(i + 1)..];
            var plus = rest.IndexOf('+');
            return plus < 0 ? rest : rest[..plus];
        }

        var pa = Pre(a.Trim());
        var pb = Pre(b.Trim());
        if (pa is null && pb is null) return 0;
        if (pa is null) return 1;
        if (pb is null) return -1;
        return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase) ? 0 : null;
    }

    /// <summary>
    /// 判定 candidateVersion 是否满足声明的范围。支持的形式：
    /// ">=0.1.7-rc.1"、"&gt;=0.1.7-rc.1 &lt;0.3.0-0"、"^4.0.1"、"A || B"。
    /// 三态返回：true 满足 / false 明确不满足 / null 无法判定。
    ///
    /// 这里必须把"明确不满足"和"无法判定"分开：之前只要有一个 token 判定不出来
    /// 就整体返回 null，导致"已知不兼容"被降级成"未知"，护栏会漏报。
    /// 规则：某个候选项的全部 token 都满足 → true；某个候选项里有 token 明确不满足
    /// （且没有无法判定的 token 挡在前面）→ 这个候选项为 false；所有候选项都为 false → false；
    /// 否则（存在无法判定的部分）→ null。
    /// </summary>
    private static bool? SatisfiesRange(string candidate, string range)
    {
        var anyAlternativeFailed = false;
        foreach (var alternative in range.Split("||", StringSplitOptions.RemoveEmptyEntries))
        {
            var allSatisfied = true;   // 目前为止每个 token 都满足
            var anyUnknown = false;    // 出现过无法判定的 token
            foreach (var token in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var verdict = SatisfiesSingle(candidate, token);
                if (verdict is null) { anyUnknown = true; break; }
                if (verdict.Value) continue;
                allSatisfied = false;
                break;
            }
            if (allSatisfied && !anyUnknown) return true;   // 该候选项确定满足
            if (!anyUnknown && !allSatisfied) anyAlternativeFailed = true;
        }
        return anyAlternativeFailed ? false : null;
    }

    private static bool? SatisfiesSingle(string candidate, string token)
    {
        token = token.Trim();
        if (token.Length == 0) return null;

        foreach (var (prefix, op) in new[] { (">=", 1), ("<=", 2), (">", 3), ("<", 4), ("=", 0) })
        {
            if (!token.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var cmp = CompareVersionStrings(candidate, token[prefix.Length..].Trim());
            if (cmp is null) return null;
            return op switch { 1 => cmp >= 0, 2 => cmp <= 0, 3 => cmp > 0, 4 => cmp < 0, _ => cmp == 0 };
        }

        if (token.StartsWith('^'))
        {
            // caret：>= 基准，且不改变最左非零位以上的部分。预发布版一律按"无法判定"处理。
            var basis = token[1..].Trim();
            var cmp = CompareVersionStrings(candidate, basis);
            if (cmp is null || cmp < 0) return cmp is null ? null : false;
            if (basis.Contains('-')) return null;
            var v = ParseVersion(basis);
            var c = ParseVersion(candidate);
            if (v is null || c is null) return null;
            var majorMatters = v.Major > 0;
            var minorMatters = !majorMatters && v.Minor > 0;
            if (majorMatters) return c.Major == v.Major;
            if (minorMatters) return c.Major == 0 && c.Minor == v.Minor;
            return c.Major == 0 && c.Minor == 0 && c.Build == v.Build;
        }

        // 裸版本号：当作"至少这个版本"，semver 范围里的常见宽松写法
        var bare = CompareVersionStrings(candidate, token);
        return bare is null ? null : bare >= 0;
    }

    /// <summary>
    /// 候选引擎版本 与 需求 是否能比较。
    /// 只有 DSH 自身那一族（"@deepseek-ai/dsh" 与 "@deepseek-ai/dsh-*"）与引擎同版本发布。
    /// 别的一律不是：实测本机引擎里 cordis=4.0.2、schemastery=3.18.2、cosmokit=1.8.3、
    /// node-addon-system=0.1.2，各有自己的版本号。把引擎版本 0.2.0-rc.2 拿去比
    /// "^4.0.1"（cordis）永远不成立——那正是早期版本会误报"4 项不满足"的原因。
    /// </summary>
    private static bool IsDshVersionedPackage(string packageName)
    {
        const string prefix = "@deepseek-ai/dsh";
        if (!packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        // "@deepseek-ai/dsh" 本身，或 "@deepseek-ai/dsh-xxx"
        return packageName.Length == prefix.Length || packageName[prefix.Length] == '-';
    }

    /// <summary>
    /// profile 里某个 @deepseek-ai/* 包实际解析到的版本（含 pnpm 的 .pnpm 存放区）。
    /// 找不到返回 null。
    /// </summary>
    private static string? ResolveInstalledPackageVersion(string profileDir, string packageName)
    {
        try
        {
            var modules = Path.Combine(profileDir, "node_modules");
            // ① 直接可见的位置
            var direct = Path.Combine(modules, packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");
            if (File.Exists(direct))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(direct));
                if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            // ② pnpm 的 .pnpm/<name>@<version>/node_modules/<name>（版本号就在目录名里）
            var pnpmDir = Path.Combine(modules, ".pnpm");
            if (!Directory.Exists(pnpmDir)) return null;
            var leaf = packageName.Split('/').Last();
            foreach (var dir in Directory.GetDirectories(pnpmDir, leaf + "@*"))
            {
                var name = Path.GetFileName(dir);
                var at = name.LastIndexOf('@');
                if (at <= 0 || at == name.Length - 1) continue;
                var version = name[(at + 1)..];
                // 目录名可能是 1.2.3 或 1.2.3_<peer-hash> 形式
                var underscore = version.IndexOf('(');
                if (underscore >= 0) version = version[..underscore];
                if (version.Length > 0) return version;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 返回 (无法判定的条数, 明确不满足的明细)。
    /// dsh 那一族与候选引擎版本比较；其余包与"当前实际装着的版本"比较——
    /// 那些包不随引擎升级而变，所以"现在满足、升级后照样满足；现在不满足也不是升级造成的"。
    /// </summary>
    private static (int Unknown, List<string> Violations) CheckPluginCompatibility(string candidateVersion, string profileDir)
    {
        var violations = new List<string>();
        var unknown = 0;
        foreach (var req in CollectPluginRequirements(profileDir))
        {
            bool? verdict;
            string basis;
            if (IsDshVersionedPackage(req.Package))
            {
                verdict = SatisfiesRange(candidateVersion, req.Range);
                basis = $"引擎 {candidateVersion}";
            }
            else
            {
                var installed = ResolveInstalledPackageVersion(profileDir, req.Package);
                if (installed is null)
                {
                    // 这个包既不由引擎随版本提供、也不在 profile 里，无从判断。
                    unknown++;
                    continue;
                }
                verdict = SatisfiesRange(installed, req.Range);
                basis = $"当前 {installed}";
            }

            if (verdict is null) { unknown++; continue; }
            if (!verdict.Value) violations.Add($"{req.Plugin} 要求 {req.Package} {req.Range}（{basis}）");
        }
        return (unknown, violations);
    }

    private static async Task<string?> GetLatestEngineVersionAsync(string node, CancellationToken ct)
    {
        try
        {
            var npm = ResolveNpmPath(node);
            using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            queryCts.CancelAfter(TimeSpan.FromSeconds(EngineQueryTimeoutSeconds));

            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c \"\"{npm}\" view {EnginePackageName} version --registry {ResolveNpmRegistry(node, ct)}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var nodeDir = Path.GetDirectoryName(node);
            var path = psi.Environment["PATH"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(nodeDir)) psi.Environment["PATH"] = nodeDir + ";" + path;
            ConfigureOptionalProxy(psi);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;
            var outTask = proc.StandardOutput.ReadToEndAsync(queryCts.Token);
            await proc.WaitForExitAsync(queryCts.Token);
            var text = (await outTask).Trim();
            if (text.Length == 0) return null;
            return text.Split('\n').Last().Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// 「升级引擎」：查最新版 → 装到 staging → 成功才替换 → 重启。
    /// 全程不碰 npx，且任何一步失败都不会动当前能跑的引擎。
    /// </summary>
    private async Task RunEngineUpgradeAsync()
    {
        if (busy || closing || IsDisposed) return;

        var cts = new CancellationTokenSource();
        CancelPendingStart();
        startCts = cts;
        EnterBusy(upgradeButton, "升级中");
        try
        {
            var node = await ResolveNodeAsync(cts.Token);
            var current = ReadEngineVersion(engineDir);

            // 版本锁优先于「升级」：钉住的版本可能正是插件唯一验证过的那个，
            // 按钮不该把它顶掉。想升级先清空 engine-version.txt。
            var pinned = ReadPinnedEngineVersion();
            if (pinned is not null)
            {
                SetInfo($"已锁定引擎版本 {pinned}，升级被跳过（清空 engine-version.txt 可解除）");
                if (!closing && !IsDisposed)
                    MessageBox.Show(
                        $"引擎版本已锁定为 {pinned}，不会升级。\n\n" +
                        "这是为了避免新版引擎让你的插件失效。\n" +
                        $"要升级请先删除或清空：\n{engineVersionPinFile}",
                        "引擎已锁定", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetInfo($"正在查询最新版本…（当前 {current ?? "未安装"}）");

            var latest = await GetLatestEngineVersionAsync(node, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (latest is null)
            {
                SetInfo("查不到最新版本（npm 或网络不可用），保持当前引擎");
                return;
            }
            if (current == latest)
            {
                SetInfo($"引擎已是最新（{latest}），无需升级");
                return;
            }

            // 兼容性护栏：升级前先拿新版本对一遍 profile 里插件的 peer 要求。
            // 实测本机 profile 只声明 6 个插件依赖、@deepseek-ai/* 全部由引擎提供，
            // 所以升级不会动插件文件，但插件声明的范围可能不覆盖新引擎——
            // 那种情况下升级会让插件失效，必须先让用户知情再决定。
            var (unknownCount, violations) = CheckPluginCompatibility(latest, webProfileDir);
            if (violations.Count > 0)
            {
                var lines = new StringBuilder();
                lines.AppendLine($"即将把引擎从 {current ?? "未安装"} 升级到 {latest}。");
                lines.AppendLine();
                lines.AppendLine($"⚠ 有 {violations.Count} 项插件要求无法满足：");
                foreach (var v in violations.Take(8)) lines.AppendLine("  · " + v);
                if (violations.Count > 8) lines.AppendLine($"  …另有 {violations.Count - 8} 项");
                lines.AppendLine();
                lines.AppendLine("标「引擎 x.y.z」的项与这次升级有关；标「当前 …」的项是那些包");
                lines.AppendLine("本来就不随引擎版本变动，升级不会改善它们。");
                if (unknownCount > 0)
                    lines.AppendLine($"另有 {unknownCount} 项因版本范围无法解析（多为预发布三选一）未能判定。");
                lines.AppendLine();
                lines.AppendLine("选择「否」保持当前引擎不变（插件的现有状态完全不受影响）。");

                var go = MessageBox.Show(lines.ToString(), "升级可能影响插件",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (go != DialogResult.Yes)
                {
                    SetInfo($"已取消升级，保持引擎 {current ?? "未安装"}（插件优先）");
                    return;
                }
            }

            // 运行中的实例占着引擎文件，先停干净再换目录。
            await StopHarnessProcessesAsync();
            if (cts.IsCancellationRequested) return;
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(10), cts.Token);
            if (cts.IsCancellationRequested) return;

            SetInfo($"正在安装引擎 {latest}（先装临时目录，成功后替换）…");
            // 升级是配置迁移的实际触发点（引擎版本一变，下次启动就可能改写 settings.yaml），
            // 所以这里再拍一份——此时还是"升级前"的配置，是最有价值的还原点。
            ConfigBackup.CreateSnapshot($"升级引擎前（{current ?? "未安装"} → {latest}）");
            await InstallEngineAsync(node, latest, ResolveNpmRegistry(node, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            SetInfo($"引擎已升级到 {ReadEngineVersion(engineDir) ?? latest}，正在重启…");
            await StartHarnessAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!closing && !IsDisposed)
                ShowError("引擎升级失败", ex.Message + "\n\n当前引擎未被改动，仍可正常使用。");
        }
        finally
        {
            if (ReferenceEquals(startCts, cts)) EndBusy();
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    /// <summary>
    /// 删除目录：node_modules 里常有只读文件，先清属性再删；失败给出可操作的提示
    /// （最常见原因是有 DSH 进程还占着引擎目录）。
    /// 用 \\?\ 扩展前缀绕过 260 字符上限——Windows 的 LongPathsEnabled 默认是关的，
    /// 而同学的用户名一变长（如 C:\Users\ZhangSanxxxx\AppData\...），
    /// 引擎 node_modules 的深路径就会超过 MAX_PATH，不加前缀时删除必定失败。
    /// </summary>
    private static void ForceDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var extended = ToExtendedPath(dir);
        try { ClearReadOnlyAttributes(extended); } catch { }
        try { Directory.Delete(extended, recursive: true); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法删除 {dir}：{ex.Message}\n（若为引擎目录被占用，请先点「停止」再重试）", ex);
        }
    }

    /// <summary>给绝对路径加 \\?\ 前缀，绕过 MAX_PATH（UNC 路径用 \\?\UNC\）。</summary>
    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full.Substring(2)
            : @"\\?\" + full;
    }

    private static void ClearReadOnlyAttributes(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attr = File.GetAttributes(file);
                if (attr.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
            catch { }
        }
    }

    // ---- 自动更新 -----------------------------------------------------------

    /// <summary>
    /// 构建 pnpm update 的启动参数。优先真实 pnpm；找不到时回退 corepack，
    /// 此时参数必须带 "pnpm" 前缀（corepack 是 shim，不直接接受 update 子命令）。
    /// 不带 --latest：git 依赖本就拉默认分支最新 commit，
    /// 带 semver 范围的注册表依赖则应留在声明的范围内。
    /// 注意：不要加 --no-frozen-lockfile —— pnpm 11 的 update 子命令不认这个 flag
    /// （`Unknown option: 'frozen-lockfile'`），会让更新每次都直接失败；
    /// update 本来就会改写 lockfile，无需该参数。
    /// </summary>
    private static ProcessStartInfo? NewPnpmUpdateStartInfo(string profile)
    {
        string fileName;
        string arguments;
        var pnpm = ResolvePnpmPath();
        if (pnpm is not null)
        {
            fileName = pnpm;
            arguments = "update --reporter=append-only";
        }
        else
        {
            var corepack = ResolveCorepackPath();
            if (corepack is null) return null;
            fileName = corepack;
            arguments = "pnpm update --reporter=append-only";
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
        var profile = webProfileDir;
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
            // 记下"跑过了"，否则每次点「开始」都要付一次 pnpm 的网络往返。
            // 想每次都跑：删掉 %LOCALAPPDATA%\DeepSeekHarness\lastPluginUpdate.txt
            StampPluginUpdate();
        }
    }

    /// <summary>
    /// 同一自然日只自动更新一次插件。pnpm update 即便命中热 store 也要 3 秒起，
    /// 遇上 GitHub 依赖超时实测 24 秒、首次拉依赖 3 分 37 秒；而插件一天更新一次
    /// 和每次启动都更新，实际没有区别。手动点「开始」也受这个节流约束，
    /// 需要强制刷新时删掉戳记文件即可。
    /// </summary>
    private int PluginUpdateCooldownHours => 20;

    private string PluginUpdateStampFile => Path.Combine(LocalAppDir, "lastPluginUpdate.txt");

    private void StampPluginUpdate()
    {
        try
        {
            Directory.CreateDirectory(LocalAppDir);
            File.WriteAllText(PluginUpdateStampFile, DateTime.Now.ToString("o"), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>null = 从未更新过（应当更新）。</summary>
    private DateTime? ReadPluginUpdateStamp()
    {
        try
        {
            if (!File.Exists(PluginUpdateStampFile)) return null;
            return DateTime.TryParse(File.ReadAllText(PluginUpdateStampFile).Trim(), out var at) ? at : null;
        }
        catch { return null; }
    }

    private static void AppendStartupLog(string text)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "startup-log.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void AppendUpdateLog(string content)
    {
        try
        {
            Directory.CreateDirectory(LocalAppDir);
            var logFile = Path.Combine(LocalAppDir, "update-log.txt");

            // 先把"表头 + 内容"拼成一条完整记录再落盘。此前是分两次写
            // （一次写内容、一次补表头），文件长度刚好卡在阈值上时，
            // 截断会把同一条记录劈成两半。
            var record = new StringBuilder();
            record.Append("=== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" ===\n");
            record.Append(content.Replace("\r\n", "\n").TrimEnd('\n')).Append('\n');

            var existing = File.Exists(logFile) ? File.ReadAllText(logFile, Encoding.UTF8) : string.Empty;

            // 每次更新都会追加整段 pnpm 输出，放任下去会无限增长。
            // 必须按"整条记录"为单位从头部滚动，不能按字符截断：
            // ① 字符截断会把多字节汉字劈成半个，写出乱码；
            // ② 截断点落在记录中间时，表头被切掉，剩下的日志无法判断是哪次跑的。
            File.WriteAllText(logFile, TrimUpdateLog(existing + record.ToString()), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// 超过 256 KB 时只保留末尾约 128 KB，且必须从一条记录的开头切起。
    /// 单条记录本身就超过 128 KB 时，保留该记录的最后 128 KB——
    /// 此时确实无法保证记录完整，但总比留下一个无头片段更有用。
    /// </summary>
    private static string TrimUpdateLog(string text)
    {
        const int MaxBytes = 256 * 1024;
        const int KeepBytes = 128 * 1024;
        if (Encoding.UTF8.GetByteCount(text) <= MaxBytes) return text;

        var lines = text.Split('\n');
        var kept = new List<string>();
        var bytes = 0;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + 1;
            if (bytes + lineBytes > KeepBytes) break;
            kept.Add(lines[i]);
            bytes += lineBytes;
        }
        kept.Reverse();
        var result = string.Join("\n", kept);
        if (result.Length > 0) return result;

        var tail = text[^Math.Min(text.Length, KeepBytes / 3)..];
        return tail[(tail.IndexOf('\n') + 1)..];
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

            string? nodeExe = null;
            try { nodeExe = await ResolveNodeAsync(CancellationToken.None); }
            catch (Exception ex) { sb.AppendLine("✗ Node.js：" + ex.Message.Split('\n')[0]); }
            if (nodeExe is not null)
            {
                var v = await GetToolVersionAsync(nodeExe, "--version");
                sb.AppendLine($"✓ Node.js：{v?.Trim() ?? "未知"}（{Path.GetDirectoryName(nodeExe)}）");
                var npm = FindToolBeside(nodeExe, "npm.cmd") ?? FindTool("npm.cmd");
                sb.AppendLine(npm is null ? "✗ npm：未找到（npm 随 Node 安装）" : $"✓ npm：{npm}");
            }

            // 引擎现在是固定目录安装，不再是 npx 哈希缓存。
            var engine = ReadEngineVersion(engineDir);
            if (engine is null)
            {
                sb.AppendLine("✗ DSH 引擎：未安装");
                sb.AppendLine("    解决：点击「开始」会自动装到固定目录（只此一次，约 1–2 分钟）");
            }
            else
            {
                sb.AppendLine($"✓ DSH 引擎：{engine}");
                sb.AppendLine($"    位置：{engineDir}");
                sb.AppendLine("    升级：面板里的「升级引擎」按钮（不会自动跟随新版）");
            }

            // 版本锁与回滚：这两项直接对应"以插件为主"的取舍，必须能一眼看到。
            var pinnedNow = ReadPinnedEngineVersion();
            sb.AppendLine(pinnedNow is not null
                ? $"🔒 引擎版本已锁定：{pinnedNow}（升级被禁用；清空 engine-version.txt 解除）"
                : $"○ 引擎未锁定（升级时跟随最新版）。想钉住版本就把版本号写进：{engineVersionPinFile}");

            var oldEngine = ReadEngineVersion(engineOldDir);
            if (oldEngine is not null)
                sb.AppendLine($"↩ 可回退的上一个引擎：{oldEngine}（在 engine.old；插件不兼容时可换回）");

            // 插件对引擎版本的要求，与实际引擎对不对得上——这正是"升级后插件还能不能用"的判据。
            // 注意只有 dsh 那一族随引擎版本走，cordis/schemastery 等有自己的版本号，
            // 那些项是按"当前已装版本"判定的，与升级无关。
            var reqs = CollectPluginRequirements(webProfileDir);
            if (reqs.Count > 0 && engine is not null)
            {
                var (unknown, bad) = CheckPluginCompatibility(engine, webProfileDir);
                if (bad.Count == 0)
                    sb.AppendLine($"✓ 插件兼容性：{reqs.Count} 项要求均满足" +
                                  (unknown > 0 ? $"（{unknown} 项无法判定）" : string.Empty));
                else
                    sb.AppendLine($"⚠ 插件兼容性：{bad.Count} 项不满足（共 {reqs.Count} 项要求）");
                foreach (var b in bad.Take(8)) sb.AppendLine("    · " + b);
                if (bad.Count > 8) sb.AppendLine($"    …另有 {bad.Count - 8} 项");
            }
            if (Directory.Exists(engineStageDir))
                sb.AppendLine("⚠ 存在未完成的安装残留：engine.tmp（下次安装会自动清掉）");

            var pnpm = ResolvePnpmPath(nodeExe);
            if (pnpm is null)
            {
                var corepack = ResolveCorepackPath(nodeExe);
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

            var profile = webProfileDir;
            sb.AppendLine(File.Exists(Path.Combine(profile, "package.json"))
                ? "✓ profile：已初始化"
                : "✗ profile：未初始化（首次点击「开始」会自动初始化）");

            if (IsPortListening(DefaultPort))
            {
                sb.AppendLine($"⚠ 端口 {DefaultPort} 已被占用，可能已有 DSH 在运行");
                var hint = DescribeDynamicPortRange(DefaultPort);
                if (hint is not null) sb.AppendLine("    " + hint.Replace("\n", "\n    "));
            }
            else
            {
                sb.AppendLine($"✓ 端口 {DefaultPort} 空闲");
            }

            // 状态栏只显示一个端口，说不清"到底谁在占用"；这里把本机监听端口列出来，
            // 排查"端口被占"时不用再去开命令行。
            var listening = ListeningPorts().OrderBy(x => x).ToArray();
            sb.AppendLine();
            sb.AppendLine($"本机 TCP 监听端口（{listening.Length} 个）：{string.Join(", ", listening)}");

            // 配置备份状态 + 一键恢复。
            // 实测 DSH 的 settings 迁移会丢掉不匹配 profile 条目 id 的配置段
            // （jet-hub 账号列表、llm-pi-ai 的自定义供应商都中过招），
            // 所以这里既报状态，也给一条不用手动翻目录的恢复路径。
            sb.AppendLine();
            var latestBackup = ConfigBackup.LatestSnapshot();
            if (latestBackup is null)
            {
                sb.AppendLine($"○ 配置备份：还没有快照（点「开始」或「重启」会自动生成，存于 {ConfigBackup.BackupRoot}）");
            }
            else
            {
                var info = Path.Combine(latestBackup, "backup-info.txt");
                var when = Directory.GetCreationTime(latestBackup);
                sb.AppendLine($"✓ 配置备份：最新一份 {when:yyyy-MM-dd HH:mm}（{Path.GetFileName(latestBackup)}）");
                if (File.Exists(info))
                {
                    foreach (var line in File.ReadAllLines(info).Where(l => l.StartsWith("原因")))
                        sb.AppendLine("    " + line.Trim());
                }
                sb.AppendLine($"    位置：{ConfigBackup.BackupRoot}");
            }

            var restore = MessageBox.Show(sb.ToString(), "环境检测", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (restore == DialogResult.Yes)
            {
                if (latestBackup is null)
                {
                    MessageBox.Show("还没有可恢复的备份快照。", "恢复配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    RestoreLatestBackup(latestBackup);
                }
            }
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

    /// <summary>
    /// 用最新快照覆盖回 $DSH_HOME。刻意只覆盖快照里有的文件、不动其他任何东西，
    /// 并在覆盖前把"当前状态"再存一份——万一恢复错了还能退回来。
    /// </summary>
    private void RestoreLatestBackup(string snapshotDir)
    {
        var confirm = MessageBox.Show(
            $"用这份快照覆盖当前配置？\n\n快照：{Path.GetFileName(snapshotDir)}\n" +
            $"目标：{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\.dsh\\\n\n" +
            "只覆盖快照里存在的文件（.credentials.yaml、settings.yaml、各 profile 的 cordis*.yml 等），\n" +
            "不碰 node_modules 与引擎。覆盖前我会先把当前状态另存一份。\n\n" +
            "恢复后需要重启引擎（点「停止」再点「开始」）才会生效。",
            "恢复配置备份", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        // 先给"恢复前"的状态留一份，保证这一步本身也可逆。
        ConfigBackup.CreateSnapshot("恢复配置前（当前状态）");
        var count = ConfigBackup.Restore(snapshotDir);
        if (count < 0)
        {
            MessageBox.Show("恢复失败，可能原因：引擎正在运行占用了配置文件。\n请先点「停止」再试。",
                "恢复配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageBox.Show(
            $"已从快照恢复 {count} 个配置文件。\n\n请点「停止」再点「开始」重启引擎使其生效。",
            "恢复配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static async Task<string?> GetToolVersionAsync(string exe, string args)
    {
        Process? proc = null;
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
            proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;
            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var text = (await outTask) + (await errTask);
            var first = text.Split('\n').FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            // 超时/取消时进程可能还活着（比如 pnpm 经由 corepack shim 卡住）。
            // 此前只 Dispose 不 Kill，会留下背地里跑着的孤儿进程。
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 可选代理。注意 7897 这个探测：本机没有 HTTP(S)_PROXY 变量时，此前每次启动
    /// 都会去连一次 127.0.0.1:7897 并等满超时（实测 150–206 ms）；而绝大多数机器
    /// 上那个端口并不存在，等于每次启动白付一笔。现在只有用户明确设过代理变量
    /// （说明他确实在用本地代理）才做这次探测。
    /// </summary>
    private static void ConfigureOptionalProxy(ProcessStartInfo psi)
    {
        var http = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var https = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var hasProxyVars = !string.IsNullOrWhiteSpace(http) || !string.IsNullOrWhiteSpace(https);
        if (hasProxyVars)
        {
            // 用户自己配了系统代理：npm 会读 HTTP(S)_PROXY，但 Node 24+ 还需要这个开关
            // 才会让 node/npx 也走代理（旧版 Node 忽略该变量，设了无害）。
            psi.Environment["NODE_USE_ENV_PROXY"] = "1";
            return;
        }

        if (!ShouldProbeLocalProxy()) return;
        if (!IsTcpOpen("127.0.0.1", ProxyPort)) return;
        psi.Environment["NODE_USE_ENV_PROXY"] = "1";
        psi.Environment["HTTP_PROXY"] = $"http://127.0.0.1:{ProxyPort}";
        psi.Environment["HTTPS_PROXY"] = $"http://127.0.0.1:{ProxyPort}";
    }

    /// <summary>
    /// 是否值得去探一次本机代理端口（结果缓存到进程结束）。
    /// 只在系统里存在任何代理线索时才探，避免无代理机器每次启动白等一个超时。
    /// DSH_FORCE_LOCAL_PROXY=1 可强制探测（给"代理变量没设但确实在跑 7897"的用户留出口）。
    /// </summary>
    private static bool? localProxyProbeWanted;

    private static bool ShouldProbeLocalProxy()
    {
        if (localProxyProbeWanted.HasValue) return localProxyProbeWanted.Value;
        var probe = string.Equals(
            Environment.GetEnvironmentVariable("DSH_FORCE_LOCAL_PROXY"), "1", StringComparison.Ordinal);
        if (!probe)
        {
            foreach (var name in new[] { "ALL_PROXY", "all_proxy", "NO_PROXY", "npm_config_proxy", "npm_config_https_proxy" })
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) continue;
                probe = true;
                break;
            }
        }
        localProxyProbeWanted = probe;
        return probe;
    }

    private static bool IsTcpOpen(string host, int port, int timeoutMs = 150)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            // 用 WaitAny 而不是 task.Wait(timeout)：超时后能被取消，也不吞掉 AggregateException。
            if (Task.WaitAny(new Task[] { connect }, timeoutMs) != 0) return false;
            return client.Connected;
        }
        catch { return false; }
        finally { try { client?.Dispose(); } catch { } }
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

    private Button NewButton(string text, string accessibleName, int x, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, 168),
            Size = new Size(94, 46),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = ButtonFont,
            Cursor = Cursors.Hand,
            AccessibleName = accessibleName
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    /// <summary>
    /// 无障碍与键盘可达性。屏幕阅读器读不出"开始/重启/停止"这种两字按钮是干什么的，
    /// 而状态与提示文字的变化（"正在启动…""启动失败"）此前也不会被主动播报。
    /// </summary>
    private void ApplyAccessibility()
    {
        AccessibleName = "DeepSeek Harness 控制台";
        AccessibleDescription = "启动与管理本机的 DeepSeek Harness Web 服务";

        // 状态与提示是纯展示控件，设置为礼貌播报区域，变化时会被读屏主动念出来。
        status.AccessibleName = "运行状态";
        status.LiveSetting = AutomationLiveSetting.Polite;
        info.AccessibleName = "状态详情";
        info.LiveSetting = AutomationLiveSetting.Polite;

        upgradeButton.AccessibleName = "升级 DSH 引擎到 npm 上的最新版本";
        upgradeButton.TabIndex = 2;
        autoUpdateCheckbox.AccessibleName = "启动后在后台更新 DSH 插件，改动下次启动生效";

        // 主按钮的 Tab 顺序按视觉从左到右。
        startButton.TabIndex = 0;
        restartButton.TabIndex = 1;
        stopButton.TabIndex = 3;
        refreshButton.TabIndex = 4;
        envButton.TabIndex = 5;
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
        upgradeButton.Enabled = false;
        active.Text = text;
        // 忙碌时按钮文字变成"启动中/停止中"，读屏就再也读不出它是哪个按钮了，
        // 所以把原语义留在 AccessibleName 里。
        if (ReferenceEquals(active, startButton)) active.AccessibleName = "开始（" + text + "）";
        else if (ReferenceEquals(active, restartButton)) active.AccessibleName = "重启（" + text + "）";
        else if (ReferenceEquals(active, stopButton)) active.AccessibleName = "停止（" + text + "）";
        else if (ReferenceEquals(active, upgradeButton)) active.AccessibleName = "升级引擎（" + text + "）";
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
        upgradeButton.Text = "升级引擎";
        startButton.AccessibleName = "启动 Harness 引擎并打开控制台。回车键等效";
        restartButton.AccessibleName = "结束当前引擎并重新启动";
        stopButton.AccessibleName = "停止引擎并释放端口";
        upgradeButton.AccessibleName = "升级 DSH 引擎到 npm 上的最新版本";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (busy) return;
        startButton.Enabled = true;
        restartButton.Enabled = true;
        stopButton.Enabled = isOn;
        upgradeButton.Enabled = true;
    }

    private void DrawLamp(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(isOn ? Color.FromArgb(34, 170, 85) : Color.FromArgb(178, 182, 190));
        e.Graphics.FillEllipse(brush, 4, 4, 28, 28);
    }

    private void ShowError(string title, string message) => MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>
    /// 进程级资源的收尾。LocalHttp / 字体 / ToolTip 都是长时间存活的对象，
    /// 此前没有任何一处释放它们。这里先置 closing，避免刷新逻辑再去用已释放的 HttpClient。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            try { refreshTimer.Stop(); } catch { }
            try { refreshTimer.Dispose(); } catch { }
            try { tooltip.Dispose(); } catch { }
            // 静态字体属于进程生命周期，不在这里释放：WinForms 控件仍可能在
            // 后续的销毁流程里引用它们，提前释放会抛 ObjectDisposedException。
        }
        base.Dispose(disposing);
    }

    private static int? ExtractPort(string url)
    {
        try { return new Uri(url).Port; } catch { return null; }
    }

    private sealed record ProcessRecord(int Id, int ParentId, string Name, string CommandLine);
}
