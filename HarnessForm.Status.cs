// ── HarnessForm 的「状态刷新与端口/身份探针」部分 ──────────────────────────────────────────
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

    // ---- 状态刷新 -----------------------------------------------------------

    private async Task<string?> ResolveUsableUrlAsync(CancellationToken ct)
    {
        // 并发探测：两个候选最多 4 秒一起等（LocalHttp.Timeout 2 秒拿到响应头，
        // ProbeBodyTimeout 预算罩住 body；原来串行，最坏 4 秒界面假死）。
        // 候选顺序即**优先级**：内存里的 authenticatedUrl 在前，文件里的在后。
        // ⚠ 这里曾写着"先到先得"，那是串行时代的说法，现在不成立——
        // 实际是 Task.WhenAll 之后按**候选数组顺序**取第一个探测成功的。
        // 行为本身合理（WhenAll 保证不再多等），而按优先级取更稳：
        // 两者同时可用时，内存里那个多半对应本会话，文件里那个可能是上一个会话的。
        var candidates = new[] { authenticatedUrl, TryReadUrlFile() }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return null;

        var probeTasks = candidates.Select(async c => (url: c, ok: await ProbeUrlAsync(c!, ct))).ToArray();
        var results = await Task.WhenAll(probeTasks);
        var winner = results.FirstOrDefault(r => r.ok).url;
        if (winner is null) return null;
        // 认证链接的第三个写入方（另两个：tail 的 HandleProcessLine、停止路径的清理）
        // 也必须持 authUrlGate：探测成功与赋值之间隔着 await，用户可能恰在此刻点了
        // 「停止」（清理在锁内清空字段）——裸赋值会把刚清掉的旧值写回。与写入方
        // 同锁之后，要么赋值先完成（停止随后覆盖，终态正确），要么清理先发生。
        // 这里没有停止路径那种"清完即终"的强约束（引擎刚被杀时探针早已失败），
        // 但同一把锁不该有两个绕开它的写入方——锁保护的是不变量，不是概率。
        lock (authUrlGate)
        {
            authenticatedUrl = winner;
            lastPort = ExtractPort(winner) ?? DefaultPort;
        }
        return winner;
    }

    /// <summary>
    /// 「这个 URL 上是不是 DSH Web」的身份标记。两个，按响应形态分用：
    /// 未认证时引擎回 401 + <see cref="AuthRequiredMarker"/>（纯文本，几十字节）；
    /// 带对 token 时回 200 + SPA 首页，其 &lt;title&gt; 恒为
    /// <see cref="SpaTitleMarker"/>（dsh-web-frontend/dist/index.html 的第 9 行，
    /// 落在第一个 4 KB 缓冲内，BodyContainsAsync 一轮就命中）。
    ///
    /// 两个标记都取自引擎自身产物，**不是**本启动器的约定——所以它们只用来排除
    /// 「3080 上坐着别人」，不参与任何安全判断。
    /// </summary>
    private const string AuthRequiredMarker = "dsh web authentication required";
    private const string SpaTitleMarker = "<title>DeepSeek Harness</title>";

    /// <summary>
    /// 「这个响应是不是 DSH Web」的唯一判定。**纯函数、可单测**。
    ///
    /// 关键在于 <b>200 也必须有 body 标记</b>：只看状态码的话，本机上任何一个
    /// dev server（本项目自己的 web profile 就跑在 Vite 上）对 <c>/</c> 回 200
    /// 都会被当成 DSH——状态栏误报"运行中"、复用路径把浏览器和 token 一起送过去、
    /// 启动前的端口预检被一并绕过。失手方向必须是"宁可说不是 DSH"。
    /// </summary>
    internal static bool IsDshHandshake(HttpStatusCode status, bool authMarkerSeen, bool spaMarkerSeen) =>
        status switch
        {
            HttpStatusCode.OK => spaMarkerSeen,          // 带对 token：200 + SPA 首页
            HttpStatusCode.Unauthorized => authMarkerSeen, // 没带 token：401 + 那句提示
            _ => false
        };

    /// <summary>
    /// 探针的**总预算**（连接 + 响应头 + body 一并计）。<c>HttpClient.Timeout</c>
    /// **不覆盖** ResponseHeadersRead 模式下 body 的读取——这是 .NET 的既定行为：
    /// 超时 CTS 在 SendAsync 返回响应头后即失效，此后的 ReadAsync 完全不受约束。
    /// 3080 上坐着一个"秒回头、慢慢滴漏 body"的不相干服务时（端口预检注释里的
    /// 威胁模型），没有这个预算的探针会无限期挂着：状态轮询的 refreshing 恒为
    /// true（状态灯与文案永久冻结），启动路径的探针又未必接收 startCts，
    /// busy 解除不了、Esc 失效。响应头最多吃掉 LocalHttp.Timeout 的 2 秒，
    /// body 至少还剩 2 秒；正常 DSH 的标记都在第一个 4 KB 缓冲里，远用不到。
    /// </summary>
    private static readonly TimeSpan ProbeBodyTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 探针统一入口的预算封装：链入调用方的取消令牌（Esc/关窗能立即打断），
    /// 再叠加 <see cref="ProbeBodyTimeout"/>。两个探针都必须走这里，不能各写各的。
    /// </summary>
    private static CancellationTokenSource ProbeBudget(CancellationToken ct)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProbeBodyTimeout);
        return budget;
    }

    /// <summary>
    /// 复用路径的探针。<b>必须验身份，不能只看 200</b>：这是唯一会把 token 作为
    /// query 发出去的调用点，而 3080 是本机端口——开发服务器（本项目自己的 web
    /// profile 就跑在 Vite 上）对 <c>/?token=…</c> 回 200 是再正常不过的事。
    /// 只看状态码的后果是：浏览器被开到不相干的进程上、token 进了它的访问日志，
    /// 而界面显示"运行中"、启动前的端口预检也被绕过（见 <see cref="ProbeServerAsync"/>）。
    /// </summary>
    private static async Task<bool> ProbeUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var budget = ProbeBudget(ct);
            using var response = await LocalHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            // 认证通过时引擎回 200 + SPA 首页，标记是标题那一行。
            var spa = await BodyContainsAsync(response, SpaTitleMarker, budget.Token);
            return IsDshHandshake(response.StatusCode, authMarkerSeen: false, spa);
        }
        catch { return false; }
    }

    /// <summary>
    /// 端口上是不是 DSH。<b>200 分支同样要验 body</b>，与 401 分支对称：
    /// 只看状态码时，任何本机 200 服务（dev server 尤其典型）都会被当成 DSH——
    /// 状态栏跟着误报"运行中"，而 <see cref="StartHarnessAsync"/> 里那道
    /// "端口被别的程序占用"的预检（走的就是本方法）会被一并绕过，
    /// 用户最终看到的是引擎 EADDRINUSE 的原始报错，正是那条预检要避免的结局。
    ///
    /// 误判方向：宁可说"不是 DSH"（多一次重新拉起），也不要把浏览器和 token
    /// 送到不相干的服务上。
    /// </summary>
    private static async Task<bool> ProbeServerAsync(int port, CancellationToken ct)
    {
        try
        {
            using var budget = ProbeBudget(ct);
            using var response = await LocalHttp.GetAsync($"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseHeadersRead, budget.Token);
            // 不带 token 时引擎回 401 + 那句提示；万一这条请求被认成了已认证（代理、
            // 未来版本的免登录开关），回的是 200 + SPA 首页——两条都验标记。
            // 分块读、命中即返回：RefreshStatusAsync 每 1.5 秒调一次本方法，
            // 正常场景第一个缓冲就够（那句话和那个 <title> 都在靠前的位置）。
            var auth = response.StatusCode == HttpStatusCode.Unauthorized
                && await BodyContainsAsync(response, AuthRequiredMarker, budget.Token);
            var spa = response.StatusCode == HttpStatusCode.OK
                && await BodyContainsAsync(response, SpaTitleMarker, budget.Token);
            return IsDshHandshake(response.StatusCode, auth, spa);
        }
        catch { return false; }
    }

    /// <summary>
    /// 响应 body 是否含指定子串：分块读、命中即返回。保留"尾部 = 子串长 - 1"的
    /// 滑动窗口，跨块边界的命中不会丢；body 异常长时内存也有界。
    /// </summary>
    private static async Task<bool> BodyContainsAsync(HttpResponseMessage response, string needle, CancellationToken ct)
    {
        // token 一路带到 stream 与 ReadAsync：预算到点（或调用方取消）时读必须能断，
        // 否则上面的预算只挡住了"拿响应头"，挡不住"读 body"——见 ProbeBodyTimeout。
        // 注意 StreamReader 只在 Memory<char> 重载上接收 token（char[] 四参重载不存在）。
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[4096];
        var window = new StringBuilder(needle.Length * 2);
        int read;
        // **必须异步读**。此前这里是同步的 reader.Read(...)：本方法由 ProbeServerAsync /
        // ProbeUrlAsync 调用，而后者挂在 RefreshStatusAsync（每 1.5 秒一轮）上，
        // 它的续延全在 UI 线程——于是 3080 上坐着一个"慢发 body"或"body 很大"的
        // 不相干服务时，整个窗体每 1.5 秒被同步阻塞到读超时（最长约 2 秒），
        // 表现为界面周期性卡住。与本文件"任何 IO 不挂 UI 线程"的纪律直接冲突。
        //
        // ConfigureAwait(false) 同样必要：它保证续延**不回到 UI 上下文**。
        // 只把 Read 换成 ReadAsync 而留着默认的上下文捕获，阻塞只是被挪到了下一帧，
        // UI 线程照样被占——那等于没修。
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            window.Append(buffer, 0, read);
            if (window.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // 丢掉窗口头部，只留可能与下一块拼出目标子串的尾巴。
            var keep = Math.Min(window.Length, needle.Length - 1);
            window.Remove(0, window.Length - keep);
        }
        return false;
    }

    private async Task RefreshStatusAsync()
    {
        if (refreshing || closing || IsDisposed) return;
        refreshing = true;
        try
        {
            var serverOn = await ProbeServerAsync(DefaultPort, CancellationToken.None);
            var ownProcess = Volatile.Read(ref dshProcess);
            var ownOn = ownProcess is not null && !ProcessHasExited(ownProcess);
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
                link.Text = "点击“启动”刷新认证链接";
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
                    ? "引擎未安装，点击“启动”会自动安装"
                    : $"引擎 {engine} · 点击“启动”启动";
                link.Text = "启动后自动打开认证链接";
            }
            lamp.Invalidate();
            UpdateButtons();

            // engine.migrating 定时归档：长期只复用不重启的用户，残留会永久占着 214 MB。
            // 每小时后台检查一次（有才动，没有零成本）。busy 时跳过；但先起跑的后台轮
            // 没跑完时用户点了「启动」，同进程两条归档仍会短暂并跑——认领靠同卷目录改名
            // 的原子性互斥（见 MigrateEngineOldToSlot），后到的那次只会 IOException 空转
            // 一轮，不损数据。
            if (!busy && DateTime.UtcNow - lastMigrateAttemptAt > MigrateThrottle)
            {
                lastMigrateAttemptAt = DateTime.UtcNow;
                // 必须挂异常观察：`_ = Task.Run(...)` 把任务丢出去就不管了，
                // 而这是本文件唯一一条"主动发起、无人 await"的归档路径——
                // 它抛出去的未观察异常会在 GC 时触发 TaskScheduler.UnobservedTaskException，
                // 与「永不让未观察异常退场」的纪律冲突（Program 里那个处理器会写 crash-log，
                // 于是用户会看到一条与崩溃毫无关系的记录）。
                // MigrateEngineOldToSlot 内部自己已经吞掉了绝大多数异常，这一道只是外层兜底。
                _ = Task.Run(MigrateEngineOldToSlot).ContinueWith(
                    static t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            // 这个方法被 1.5 秒一次的定时器驱动，而所有调用点都是 async void 事件处理器：
            // 异常冒出去没人接，会直接终止进程。刷新失败最多是状态文案不准，记日志即可。
            try { AppendStartupLog("刷新状态失败：" + ex.Message); } catch { }
        }
        finally { refreshing = false; }
    }
}
