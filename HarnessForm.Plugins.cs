// ── HarnessForm 的「插件自动更新（pnpm / corepack）」部分 ──────────────────────────────────────────
// 由 HarnessForm.cs 按本文件原有的「// ---- 分段 ----」分隔线机械切分而来。
// 切分只搬位置、不改任何一行成员代码；partial class 之间共享全部字段与成员。
// 各段清单见 README「文件说明」。改动请落在语义所属的那一段里。

using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing.Drawing2D;
using System.Windows.Forms.Automation;

namespace DeepSeekHarness;

internal sealed partial class HarnessForm : Form
{

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
    private static ProcessStartInfo? NewPnpmUpdateStartInfo(string profile, string? nodePath = null)
    {
        string fileName;
        string arguments;
        // 优先用与被选中 node.exe 同目录的那套 pnpm/corepack，保证 node/npm/pnpm 是同一次安装。
        var pnpm = ResolvePnpmPath(nodePath);
        if (pnpm is not null)
        {
            fileName = pnpm;
            arguments = "update --reporter=append-only";
        }
        else
        {
            var corepack = ResolveCorepackPath(nodePath);
            if (corepack is null) return null;
            fileName = corepack;
            arguments = "pnpm update --reporter=append-only";
        }
        var psi = new ProcessStartInfo
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
        // PATH 与代理此前只有 npm 那几条路径配了，pnpm 这条独缺——于是"引擎装得上、
        // 插件却更新不了"（换机器最容易复现：node 装在非标准目录，PATH 里没有它）。
        // 与 StartHarnessAsync / InstallEngineAsync 保持同一套做法。
        var nodeDir = nodePath is null ? null : Path.GetDirectoryName(nodePath);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(nodeDir) &&
            !path.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(nodeDir, StringComparer.OrdinalIgnoreCase))
            psi.Environment["PATH"] = nodeDir + ";" + path;
        ConfigureOptionalProxy(psi);
        return psi;
    }

    /// <summary>
    /// 把 package.json 里 link: 依赖的目标解析成待检查的路径。相对路径以
    /// <paramref name="profileDir"/>（包所在目录）为基准——这是 pnpm 的语义；
    /// 此前原样塞给 Directory.Exists，相对路径按**启动器的当前工作目录**解析，
    /// 有效的 <c>link:../plugin</c> 会被判成断链、整轮插件更新被跳过——
    /// 检测自己诱发了它要防的失败。绝对路径原样。internal（而非 private）：
    /// "相对基准是 profile 不是 CWD"这条口径要被单测钉住。
    /// </summary>
    internal static string ResolveLinkTarget(string profileDir, string linkSpec) =>
        Path.IsPathRooted(linkSpec) ? linkSpec : Path.Combine(profileDir, linkSpec);

    /// <summary>
    /// 找出 profile 里路径已失效的 link: 依赖（pnpm 遇到断链会整体失败，
    /// 提前检测并给出具体路径，比笼统的"更新失败"更可定位）。
    ///
    /// **三段都要扫**：dependencies / devDependencies / optionalDependencies。
    /// 此前只扫第一段，于是 profile 里一处 devDependency 的断链照样让
    /// `pnpm update` 整体失败，而预检说"没发现问题"——正是它本该拦住的那种失败。
    /// internal（而非 private）：纯函数式的"取哪些段"可单测。
    /// </summary>
    internal static readonly string[] DependencySections =
        { "dependencies", "devDependencies", "optionalDependencies" };

    private static List<string> FindBrokenLinkDeps(string profile)
    {
        var broken = new List<string>();
        try
        {
            var json = File.ReadAllText(Path.Combine(profile, "package.json"));
            using var doc = JsonDocument.Parse(json);
            foreach (var section in DependencySections)
            {
                if (!doc.RootElement.TryGetProperty(section, out var deps)) continue;
                if (deps.ValueKind != JsonValueKind.Object) continue;
                foreach (var dep in deps.EnumerateObject())
                {
                    var value = dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString() : null;
                    if (value is null || !value.StartsWith("link:", StringComparison.OrdinalIgnoreCase)) continue;
                    var linkPath = value.Substring(5);
                    if (!Directory.Exists(ResolveLinkTarget(profile, linkPath)))
                        broken.Add($"{section}: {dep.Name} -> {linkPath}");
                }
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

        // 挑一套与已装 Node 一致的 pnpm/corepack：解析不出 Node 不阻断插件更新
        //（这是后台的"锦上添花"功能，不该因为 node 探测失败就整个放弃）。
        string? nodePath = null;
        try { nodePath = await ResolveNodeAsync(ct); }
        catch { }

        var psi = NewPnpmUpdateStartInfo(profile, nodePath);
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
        // stdout 与 stderr 的回调分属两个独立的线程池线程，StringBuilder 不是线程安全的。
        // 之前直接 AppendLine，日志会错乱甚至抛异常——而这个异常发生在管道读取线程上，
        // 无人接管会直接终止进程。
        var outputGate = new object();
        Process? proc = null;
        var succeeded = false;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) lock (outputGate) output.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) lock (outputGate) output.AppendLine(e.Data);
            };

            // 取消检查点**紧贴 process.Start()**。解析 Node 的 OCE 被上面那个 catch { } 吞掉了
            //（那是有意的：探测失败不该阻断插件更新），于是从"用户已取消"走到这里时
            // ct 已是取消态。引擎那条路径早就要求检查点必须贴着 Start，插件这条此前漏了。
            // 后果轻微（updateCts 的 kill-on-cancel 回调会补刀、pnpm 起跑后立刻被杀），
            // 但"这次没出事"不等于纪律满足。
            updateCts.Token.ThrowIfCancellationRequested();
            if (!proc.Start())
            {
                SetInfo("更新失败：无法启动 pnpm");
                return;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 取消击杀挂同步回调（理由见 RunCmdAsync 同款注释）：关窗丢掉 OCE 续延时，
            // 孤儿 pnpm 会继续写 profiles\node_modules，与下一次引擎启动的模块链接
            // 重建踩踏同一棵树——正是 RunStartAsync 注释里要防的并发场景。
            using var killOnCancel = updateCts.Token.Register(() =>
            {
                try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            try { await proc.WaitForExitAsync(updateCts.Token); }
            catch (OperationCanceledException)
            {
                // WaitForExitAsync 在超时时是**抛异常**而不是正常返回。
                // 之前把"更新超时"写成 await 之后判断 updateCts.Token.IsCancellationRequested，
                // 那一支永远走不到——pnpm 跑满 10 分钟被取消时用户什么反馈都收不到。
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                // ct 也被取消 = 用户主动取消（关窗/又点了一次启动），不必打扰。
                if (!ct.IsCancellationRequested) SetInfo("更新超时，已跳过");
                return;
            }

            // ⚠ 同 InstallEngineAsync：OutputDataReceived 是**异步**投递的，
            // 进程一退出不等于管道里剩下的行已经派发完。少这一次"无超时再等一次"，
            // 失败时展示给用户的输出摘要恰好会缺掉最关键的那几行 npm error，
            // 而用户拿到的正是一段不完整的报错上下文。
            await proc.WaitForExitAsync(CancellationToken.None);

            if (proc.ExitCode == 0)
            {
                succeeded = true;
                SetInfo("插件已更新到最新");
            }
            else
            {
                SetInfo($"更新部分失败（{proc.ExitCode}），继续启动");
            }
        }
        catch (Exception ex)
        {
            // proc.Start() 抛的是 Win32Exception（文件没了/被占用）而不是返回 false，
            // 它会穿过这个 try 飞到调用方去。而本任务在取消路径上**不被 await**
            // （RunStartAsync 的 ContinueWith 只 Dispose cts、不观察异常），
            // 那条路上没人接——正是本方法上方注释里"永不带未观察异常退场"那条不变量
            // 要防的形状。插件更新是锦上添花，失败只留一条信息。
            SetInfo("插件更新失败：" + ex.Message);
            try { AppendUpdateLog($"插件更新异常：{ex.GetType().Name}: {ex.Message}"); } catch { }
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
            lock (outputGate) AppendUpdateLog(output.ToString());
            // 只在真的更新成功时才盖戳记。启动失败、超时、用户取消都不该算"跑过了"：
            // 否则一次网络故障会让 20 小时内都不再重试，只能等用户手动删这个文件。
            if (succeeded) StampPluginUpdate();
        }
    }

    /// <summary>
    /// 距上次**成功**更新满 20 小时才自动更新一次。pnpm update 即便命中热 store 也要 3 秒起，
    /// 遇上 GitHub 依赖超时实测 24 秒、首次拉依赖 3 分 37 秒；一天更新一次和每次启动
    /// 都更新，实际没有区别。手动点「启动」也受这个节流约束，需要强制刷新时删掉
    /// 戳记文件即可。
    ///
    /// 刻意是 20 小时而不是"同一自然日"：后者在边界上是反的——23:00 更新过一次，
    /// 次日 02:00 点启动时"新的一天"到了却只剩 3 小时，按自然日会放行、按冷却会跳过，
    /// 用户看到的提示（"N 小时前已更新过，跳过"）与承诺对不上。20 小时的冷却窗口
    /// 至少保证"上一句提示"永远成立。
    /// </summary>
    // 与"上次成功更新"比较的冷却时长。不依赖任何实例状态，用 const 而不是实例属性。
    private const int PluginUpdateCooldownHours = 20;

    private static string PluginUpdateStampFile => Path.Combine(LocalAppDir, "lastPluginUpdate.txt");

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

    // AppendStartupLog / AppendUpdateLog 的读-改-写互斥。两边调用点分布在 UI 线程与
    // 多个后台线程（tail 循环、Task.Run 的归档/清扫、Swallow.Quiet），File.ReadAllText
    // （FileShare.Read）与并发的 WriteAllText 撞上时 IOException 被各自的方法兜掉——
    // 整条日志无声丢失；两个执行者都读完再写还会互相覆盖（丢更新）。
    // 进程内 lock 消掉最常见的丢条目形态；跨登录会话（Local\ 互斥体按会话隔离）
    // 仍存在跨进程窗口，那属于可接受的日志瑕疵，不值得为它改追加式轮转。
    private static readonly object logGate = new();

    internal static void AppendStartupLog(string text)
    {
        try
        {
            lock (logGate)
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(logDir);
                var path = Path.Combine(logDir, "startup-log.txt");
                // 读-改-写而不是 AppendAllText：与 update-log 同一条截断策略，
                // 超过上限按整条记录从头部滚动（此前它无限增长，刷新反复失败时会一直胖下去）。
                // 调用点都是低频事件（失败/切换/归档），整读整写不构成负担。
                var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
                var combined = existing + $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n";
                File.WriteAllText(path, TrimLogTail(combined), new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private void AppendUpdateLog(string content)
    {
        try
        {
            lock (logGate)
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
                File.WriteAllText(logFile, TrimLogTail(existing + record.ToString()), new UTF8Encoding(false));
            }
        }
        catch { }
    }

    /// <summary>
    /// 超过 256 KB 时只保留末尾约 128 KB，且必须从一条记录的开头切起。
    /// 单条记录本身就超过 128 KB 时，保留该记录的最后 128 KB——
    /// 此时确实无法保证记录完整，但总比留下一个无头片段更有用。
    /// update-log 与 startup-log 共用（截断策略必须一致，别只改一处）。
    /// </summary>
    internal static string TrimLogTail(string text)
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

        // 到这里说明 kept 里只有空行（原文总以 '\n' 收尾，于是"最后那条超长记录"
        // 之后必然还挂着一个尾随空行，它会滚进来、拼起来仍是空串）。
        // 兜底：最后一条记录本身就是超过保留预算的单行（pnpm 的裸 \r 进度串合成一行后
        // 很常见），按整条记录滚动一行都放不下——保留它的尾部。
        //
        // 原写法是 tail[(tail.IndexOf('\n') + 1)..]，无判空也无边界检查：尾段里唯一的换行
        // 正好落在最后一个字符上（也就是上面那种以 '\n' 收尾的**常规**形态）时，
        // 切点等于尾段末尾 → 返回空串 → 整份日志被清成 0 字节。实测 300 KB 输入归零，
        // 恰好丢掉最需要排查的那条超长记录与全部历史，而且零报错、无任何痕迹。
        // 因此切点必须落在"换行之后还剩内容"的位置上；尾段内没有换行（-1）时原样保留。
        var tail = text[^Math.Min(text.Length, KeepBytes / 3)..];
        var nl = tail.IndexOf('\n');
        return nl >= 0 && nl + 1 < tail.Length ? tail[(nl + 1)..] : tail;
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
}
