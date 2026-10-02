// ── HarnessForm 的「启动 / 重启 / 停止，以及引擎 stdout 的 tail 读取」部分 ──────────────────────────────────────────
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

    // ---- 启动 / 重启 / 停止 -------------------------------------------------

    private async Task RunStartAsync(Button active, string busyText, bool reuseExisting)
    {
        if (busy || closing || IsDisposed) return;

        var cts = new CancellationTokenSource();
        CancelPendingStart();
        startCts = cts;
        EnterBusy(active, busyText, cts);

        // 插件开关只在这里读一次。原来是读两次：UpdatePluginsAfterStartAsync 在创建时
        // 读一次，本方法在 await startTask（最长十几秒）之后又读一次——而复选框在这期间
        // 一直可点（EnterBusy 没禁它）。用户中途取消勾选，第二次读就会走到
        // "跳过 AwaitQuietlyAsync" 那条路，把一个还在跑 pnpm 的任务丢在身后。
        var autoUpdate = autoUpdateCheckbox.Checked;

        // 在动任何东西之前先给配置拍一份快照。实测 DSH 的 settings 迁移会丢掉
        // 不匹配 profile 条目 id 的配置段（jet-hub 账号、llm-pi-ai 供应商都中过招），
        // 备份必须发生在"可能被改写"之前，事后再备份就晚了。
        // 内容没变化时不会重复生成，所以正常启动几乎零成本。
        // 放后台线程：哈希 + 复制是真实磁盘 I/O，%USERPROFILE% 落在漫游配置/网络盘
        // 时并不便宜（FoldersForm 对同类 I/O 同一条纪律），不能挂在 UI 线程上。
        await Task.Run(() => ConfigBackup.CreateSnapshot(reuseExisting ? "启动前" : "重启前"));

        // engine.old 的归档放在这里，而不是"打开版本管理窗口"时。
        //
        // 为什么不在"列举版本"里做：归档是改名 +（同名槽时）递归删除，是**写操作**；
        // 列举版本是只读操作。写操作挂在只读路径上，一旦对话框被重复打开就会有两个
        // 后台扫描同时对同一个 engine.<版本> 槽"删除 + 改名"。
        //
        // 为什么必须在 reuseExisting 早退**之前**：1.3.0 起引擎随启动器退出而存活，
        // 所以"引擎还活着"是常态，RunStartAsync 会在探到可用的 web-url.txt 后直接
        // 复用并 return，根本走不到 StartHarnessAsync。挂在引擎安装路径上等于
        // "只有引擎没起来时才归档"——那 README 承诺的"下次启动归档"就是假的。
        // 这里只碰 engine.old 与 engine.<版本>，**不碰 engine**，所以引擎在跑也安全。
        // 放在 EnterBusy 之后：busy 已禁用「版本」，不会与版本管理的扫描并发。
        // 放后台线程：同名槽的清理是 2.5 万文件的递归删除，不能挂在 UI 线程上。
        try { await Task.Run(MigrateEngineOldToSlot); } catch (Exception ex) { Swallow.Quiet(ex, "migrate-engine-old"); }

        Task? pluginTask = null;
        try
        {
            if (reuseExisting)
            {
                var known = await ResolveUsableUrlAsync(cts.Token);
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
            // 两个 Task 仍然同时创建，但插件更新在内部先等引擎启动收敛才动
            // profiles\node_modules：引擎引导时会重建 profile 的模块链接（用
            // node_modules.lock 串行化），pnpm update 不认那个锁、直接写同一棵树，
            // 首启/刚升级时重建窗口最长，并发就是真实的踩踏风险。
            // 插件的生效时机本就是「下次启动」，这里没有语义损失。
            var startTask = StartHarnessAsync(cts.Token);
            pluginTask = UpdatePluginsAfterStartAsync(startTask, cts.Token, autoUpdate);
            await startTask;
            if (cts.IsCancellationRequested) return;

            if (autoUpdate)
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
            // CTS 归本操作所有（见 CancelPendingStart 的注释），清 startCts 要判
            // 引用——被 CancelPendingStart 清空过就不该再动别人的槽位。
            if (ReferenceEquals(startCts, cts)) startCts = null;
            // EndBusy 判的是 busyOwner 而**不是** startCts：取消会清空 startCts，
            // 拿它判所有权会让"被 Esc 取消的这次操作"永远等不到 EndBusy，
            // busy 再无第二处复位 → 全部按钮永久禁用（见 busyOwner 字段注释）。
            if (ReferenceEquals(busyOwner, cts))
            {
                busyOwner = null;
                EndBusy();
            }
            // pluginTask 有可能还挂在后台（取消时 startTask 先结束、它后收尾）。
            // 那就不能在这儿释放 cts——不是"会抛"，而是它后续还要读 ct，
            // 依据一个已经被别人 Dispose 掉的对象。改由它自己在结束时释放。
            if (pluginTask is not null && !pluginTask.IsCompleted)
                _ = pluginTask.ContinueWith(
                    _ => { try { cts.Dispose(); } catch { } },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            else
                try { cts.Dispose(); } catch { }
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    /// <summary>
    /// 插件更新任务：与引擎启动同时创建，但内部先 await startTask 等引擎就绪，
    /// 再动 profiles\node_modules（详见 RunStartAsync 里的顺序说明）。
    /// 引擎启动失败/取消则本轮直接跳过——更新失败信息只走 startTask 自己的异常路径，
    /// 且这样收口后本任务永不带未观察异常退场。
    /// <paramref name="autoUpdate"/> 由调用方在建任务时就读定，**不要在这里重读复选框**：
    /// 调用方在 await startTask 之后还要用同一个值决定要不要等本任务收尾，
    /// 两边读的不一致就会把一个还在跑的任务丢在身后。
    /// </summary>
    private async Task UpdatePluginsAfterStartAsync(Task startTask, CancellationToken ct, bool autoUpdate)
    {
        if (!autoUpdate) return;

        try { await startTask; }
        catch { return; }
        if (ct.IsCancellationRequested || closing || IsDisposed) return;

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
        // busy 期间"停止"不再静默丢弃：启动/升级最长要几分钟，这段时间里按 Esc 或
        // 主按钮没有任何反馈，用户只能等或强杀。busy 时先取消 CTS——StartHarnessAsync
        // 在 process.Start() 前有 ThrowIfCancellationRequested；万一进程已拉起，
        // 紧接着的 StopHarnessProcessesAsync 会在同一 UI 线程上把它杀掉（不存在
        // 并发间隙，RunStartAsync 的 await 续延不可能插进来赋值新的 dshProcess）。
        // EnterBusy/EndBusy 的所有权：非忙碌时本方法自己占忙碌态（owner 就是一个
        // 只属于本次调用的对象）；忙碌时忙归 RunStartAsync / RunEngineUpgradeAsync
        // 所有——它们的 finally 按 busyOwner 自行复位。注意那句"自行复位"不再
        // 依赖 startCts（取消会把它清空，曾导致 busy 永不解除、整窗锁死）。
        var ownsBusy = !busy;
        var self = new object();
        if (ownsBusy)
        {
            // 独立停止按钮已并入主按钮（随状态切换语义），停止动作的忙碌态显示在主按钮上。
            EnterBusy(startButton, "停止中", self);
        }
        else
        {
            SetInfo("正在取消当前操作并停止引擎…");
        }
        try
        {
            // 忙碌时也必须走这一步：它是"取消正在跑的启动/升级"的唯一入口。
            // 只 Cancel、不 Dispose——CTS 归创建它的操作（见 CancelPendingStart）。
            CancelPendingStart();
            await StopHarnessProcessesAsync();
            // 与停止本体（StopHarnessProcessesCore）同持 authUrlGate：清空/删除
            // 必须与 tail 线程的写入互斥，否则"清完又被旧值写回"的窄缝始终敞着。
            lock (authUrlGate)
            {
                authenticatedUrl = null;
                TryDeleteUrlFile();
            }
        }
        catch (Exception ex)
        {
            if (!closing && !IsDisposed) ShowError("停止失败", ex.Message);
        }
        finally
        {
            // 判 busyOwner 而非无条件 EndBusy：万一中途有后继操作接管了忙碌态，
            // 这里不能替它收尾（同 RunStartAsync / RunEngineUpgradeAsync 的纪律）。
            if (ownsBusy && ReferenceEquals(busyOwner, self))
            {
                busyOwner = null;
                EndBusy();
            }
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    /// <summary>
    /// 取消进行中的启动/升级。**只 Cancel，不在这里 Dispose**。
    ///
    /// CTS 的所有权归**创建它的那个操作**（RunStartAsync / RunEngineUpgradeAsync），
    /// 由它的 finally 释放。理由不是"提前 Dispose 会抛异常"——实测 net8 上对已释放的
    /// CTS 做 Register / CreateLinkedTokenSource 都不抛（只有 Token.WaitHandle 与
    /// Cancel() 会），所以拿"会 ObjectDisposedException"当理由是错的。
    /// 真正的理由有两条：
    ///   ① 旧代码的 finally **从不释放** CTS，正常跑完一次就永久泄漏一个；
    ///   ② 谁拥有谁释放，这条纪律让"释放时机"只有一个地方说了算。本函数的注释
    ///      曾经给出一个在 net8 上不成立的前提，会误导下一个改这里的人。
    /// </summary>
    private void CancelPendingStart()
    {
        var cts = startCts;
        startCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
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
        // 走后台线程：锁文件存在时这里要做一次 ~140 ms 的 WMI 全量进程查询，
        // 留在 UI 线程上就是白屏半秒——与 StopHarnessProcessesAsync 开头同一条理由。
        await Task.Run(ClearOrphanProfileLock);

        // 端口被别的程序占用时，node 只会报 EADDRINUSE 然后立刻退出，
        // 界面最终显示的是"立即退出（代码 1）"——同学完全无从判断。
        // 这里在启动前先说清楚（走到这一步，本启动器自己的实例已被停干净）。
        if (await IsPortListeningAsync(DefaultPort, ct) && !await ProbeServerAsync(DefaultPort, ct))
        {
            var hint = await DescribeDynamicPortRangeAsync(DefaultPort);
            throw new InvalidOperationException(
                $"端口 {DefaultPort} 已被其他程序占用（不是本程序启动的 DSH）。\n" +
                $"请在命令行执行：netstat -ano | findstr :{DefaultPort}\n" +
                "找到占用进程后关闭它，再点「重启」。" +
                (hint is null ? string.Empty : "\n\n" + hint));
        }

        // 引擎 stdio 文件化：经 cmd 把 stdout/stderr 追加重定向进 engine-stdio.log。
        // 不再用管道的理由见字段区注释（启动器死亡 → 断管 → 引擎陪葬，实测 ~1 秒）。
        // 复用路径到不了这里；只有真拉新引擎才截断日志并换代游标。
        //
        // 顺序刻意是：换令牌 → 截断 → 以**截断后的文件末尾**为起点建新游标 → 起跑。
        // 游标不能再从 0 读：日志是 cmd `>>` 追加、历史仍在盘上（只有超过 8 MB 才截尾），
        // 从头读等于把上一个会话整体回放一遍——旧 `?token=` 行会回填 authenticatedUrl、
        // 弹出死链接标签页、写脏 web-url.txt，等待循环还会在**新引擎就绪之前**
        // 因 authenticatedUrl != null 提前判成"启动成功"。此前注释与 README 说的
        // "每次重新拉起时重开"从未被实现过（没有任何删除/清空动作），这条就是那笔债。
        // 而截断发生在换代之际、旧循环可能正卡在一次 Feed 中间（`Length < Pos`
        // 会把它复位到 0 拿到整段保留尾部）——所以分发处另有逐行代际守卫，两条路各堵各的。
        var tailToken = Interlocked.Increment(ref engineTailToken);
        // 摘要缓冲随引擎换代清空：它喂的是「启动失败」弹窗里的「最后输出」。
        // 不清的话，新引擎零输出即崩时弹窗里躺着的是上一代引擎/上一次 npm install
        // 的行——既误导排障，又让"未输出任何日志 → 给出日志路径与自检指引"
        // 那个分支（summary.Contains("未输出任何日志")）永远轮不到触发。
        // 与"新游标从截断后末尾起步"同一语义边界：一代引擎一份摘要。
        lock (recentOutput) recentOutput.Clear();
        try
        {
            Directory.CreateDirectory(LocalAppDir);
            // 走后台线程：截断 >8 MB 时要同步读写整整 8 MB，留在 UI 线程上就是一次
            // 可感知的停顿（与下面替换三步、RunStartAsync 里的归档同一个理由）。
            await Task.Run(TruncateEngineLog);
        }
        catch { }
        // 取长度单独守卫：截断可能因共享冲突抛（Defender 正扫描），但那不该把起点
        // 拖回 0——起点=0 就是这次修掉的"回放历史"本身。只读探测不会撞写锁，
        // 它自己失败时文件多半已不存在，引擎也无从追加，0 才是正确答案。
        long cursorStart;
        try { cursorStart = File.Exists(engineStdioLog) ? new FileInfo(engineStdioLog).Length : 0; }
        catch { cursorStart = 0; }
        engineLogCursor = new LogCursor(cursorStart);
        // 游标**在提交任务时**就取出来当参数传进去，不能让后台循环自己去读字段。
        // 原先是在 EngineTailLoopAsync 函数体第一行 `var cursor = engineLogCursor`：
        // 那行跑到线程池线程上才执行，而 Task.Run 的执行时机不确定——线程池饥饿
        // （或关窗/重启期间池紧张）叠加"在这几毫秒里又完成了一次完整启动"时，
        // 上一代循环会读到**新一代**的游标，于是两代循环共享同一个 LogCursor：
        // `cursor.Pos += len` 与 `Remainder` 被两个线程同时改写，而这两处**不受
        // 代际令牌守卫约束**（守卫只管"要不要分发这一行"）。注释里"游标在入口捕获
        // 一次……每代一个循环、互不共享"承诺的正是不发生这件事——那就必须真的
        // 在提交时捕获，把"入口"钉在提交那一刻。
        var tailCursor = engineLogCursor;

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            // /s：整条外层引号原样交给 cmd；>> 与 2>&1 由 cmd 完成，node 拿到的是文件句柄。
            Arguments = "/d /s /c \"\"" + node + "\" \"" + EngineEntryScript +
                        "\" web --no-open --host 127.0.0.1 --port " + DefaultPort +
                        " >> \"" + engineStdioLog + "\" 2>&1\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["DSH_HOME"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        // 必须读**父进程**的 PATH：psi.Environment 是子进程环境的"覆盖表"，初始为空，
        // 从它读 ["PATH"] 永远是 null → 去重判断形同虚设，还会把子进程 PATH 整个覆盖成
        // 只剩 nodeDir（子进程环境 = 父环境 + 覆盖表）。System32、git 等会从引擎的
        // 环境里消失，属"本机碰巧能跑、别人机器上莫名失败"的坑。
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var nodeDir = Path.GetDirectoryName(node);
        if (!string.IsNullOrWhiteSpace(nodeDir) && !path.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(nodeDir, StringComparer.OrdinalIgnoreCase))
            psi.Environment["PATH"] = nodeDir + ";" + path;
        ConfigureOptionalProxy(psi);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (closing || IsDisposed) return;
            // 读侧必须走 Volatile.Read：Exited 回调在线程池线程上跑，而 dshProcess
            // 由 UI 线程写（见字段注释的纪律）。
            if (ReferenceEquals(Volatile.Read(ref dshProcess), process))
            {
                try { BeginInvoke(UpdateButtons); } catch { }
            }
        };
        // 取消检查点必须紧贴 process.Start()：关窗时 FormClosing 已经跑完
        // CancelPendingStart + StopEngineForExit，而消息泵仍可能分发此前已排队的
        // 本方法续延（点 ✕ 落在 await Task.Run(ClearOrphanProfileLock) 或
        // IsPortListeningAsync 的那几百毫秒里就会这样）。中间若没有这道检查，
        // 引擎会被拉起、dshProcess 被赋值、然后 653 行才 return——而那时
        // StopEngineForExit 早已执行完，没人再管它：窗口关了、3080 上留着一个孤儿。
        ct.ThrowIfCancellationRequested();

        if (!process.Start()) throw new InvalidOperationException("无法启动 DSH 引擎。");
        Volatile.Write(ref dshProcess, process);
        // 文件 tail 替代原来的管道事件：认证链接捕获、进度行、recentOutput 摘要都走它。
        // 文件 tail 放到后台线程跑：循环体每 250ms 要开一次日志文件读增量，
        // 留在 UI 线程上时（async 方法沿 WinForms 上下文恢复）一次磁盘卡顿
        // （engine 目录被 Defender 实时扫描正是实测会卡的地方）就直接变成界面顿挫。
        // HandleProcessLine 里所有 UI 改动本来就走 BeginInvoke，切到后台无需其他改动。
        _ = Task.Run(() => EngineTailLoopAsync(process, tailToken, tailCursor));

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
                // 与等待循环尾部（769 行一带）同一条取消语义：取消就是取消，
                // 静默 return。此前这里空 catch 吞掉 OCE 后继续往下走，一次取消
                // 会被转写成"DeepSeek Harness 立即退出"的启动失败——今天只是被
                // closing 守卫遮住看不见，将来谁加一条"非关窗取消"路径，这里
                // 立刻变成假报错。
                try { await Task.Delay(300, ct); }
                catch (OperationCanceledException) { return; }
                var code = SafeExitCode(process);
                var codeText = code >= 0
                    ? $"代码 {code}"
                    : "未知（进程句柄已被释放——通常是启动器自身在清理该进程，而不是引擎崩溃）";
                var (summary, summaryEmpty) = RecentOutputSummaryParts();
                if (summaryEmpty)
                {
                    summary += "\n（引擎输出已改到日志文件，可直接打开查看：" + engineStdioLog +
                              "；也可点「环境」查看引擎与 Node 状态，" +
                              $"或手动执行 node \"{EngineEntryScript}\" web --no-open --port {DefaultPort} 复现）";
                }
                throw new InvalidOperationException(
                    $"DeepSeek Harness 立即退出（{codeText}）。\n{summary}\n请检查 Node 与引擎安装（可点「环境」自检）。");
            }

            if (!portAppeared && await IsPortListeningAsync(DefaultPort, ct)) portAppeared = true;

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

    /// <summary>
    /// 引擎输出摘要。返回 (文本, 是否为空)。
    /// 拆成 out 参数而不是让调用方去 Contains("未输出任何日志")：那段魔法字符串
    /// 把"措辞"和"语义"绑死了——改一次文案（比如把"未输出"改成"没有输出"），
    /// 那个分支就静默失效，且没有任何测试会发现。
    /// </summary>
    private (string Text, bool Empty) RecentOutputSummaryParts()
    {
        lock (recentOutput)
        {
            if (recentOutput.Count == 0) return ("（DSH 未输出任何日志）", true);
            return ("最后输出：\n" + string.Join("\n", recentOutput), false);
        }
    }

    private string RecentOutputSummary() => RecentOutputSummaryParts().Text;

    private void HandleProcessLine(string? line, int token)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = AuthUrlRegex.Match(line);
        string? url = match.Success
            ? match.Value.TrimEnd('.', ',', ';', ')', ']', '\x1b')
            : null;
        if (url is not null)
        {
            // 端口归属校验：引擎固定以 --port 3080（DefaultPort，const）拉起，日志里
            // 若出现指向**其他端口**的 token URL（第三方插件的输出、未来引擎打印的
            // 回调地址等），采纳它只会写脏 web-url.txt、顶掉 authenticatedUrl、把浏览器
            // 带去陌生地址。与探针同一失手方向：端口对不上就不当认证链接，按普通日志行走。
            var urlPort = ExtractPort(url);
            if (urlPort is null || urlPort != DefaultPort) url = null;
        }
        if (url is null)
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

        // 写入前的二次代际核对。DispatchEngineLogText 的逐行守卫通过之后，本方法可能
        // 被调度延迟到「杀引擎 → RetireEngineTail → 清 authenticatedUrl / 删
        // web-url.txt」（StopHarnessProcessesAsync 1153-1158 一带）之后才执行——
        // 那会把刚清掉的死 token 又写回去，重启等待循环随即在**新引擎起跑前**误判
        // "启动成功"。这正是 1.4.1 修过两次的"authenticatedUrl 复活"失效形状剩下的
        // 一条 check-then-act 缝：守卫在上游查过一次，不等于写进数据库的那一刻还成立。
        //
        // 核对与写入同持 authUrlGate 后，这条缝只剩"锁内核对通过"这一种通过方式：
        // 停止路径的"退役 + 清字段 + 删文件"在另一侧持同一把锁——要么本写先完成
        //（停止的清理随后覆盖它，终态正确），要么退役先发生（下面的核对失败，
        // 不再写）。
        // 同代只认**第一条**：引擎重启内部 web server、请求日志回显带 token 的完整
        // URL、drain 阶段的尾行，都会再走一遍这里——每条都采纳等于每条都多开一个
        // 浏览器标签，还可能拿一条过期/陌生 URL 顶掉对的（authenticatedUrl 与
        // web-url.txt 都是"最后一次写赢"）。跨代不受影响：停止路径清空字段，
        // 新引擎起跑后第一条照常捕获。
        if (!TailGenerationAlive(token, engineTailToken)) return;
        lock (authUrlGate)
        {
            if (!TailGenerationAlive(token, engineTailToken)) return;
            if (authenticatedUrl is not null) return;
            authenticatedUrl = url;
            lastPort = ExtractPort(url) ?? DefaultPort;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(urlFile)!);
                // DPAPI 加密落盘：token 只有当前 Windows 用户能解开，其他进程拿到文件也没用。
                DpapiFile.WriteAllText(urlFile, url);
            }
            catch { }
        }

        if (closing || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (closing || IsDisposed) return;
                // UI 回调排队期间令牌可能已退役（刚点完「停止」）：不能把死链当新会话打开。
                if (!TailGenerationAlive(token, engineTailToken)) return;
                link.Text = "打开 DeepSeek Harness 控制台";
                UpdateButtons();
                OpenBrowser(url);
            });
        }
        catch { }
    }

    /// <summary>
    /// engine-stdio.log 的增量读取：每 250ms 把新字节解码、按行喂给
    /// <see cref="HandleProcessLine"/>——替代原来的 stdout/stderr 管道事件。
    /// 退出时代际令牌一换，上一任循环自行收工；进程退出后再把尾巴读干净
    /// （崩溃前的最后几行日志就在那里，"启动失败"摘要全靠它）。
    /// 整段自吞异常：这是个后台循环，任何一轮读失败下一轮继续即可。
    /// 游标在**入口捕获一次**：之后即使 UI 线程为新引擎换了新游标，本循环也
    /// 只读写自己捕获的这份旧游标，不存在与复位交错写共享字段的窗口。
    /// 分发是逐行核对代际令牌的（见 <see cref="DispatchEngineLogText"/>），
    /// 所以"正在读的那一段"也不会越代外流——被取代后哪怕手里攥着刚读到的
    /// 上一会话 token 行，也一行都不会喂出去。
    /// </summary>
    private async Task EngineTailLoopAsync(Process process, int token, LogCursor cursor)
    {
        // cursor 由**提交任务时**捕获并传进来（见调用点注释）。在这里读字段会让
        // 上一代循环在执行时机被推迟时读到新一代的游标，两代共享同一对象、
        // 并发改写 Pos 与 Remainder——而那两处不受代际守卫约束。
        var buffer = new byte[16 * 1024];
        while (true)
        {
            if (!TailGenerationAlive(token, engineTailToken)) return;
            try
            {
                FeedEngineLogChunk(cursor, buffer, token);
            }
            catch { }

            if (!TailGenerationAlive(token, engineTailToken)) return;
            if (ProcessHasExited(process))
            {
                // 退出检测与文件写入之间总有先后差：最后多读几轮，把没落完的日志收干净。
                // 收尾排空不受代际守卫影响：那时没有新引擎起跑、令牌没换过，照常喂完。
                //
                // 停止条件必须是"**没读到新字节**"，不能是"这一轮没分发出行数"。
                // 引擎崩溃时最后一行往往**没有结尾换行**——那正是最该进"启动失败"
                // 摘要的一行，而 DispatchEngineLogText 对它切不出换行、
                // 把它留在 Remainder 里返回 0，于是旧判据在读完文件的第一轮就 break。
                // cursor.Pos（已读字节偏移）是"确实读到了什么"的权威口径。
                var lastPos = cursor.Pos;
                for (var drain = 0; drain < 4; drain++)
                {
                    try { FeedEngineLogChunk(cursor, buffer, token); } catch { break; }
                    if (cursor.Pos == lastPos) break;     // 没有新字节 = 真的读完了
                    lastPos = cursor.Pos;
                    await Task.Delay(150);
                }

                // 最后把**没有结尾换行的残余**当一行喂出去。上面改用 Pos 口径只解决了
                // "不要提前 break"，那一行仍留在 Remainder 里没人管——而它恰恰是崩溃
                // 的最后一句（最可能是报错行）。引擎正常退出时文件以 '\n' 收尾，
                // Remainder 为空，这里是空操作。
                if (cursor.Remainder.Length > 0 && TailGenerationAlive(token, engineTailToken))
                {
                    var tail = cursor.Remainder;
                    cursor.Remainder = string.Empty;
                    HandleProcessLine(tail.TrimEnd('\r'), token);
                }
                return;
            }
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// tail 的全部可变状态。一个引擎一代：新引擎起跑时整体换新对象（见
    /// <see cref="engineLogCursor"/> 的注释），旧循环持有旧对象自洽退场。
    /// 字段刻意 public：只在 tail 循环单线程访问（每代一个循环、互不共享），
    /// 无并发写面，包一层属性只增加噪音。
    /// **没有无参构造**：起点必须显式给出。"从 0 起步"在追加式日志上等于
    /// 回放整个历史（旧会话的 token 行照发不误）——1.4.1 起这是编译期禁止
    /// 的形状，而不是只靠注释提醒的约定。internal（而非 private）是为了被单测钉住。
    /// </summary>
    internal sealed class LogCursor
    {
        internal LogCursor(long startPos) => Pos = Math.Max(0, startPos);

        public long Pos;                                        // 已读到的字节偏移
        public Decoder Decoder = Encoding.UTF8.GetDecoder();
        public string Remainder = string.Empty;                 // 未完成的半行
    }

    /// <summary>未成行部分的长度上限。引擎正常输出不会写出这么长的单行；超过就说明
    /// 它写了不该写的东西（无换行的进度串、损坏的编码流）。丢了开头总比让字符串
    /// 无界增长好——这正是本护栏存在的理由。</summary>
    private const int MaxPendingLogChars = 16 * 1024;

    /// <summary>把新增字节解码成字符并按行分发；返回本次分发的行数。</summary>
    private int FeedEngineLogChunk(LogCursor cursor, byte[] buffer, int token)
    {
        if (!File.Exists(engineStdioLog)) return 0;
        using var fs = new FileStream(engineStdioLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < cursor.Pos)
        {
            // 文件被截断重写（或异常缩小）：偏移与解码状态全部复位。
            // 注意此刻代际多半已经换过——分发处有逐行守卫兜底，复位后读到的
            // 历史内容不会被喂出去，只会被丢弃（下一次换代由新游标从末尾续读）。
            cursor.Pos = 0;
            cursor.Decoder = Encoding.UTF8.GetDecoder();
            cursor.Remainder = string.Empty;
        }
        if (fs.Length == cursor.Pos) return 0;
        fs.Seek(cursor.Pos, SeekOrigin.Begin);

        var lines = 0;
        int len;
        while ((len = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            cursor.Pos += len;
            var chars = new char[cursor.Decoder.GetCharCount(buffer, 0, len)];
            var n = cursor.Decoder.GetChars(buffer, 0, len, chars, 0);
            lines += DispatchEngineLogText(cursor, new string(chars, 0, n), token);
        }
        return lines;
    }

    /// <summary>把未成行部分压回上限：只保留尾部。</summary>
    private static string CapRemainder(string value) =>
        value.Length > MaxPendingLogChars ? value[^(MaxPendingLogChars / 2)..] : value;

    private int DispatchEngineLogText(LogCursor cursor, string text, int token)
    {
        var data = cursor.Remainder + text;
        var cut = data.LastIndexOf('\n');
        if (cut < 0)
        {
            // 一行迟迟不成形（引擎理论上不会这样，防超长行撑爆内存）：留尾巴，丢旧头。
            cursor.Remainder = CapRemainder(data);
            return 0;
        }
        // 换行符**之后**的尾巴同样要过护栏。原实现只护了 cut < 0 这一支，于是
        // "换行 + 后面接一大段没有换行的内容"（pnpm 的裸 \r 进度串、损坏的编码流）
        // 会把 Remainder 顶到无界增长——那恰恰是最容易触发的那种形态。
        cursor.Remainder = CapRemainder(data.Substring(cut + 1));
        var lines = 0;
        foreach (var raw in data.Substring(0, cut + 1).Split('\n'))
        {
            // 逐行代际守卫：本循环一旦被新引擎取代，哪怕手里正攥着一整段刚读进来的
            // 内容也立刻停止分发。失手代价全在"不报错"那一侧：上一会话的 token 行
            // 回填 authenticatedUrl、OpenBrowser 弹死链接、写脏 web-url.txt，
            // 启动等待循环还会在新引擎就绪前提前返回"成功"。
            if (!TailGenerationAlive(token, engineTailToken)) return lines;
            lines++;
            HandleProcessLine(raw.TrimEnd('\r'), token);
        }
        return lines;
    }
}
