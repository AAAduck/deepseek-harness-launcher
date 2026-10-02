// ── HarnessForm 的「进程管理：WMI 快照、残留匹配、停止与退出清扫」部分 ──────────────────────────────────────────
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

    // ---- 进程管理 -----------------------------------------------------------

    private async Task StopHarnessProcessesAsync()
    {
        // 查杀整体放后台线程：WMI 全量查询实测约 140 ms，加上逐个 Kill，
        // 同步跑会把 UI 线程冻住半秒——方法名带 Async 就不该在调用线程上干这些。
        await Task.Run(StopHarnessProcessesCore);
        // dshProcess 的 Dispose/置 null 收回到 UI 续延上做（Core 里曾经顺手做了，
        // 但那是后台线程写 UI 所属字段）。杀干净后句柄上的进程必然已退出，
        // Dispose 只是释放内核句柄、无副作用，放这里与原先语义一致。
        try { Volatile.Read(ref dshProcess)?.Dispose(); } catch { }
        Volatile.Write(ref dshProcess, null);
        // 引擎没了，它的 tail 循环也必须在这一刻退役：否则旧循环的退场排空
        // （最多 4×150 ms）还能以当前代令牌通过逐行守卫，把刚删掉的 web-url.txt
        // 重写成过期 token、复活 authenticatedUrl。见 RetireEngineTail。
        RetireEngineTail();
        // 引擎进程没了，上一份认证链接就是过期 token。不在这里清空的话，
        // 「重启」/「升级后重启」的等待循环会在下一个引擎还没输出任何日志时
        // 因 authenticatedUrl != null 立刻"成功返回"，浏览器先弹出过期 token 的失败页。
        authenticatedUrl = null;
        TryDeleteUrlFile();
        // 刚杀完必须让进程快照缓存作废：否则紧接着的 ClearOrphanProfileLock
        // 拿到的是"杀之前"的缓存（1 秒 TTL，而端口通常几百毫秒内就关、等不到过期），
        // 把已死进程当成活残留，拒绝清孤儿锁——恰好复现这个功能本来要防的启动失败。
        InvalidateProcessRecordCache();
    }

    private void StopHarnessProcessesCore()
    {
        var records = GetProcessRecords();
        // 读 WMI 取 CreationDate 是"这是不是当初那个进程"的依据，但 WMI 查询本身
        // 会失败（服务被拦、权限不足、瞬时故障），而失败时 GetProcessRecords 返回的是
        // **空字典**——不是抛异常。原先空字典直接让下面整个杀进程循环空转：
        // 一个都杀不掉、界面回到"未运行"、日志里一个字都没有。
        // 这条必须有痕迹：它正是"界面说停了、引擎其实还在 3080 上活着"那种故障。
        if (records.Count == 0)
            AppendStartupLog("进程快照为空（WMI 查询失败或被拦截）：本轮只结束本启动器自己拉起的引擎进程树。");

        var current = Volatile.Read(ref dshProcess);
        if (current is not null && !ProcessHasExited(current))
        {
            // **手里握着活句柄本身就是同一性证明**：这个 Process 对象是我们自己 Start
            // 出来的，内核句柄一直指着那个进程，既不会被 PID 复用骗到，也不需要
            // WMI 快照来证明。所以它不必等 records 里有对应条目——先杀。
            try
            {
                current.Kill(entireProcessTree: true);
            }
            catch (Exception ex) { AppendStartupLog("结束本启动器拉起的引擎进程树失败：" + ex.Message); }
        }

        var seeds = records.Values.Where(IsHarnessCommand).Select(x => x.Id).ToHashSet();
        // dshProcess 只在上面读一次（取不在这里用：句柄那条路已经直接杀过了）。
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
            // Process 对象持有内核句柄，用完即收、不等 GC（与 Program.ActivateExistingWindow 同一纪律）。
            // PID 复用防护：GetProcessById 之后带容差比对 StartTime（见 IsSameProcessStart——
            // 严格相等在 WMI 的微秒精度下连"同一个进程"都判不等，实测单进程命中率仅约 1/5，
            // 曾经让「停止」大概率空转且不报错）。PID 被复用时创建时间必然相差秒级以上，
            // 容差比对照样跳过——宁可漏掉一个残留，也不能误杀同 PID 的新进程。
            if (!records.TryGetValue(id, out var expected)) continue;
            // WMI 没给出 CreationDate 时回退成 DateTime.MinValue，于是
            // IsSameProcessStart 必然判不等 → 这个进程**永远杀不掉、且无任何痕迹**。
            // 与其那样，不如在没有对照依据时**跳过并留痕**：PID 刚被系统复用的
            // 概率极低，为此冒误杀一个无关进程不值得；但"杀不掉"必须是可见的。
            if (expected.StartTime == DateTime.MinValue)
            {
                Swallow.Quiet(
                    new InvalidOperationException($"PID {id} 的 WMI CreationDate 缺失，无法确认同一性，已跳过"),
                    "kill-skip-no-creationdate");
                continue;
            }
            try
            {
                using var victim = Process.GetProcessById(id);
                if (!IsSameProcessStart(victim.StartTime, expected.StartTime))
                {
                    // PID 复用：现在占用这个 PID 的是另一个进程。留痕——它每轮都会命中，
                    // 而"杀不掉"若无声无息，用户只会看到「停止」似乎没起作用。
                    Swallow.Quiet(
                        new InvalidOperationException(
                            $"PID {id} 的创建时间与快照不符（疑似 PID 复用），已跳过该进程"),
                        "kill-skip-pid-reused");
                    continue;
                }
                victim.Kill(entireProcessTree: true);
            }
            catch (Exception ex) { Swallow.Quiet(ex, "kill-process"); }
        }
        // dshProcess 是 UI 线程字段：Dispose 与置 null 收回 StopHarnessProcessesAsync 的
        // await 之后、在 UI 续延上做，避免后台线程写 UI 所属状态的跨线程写。
        // 只读字段（拿句柄去 Kill）不受此限——Process 对象本身线程安全，
        // 而且这一步必须能在 WMI 快照不可用时独立生效（见上面那段注释）。
    }

    /// <summary>
    /// 关窗退出时结束引擎。与「停止」按钮的区别：这里必须**快**，因为它在窗体的
    /// 关闭路径上，阻塞会让窗口卡住不消失。
    /// 先直接杀掉已知的进程树（不查 WMI）；WMI 定向清扫**只在复用路径**上补一次——
    /// 复用的引擎是上一个（可能已崩溃的）实例拉起的，dshProcess 为 null，
    /// 这次扫描是关窗时结束它的唯一手段。本实例自己拉起的引擎树已被句柄整树
    /// 击杀（句柄即同一性证明），再扫一遍只是重复回答已知的事：白白把 ~140 ms
    /// 的 WMI 全量查询压进关窗的 UI 线程（WMI 受损的机器上可到数秒）。
    /// 更陌生的残留（别处启动、连复用探针都没对上的）宁可漏掉——下次启动的
    /// 端口预检会给出明确报错兜住（见 MatchesEngineProcess 的注释）。
    ///
    /// ⚠ 但"复用路径"（dshProcess == null）是 1.3.0 起的**常态**，而复用路径下
    /// 这一段会在关窗的 UI 线程上同步跑一次全量 WMI：本机约 140 ms，
    /// WMI 提供程序受损/被拦的机器上可到数秒——窗口就是"点 × 然后僵在那"。
    /// 与本方法开头"这里必须快"的意图直接冲突。
    /// 现在把 WMI 那一段放到后台线程，并**限时**等待：正常机器上它先完成（≈140 ms，
    /// 窗口照旧瞬间消失）；机器不正常时等满预算就走——让窗口消失比杀掉那个残留重要，
    /// 残留还有下次启动的端口预检兜着。
    /// </summary>
    private void StopEngineForExit()
    {
        var ownProcess = Volatile.Read(ref dshProcess);
        var ownEngine = ownProcess is not null;
        try
        {
            if (ownProcess is not null && !ProcessHasExited(ownProcess))
                ownProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { AppendStartupLog("退出时结束引擎失败：" + ex.Message); }
        try { ownProcess?.Dispose(); } catch { }
        Volatile.Write(ref dshProcess, null);
        // 退出路径同样要退役 tail 代际：HandleProcessLine 是在写入 authenticatedUrl
        // 与 web-url.txt **之后**才检查 closing 的，所以引擎临死前最后几行日志里的
        // token 行仍会被这条排空路径吃掉并落盘（下一双击时就是一条死链）。
        RetireEngineTail();

        if (!ownEngine)
        {
            // 后台跑 + 限时等：正常机器上它先完成（≈140 ms，窗口照旧瞬间消失）；
            // WMI 受损的机器上等满预算就走——"让窗口消失"比"杀掉那个残留"重要。
            var sweep = Task.Run(() => KillEngineProcessesCore());
            try
            {
                // Wait(预算) 而不是 Wait()：UI 线程被挂住 = 窗口不消失。
                // 超时后任务继续在后台跑完（它不碰任何 UI 状态），代价为零。
                if (!sweep.Wait(ExitSweepBudget))
                    AppendStartupLog(
                        $"关窗时结束残留引擎的后台清理超过 {ExitSweepBudget.TotalMilliseconds:F0} ms 仍未完成，" +
                        "已不等待（引擎可能残留，下次启动的端口预检会兜住）。");
            }
            catch (Exception ex) { Swallow.Quiet(ex, "exit-sweep-wait"); }
        }
        InvalidateProcessRecordCache();
    }

    /// <summary>关窗时结束残留引擎的等待预算。超过它就不等了——见调用点注释。</summary>
    private static readonly TimeSpan ExitSweepBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 关窗时的残留引擎清扫本体。跑在后台线程上（调用点限时等待）。
    /// 逐条处理与 StopHarnessProcessesCore 同纪律：缺 CreationDate 跳过并留痕、
    /// PID 复用跳过、Kill 失败留痕。
    /// </summary>
    private void KillEngineProcessesCore()
    {
        try
        {
            foreach (var record in GetProcessRecords().Values.Where(IsEngineProcess))
            {
                if (record.StartTime == DateTime.MinValue)
                {
                    Swallow.Quiet(
                        new InvalidOperationException($"PID {record.Id} 的 WMI CreationDate 缺失，无法确认同一性，已跳过"),
                        "kill-skip-no-creationdate");
                    continue;
                }
                try
                {
                    using var victim = Process.GetProcessById(record.Id);
                    if (!IsSameProcessStart(victim.StartTime, record.StartTime)) continue;
                    victim.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { Swallow.Quiet(ex, "kill-process"); }
            }
        }
        catch (Exception ex) { Swallow.Quiet(ex, "kill-engine-sweep"); }
    }

    /// <summary>
    /// 两个"进程创建时间"读数是否指向同一个进程的启动。纯函数、可单测——
    /// 它守着两条杀进程路径（「停止」与关窗清扫），判错了不会报错。
    ///
    /// 为什么必须带容差而不是严格相等：两个读数的精度不同——
    /// WMI 的 Win32_Process.CreationDate 是 DMTF 微秒精度（6 位小数），
    /// 而 Process.StartTime 是 100ns 精度（FILETIME 原值）。同一个进程的
    /// 两个读数因此恒差 0–0.9 µs（实测本机 21 个进程样本里仅 3 个严格相等，
    /// 新起 5 个 cmd.exe 仅 1 个命中；差值全落在 0.1–0.9 µs、方向恒为
    /// StartTime ≥ WMI）。严格相等会把"同一个进程"判成"PID 被复用了"，
    /// 于是杀进程循环对真正的目标也跳过——「停止」大概率空转且不报错。
    ///
    /// 容差选 1ms：比最大读数差（0.9 µs）大三个数量级，足以吸收任何精度损失；
    /// 而 PID 复用后新进程的创建时间必然与旧读数相差秒级以上（复用前提是旧句柄
    /// 全部关闭、旧进程已完全退出），1ms 与之相比可忽略——防护语义不变。
    /// </summary>
    internal static bool IsSameProcessStart(DateTime a, DateTime b) =>
        Math.Abs((a - b).Ticks) <= TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// 只认「命令行里带本启动器引擎目录」的进程，用于退出清扫。
    /// 与 IsHarnessCommand 同样必须先排除桌面客户端：它的引擎宿主命令行里
    /// 也含 @deepseek-ai/dsh，但那是客户端自己的，不是我们启动的。
    /// </summary>
    private bool IsEngineProcess(ProcessRecord p) =>
        MatchesEngineProcess(p.Name, p.CommandLine, engineDir);

    /// <summary>
    /// 引擎进程的镜像名形态：node（引擎本体）、cmd（stdio 重定向到 engine-stdio.log
    /// 的包装层）、npx（早期残留的启动方式）。其余进程名——编辑器、资源管理器、任何
    /// GUI 工具——即便命令行里带着引擎目录下的文件路径（用户用编辑器打开了 bin.js
    /// 是最现实的形状），也不是引擎，绝不能进杀进程名单。两条杀进程路径共用本判定。
    /// </summary>
    private static bool IsEngineProcessShape(string name) =>
        name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("npx.cmd", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("npx.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Electron 形态：DSH 桌面客户端用它自己的可执行文件跑引擎宿主
    /// （<c>"…\DeepSeek Harness.exe" --expose-internals …\resources\app.asar\dsh\…</c>），
    /// 不是 node.exe，所以 <see cref="IsEngineProcessShape"/> 认不出它。
    /// 这里单独列出，且**只**用于"有没有人可能正持锁"这类只读判断——
    /// 杀进程路径绝不能因此把客户端卷进来（见 <see cref="MatchesHarnessCommand"/>）。
    /// </summary>
    private static bool IsElectronProcessShape(string name) =>
        name.StartsWith("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("electron.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个进程**可能**正持有 ~/.dsh/profiles/node_modules.lock（纯函数、可单测）。
    ///
    /// 为什么不能借用 <see cref="IsHarnessCommand"/>：那条判据为了"不误杀"而明确排除了
    /// 桌面客户端（app.asar / dsh-desktop-host / dsh-subprocess 一律出局）。
    /// 而这把锁**恰恰就是桌面客户端的引擎在引导时持有的**——客户端与本启动器共用同一个
    /// %USERPROFILE%\.dsh\profiles。客户端正持锁引导时用户双击启动本启动器，
    /// 借来的判据看不到任何"活着的引擎" → 活锁被删 → 两个引擎并发写同一份 profile，
    /// 正是这把锁要防的踩踏。
    ///
    /// 两条判据的方向因此相反，这里也是：
    ///   • <see cref="IsHarnessCommand"/> 宁可漏杀（杀错不可逆）；
    ///   • 本函数宁可多认（漏认 = 删掉活锁，是这里唯一不可逆的错误）——
    ///     代价仅仅是"这轮不清锁"，而孤儿锁下次启动 WMI 正常时仍会被清掉。
    /// </summary>
    internal static bool MayHoldProfileLock(string name, string commandLine, string userHomeDir)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(commandLine)) return false;
        // 启动器自己不会持锁（它不跑引擎引导），排除掉免得自己把自己当成"有活锁"。
        if (string.Equals(name, "DeepSeekHarness.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsEngineProcessShape(name) && !IsElectronProcessShape(name)) return false;
        if (string.IsNullOrEmpty(userHomeDir) || !ContainsPathSegment(commandLine, userHomeDir)) return false;
        return commandLine.Contains("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains("@deepseek-ai\\dsh", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(".dsh\\profiles", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(".dsh/profiles", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 引擎目录下 dsh 包的完整路径。命令行里出现它，进程才可能与本引擎有关——
    /// 这是比"引擎目录"更精确一档的锚点：引擎目录本身会被任何"以该目录下文件为
    /// 参数"的进程撞上（编辑器、node 跑用户自己放在引擎目录下的脚本……）。
    /// internal（而非 private）：匹配口径要被单测直接钉住。
    /// </summary>
    internal static string EnginePackageDirUnder(string engineDir) =>
        Path.Combine(engineDir, "node_modules", "@deepseek-ai", "dsh");

    /// <summary>
    /// 退出清扫的匹配内核（纯函数、可单测）。它与 <see cref="MatchesHarnessCommand"/>
    /// 是**两条彼此独立的杀进程路径**（这里=关窗，那边=点「停止」），两者只有一条交集：
    /// 都绝不能碰桌面客户端的引擎宿主。改其中一个时别忘了另一个——它们已经分叉过一次了。
    ///
    /// 判据刻意比「停止」那条窄：只认"命令行带引擎内 dsh 包目录"的进程，不做宽松兜底。
    /// 关窗时宁可漏掉一个陌生残留（下次启动的端口探测会给出明确报错兜住），
    /// 也不能整树杀掉一个和本启动器毫无关系的进程。
    ///
    /// 空串护栏与 <see cref="MatchesHarnessCommand"/> 同源、同样关键：
    /// <c>commandLine.Contains("")</c> 恒为 true，engineDir 一旦为空，
    /// **每一个进程**都会在此被判成"我们的引擎"、在关窗时被整树杀掉。
    /// WMI 取值处已把 null 兜成空串，所以这些是契约护栏而非现实风险——
    /// 但它护的正是代价最高的那条分支。
    /// </summary>
    internal static bool MatchesEngineProcess(string name, string commandLine, string engineDir)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(commandLine)) return false;
        if (string.IsNullOrEmpty(engineDir)) return false;
        if (string.Equals(name, "DeepSeekHarness.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (commandLine.Contains("app.asar", StringComparison.OrdinalIgnoreCase)) return false;
        if (commandLine.Contains("dsh-desktop-host", StringComparison.OrdinalIgnoreCase)) return false;
        // 双重核对：进程形态（node/cmd/npx）+ 命令行里带引擎内的 dsh 包目录。
        // 此前只认"命令行含引擎目录"的目录子串——编辑器以引擎目录下的文件为参数
        // 时同样命中，关窗清扫会把它整树带走。
        if (!IsEngineProcessShape(name)) return false;
        return commandLine.Contains(EnginePackageDirUnder(engineDir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 全进程快照（含命令行）。WMI 带 CommandLine 的全量查询在本机实测约 140 ms，
    /// 而一次启动里 StopHarnessProcessesAsync 与 ClearOrphanProfileLock 会各要一份；
    /// 缓存 1 秒即可让两者共用同一次查询，又不至于让快照过期到影响"找出残留进程"的准确性。
    /// 缓存字段**刻意不加锁**：并发下最坏是多跑一次 140 ms 查询、或短暂读到旧快照——
    /// 两者都不改变决策正确性（Kill 前还有 StartTime 容差兜底），而加锁反倒会把
    /// WMI 查询挂进另一条线程的等待路径。
    /// </summary>
    private static Dictionary<int, ProcessRecord>? processRecordCache;
    // 缓存时刻存成 **UTC ticks（long）** 而不是 DateTime。理由与本文件对 lastInstallInfoAtTicks
    // 的处理完全一致：DateTime 是 8 字节结构，跨线程读写**不保证原子**，撕裂读会得到一个
    // 既不是旧值也不是新值的时刻；Volatile.Read/Write 又只接受引用类型。两头都够不着时，
    // 唯一的正确做法就是拆成 long 用 Interlocked —— 这里照那条已确立的纪律执行。
    private static long processRecordCacheAtTicks;
    /// <summary>
    /// engine.migrating 定时归档的节流器：RefreshStatusAsync 每 1.5 秒跑一次，
    /// 这里挂一个每小时一次的 check，确保长期只复用不重启的用户也不会让
    /// engine.migrating 永久占着约 214 MB。
    /// </summary>
    private static DateTime lastMigrateAttemptAt = DateTime.MinValue;
    private static readonly TimeSpan MigrateThrottle = TimeSpan.FromHours(1);

    private static Dictionary<int, ProcessRecord> GetProcessRecords()
    {
        var now = DateTime.UtcNow;
        // 缓存读取必须跨线程安全：关窗清扫在 UI 线程、停止路径与孤儿锁清理在后台线程。
        // 字典是引用类型走 Volatile；时刻是拆开的 long 走 Interlocked（见字段注释）。
        var cached = Volatile.Read(ref processRecordCache);
        var cachedAt = new DateTime(Interlocked.Read(ref processRecordCacheAtTicks), DateTimeKind.Utc);
        if (cached is not null && cachedAt != DateTime.MinValue &&
            (now - cachedAt) < TimeSpan.FromSeconds(1))
            return cached;

        var result = new Dictionary<int, ProcessRecord>();
        var completed = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process");
            // Collection 与每个 ManagementObject 都持有 COM 对象（IWbemClassObject），
            // 只 Dispose searcher 的话它们全靠终结器排队——启动/停止/清扫/锁清理
            // 叠加 1 秒缓存未命中时短时间可积压数百个。与"Process 句柄用完即收"
            // 同一条纪律。
            using var results = searcher.Get();
            // 逐条 try：WMI 提供程序半途故障、或个别对象的某个属性读不出来时，
            // 坏的那一条**不该让整份快照作废**——那等于把一台本来还能枚举 400/500
            // 个进程的机器降级成"什么都没有"。代价是这一份**不完整**，
            // 所以 completed 保持 false、下面不发布缓存（见下）。
            foreach (var item in results.Cast<ManagementObject>())
            {
                using (item)
                {
                    try
                    {
                        var id = Convert.ToInt32(item["ProcessId"]);
                        var parent = Convert.ToInt32(item["ParentProcessId"]);
                        // PID 复用防护：StartTime 用于在 Kill 前比对——PID 被系统复用时 StartTime 必然不同。
                        // WMI 的 CreationDate 是 FILETIME（UTC），转成 DateTime 供后续比较。
                        var startTime = item["CreationDate"] is string creationStr
                            ? ManagementDateTimeConverter.ToDateTime(creationStr)
                            : DateTime.MinValue;
                        result[id] = new ProcessRecord(id, parent, item["Name"] as string ?? string.Empty, item["CommandLine"] as string ?? string.Empty, startTime);
                    }
                    catch (Exception ex) { Swallow.Quiet(ex, "process-record-row"); }
                }
            }
            completed = true;
        }
        catch (Exception ex)
        {
            // 整份快照拿不到：留痕，而不是把一个空/半截字典静悄悄交给下游。
            Swallow.Quiet(ex, "process-records");
        }

        // **部分结果绝不发布为缓存**。下游把这份快照当"全量进程表"用：
        // 停止/清扫会漏掉没被枚举到的残留（静默漏杀、无痕），孤儿锁清理会把
        // "没看到持锁者"读成"没人持锁"。而空快照本来就有守卫（跳过并留痕），
        // **半截快照此前却绕过了它**——Count != 0。
        // 不缓存的代价只是下一次调用重跑一次 WMI（本机约 140ms）；
        // 缓存一份残缺快照的代价是"漏杀 + 误删锁"，两者不可比。
        if (!completed) return result;
        Volatile.Write(ref processRecordCache, result);
        Interlocked.Exchange(ref processRecordCacheAtTicks, now.Ticks);
        return result;
    }

    /// <summary>
    /// 让进程快照缓存立即过期。**任何一处杀掉进程之后都必须调用**：
    /// 缓存的用途是省掉同一次启动里的重复 WMI 查询，但"刚杀完再查"恰恰需要新快照。
    /// </summary>
    private static void InvalidateProcessRecordCache()
    {
        Volatile.Write(ref processRecordCache, null);
        Interlocked.Exchange(ref processRecordCacheAtTicks, 0L);
    }

    private static bool IsHarnessCommand(ProcessRecord p) =>
        MatchesHarnessCommand(p.Name, p.CommandLine, engineDir, userHomeDir, DefaultPort);

    /// <summary>
    /// <see cref="IsHarnessCommand"/> 的纯函数内核：吃参数、不读任何静态状态。
    /// 单独拆出来只有一个理由——"杀错进程"是本项目最贵的一个判断：会崩掉用户
    /// 正在用的桌面客户端、会误杀别的登录会话的引擎，而它错了不会报错。
    /// 这类逻辑必须有单测钉住，不能只靠注释（见 tests/DeepSeekHarness.Tests）。
    /// 改动时保持与调用点行为完全一致，别顺手"优化"匹配规则。
    /// </summary>
    internal static bool MatchesHarnessCommand(
        string name, string commandLine, string engineDir, string userHomeDir, int port)
    {
        // 静态形式而不是 name.Equals(...)/c.Contains(...)：本函数要按契约接受任意输入，
        // 之前它只在 WMI 取值处被保证非空，于是测试传 null 就直接 NRE——
        // 纯函数连"脏输入不会炸"都做不到，就更谈不上被钉住。
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(commandLine)) return false;

        // 空 engineDir 是灾难性的输入：c.Contains("") 恒为 true，于是**任何进程**都会被
        // 当成"本启动器装的引擎"而整树杀掉。与其指望调用方永远传对，不如在这里挡住。
        // 生产上 Path.Combine 几乎不可能给出空串（GetFolderPath 返回空时得到的是相对
        // 路径 "DeepSeekHarness\engine"），所以这条是**契约护栏**而非现实风险——
        // 但它保护的是"判错即整树杀进程"这种代价最高的分支，留着不亏。
        if (string.IsNullOrEmpty(engineDir)) return false;

        if (string.Equals(name, "DeepSeekHarness.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var c = commandLine;

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

        // 进程形态收窄：引擎及其包装层只可能是 node / cmd / npx（两条杀进程路径
        // 共用同一判定）。编辑器、资源管理器等任何其他进程名直接出局——无论命令行
        // 长什么样。此前这道收窄只挡在宽松匹配前面，"引擎目录精确匹配"对任意进程名
        // 生效：一个以引擎目录下文件为参数的无关进程（编辑器打开了 bin.js）会被
        // 整树杀掉——匹配必须同时核对进程类型与实际引擎入口，不能仅凭目录子串。
        if (!IsEngineProcessShape(name)) return false;

        // 本启动器自己拉起的引擎：命令行里带着**引擎内的 dsh 包目录**——比"引擎
        // 目录"更精确一档的锚点（目录本身会被任何"以该目录下文件为参数"的进程
        // 撞上，见 IsEngineProcessShape 处的注释）。与退出清扫同一口径。
        if (c.Contains(EnginePackageDirUnder(engineDir), StringComparison.OrdinalIgnoreCase)) return true;

        // 宽松匹配只对 node / npx / cmd 生效，进程形态已由 IsEngineProcessShape
        // 统一收窄（此前这里各算一份 isNode/isNpx，两边迟早漂移）。

        // 宽松匹配只认**本用户**的残留：另一个登录会话里的引擎 / npx 缓存命令行
        // 同样含 @deepseek-ai/dsh，不加这道限定会把别人会话的进程整树杀掉
        // （互斥体是 Local\ 每会话一个，管不到别的会话）。一切用户态路径
        // （LOCALAPPDATA、.dsh、npm 全局目录）都在 %USERPROFILE% 之下。
        // 误杀别人进程的代价远大于漏杀一个残留——真残留占着端口有启动前报错兜底。
        //
        // **这道收窄必须待在这里、不能提到函数开头**。上面那条引擎包目录精确匹配
        // 不需要 userHomeDir：命令行里带着本启动器的引擎内 dsh 包目录，本身就是
        // 确定性的证据。
        // 要是把"userHomeDir 为空就返回 false"提到最前面，在 USERPROFILE 缺失 /
        // 用户配置文件 hive 未加载 / 受限容器这类机器上（本项目自己的文档就说
        // GetFolderPath 无法确定时返回空串），就变成**连自己的引擎都杀不掉**：
        // 残留引擎占住端口、孤儿 node_modules.lock 永远清不掉。
        //
        // 空串这里也必须拒：原来的写法是 `userHomeDir.Length > 0 && !c.Contains(...)`，
        // 空串时这道收窄被**跳过**，恰好把"归属不清就不动手"的既定语义反转成
        // "放行所有用户"——那正是本函数最不能犯的错。
        //
        // 段级比较而不是子串：c.Contains(userHomeDir) 只判"用户目录这段文字出现过"。
        // C:\Users\Dan 是 C:\Users\Daniel 的**前缀**，Contains 照样通过——同机同时存在
        // Daniel 与 Dan 两个账户时，Daniel 会话的引擎/npx 残留会被判成本启动器的残留，
        // 而这一分支连端口都不用匹配（见上），于是直接整树杀掉。以管理员运行时
        // （UAC 提升后 USERPROFILE 可能指向别的账户）更糟：一整棵别的用户的进程树被清。
        // ClearOrphanProfileLock 借用的也是这个判定，于是同样会永久拒绝对**活锁**动手。
        // 拼音用户名前缀极常见（li / liwei、zhang / zhangsan），这不是边缘情形。
        if (string.IsNullOrEmpty(userHomeDir) ||
            !ContainsPathSegment(c, userHomeDir)) return false;

        if (c.Contains("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("@deepseek-ai\\dsh", StringComparison.OrdinalIgnoreCase)) return true;

        // 两条宽松正则只为兜早期 npx / dsh.cmd 时代留下的残留。再收窄一道：
        // 命令行里必须出现**恰好等于**本启动器端口的独立数字 token，否则一个碰巧
        // 提到 "dsh" 的 node/cmd 进程也会被整树杀掉——误杀别人进程的代价远大于漏杀
        // 一个残留（真残留占着端口时，启动前的端口探测会给出明确报错兜住）。
        //
        // 这里必须是 token 级匹配而不是子串匹配：子串写法（c.Contains("3080")）会把
        // "--port 30801" 也算命中——用户在 %USERPROFILE% 下自己装一份 dsh 跑在 30801
        // 是很正常的形态（路径无空格 → 命令行不加引号 → 宽泛正则照样命中），
        // 结果就是点一次「停止」把用户自己的进程整树杀掉。这是真实的误杀面。
        if (!MentionsLauncherPort(c, port)) return false;

        return DshCommandRegex.IsMatch(c) || NpxDshCommandRegex.IsMatch(c);
    }

    /// <summary>
    /// 命令行里是否出现了一个**恰好等于** <paramref name="port"/> 的独立数字 token。
    /// 抽成具名函数是因为"端口收窄"是这个判断里最容易改坏的一环：
    /// 它必须认得 `--port 3080`、`--port=3080` 与裸 `3080`，但**不能**把 `30801`、
    /// `0.3080`、或版本号 `0.1.5` 里的数字误当成端口。
    ///
    /// 注意这条只**收紧不放宽**：新写法命中的集合是旧子串写法的子集——
    /// 端口作为独立 token 出现的命令行两边都命中，只有"端口仅以更长数字的一部分
    /// 出现"（`--port 30801`）才被新写法放过。而那恰恰是旧写法会误杀的那种。
    /// </summary>
    internal static bool MentionsLauncherPort(string commandLine, int port)
    {
        // 它被单测直接调用、也是 internal 表面，按上面那条同样的纪律自己挡脏输入。
        if (string.IsNullOrEmpty(commandLine)) return false;
        var want = port.ToString(CultureInfo.InvariantCulture);
        foreach (Match m in PortTokenRegex.Matches(commandLine))
            if (string.Equals(m.Value, want, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 命令行里是否出现了 <paramref name="path"/> 这个**完整路径段**（纯函数、可单测）。
    ///
    /// 为什么不能用 Contains：<c>C:\Users\Dan</c> 是 <c>C:\Users\Daniel</c> 的前缀。
    /// 子串写法在同机存在两个相近账户名时必然误判——而误判的方向是
    /// "把别的用户会话的引擎判成本启动器的残留并整树杀掉"，这条分支甚至不要求
    /// 端口匹配。以管理员身份运行时更糟（USERPROFILE 可能指向别的账户）。
    /// 拼音用户名前缀（li / liwei、zhang / zhangsan）非常常见，不是边缘情形。
    ///
    /// 判据：命中位置的**后面**必须是分隔符、引号、空白或字符串末尾——
    /// 这才是能区分 Dan 与 Daniel 的那道边界；前面同样要求一个边界字符，
    /// 以免把更长路径里的中段当成起点。
    ///
    /// 失手方向是安全的：`\\?\C:\Users\Daniel\...` 这类长路径前缀会让"前面有边界"
    /// 不成立，于是本会话自己的残留可能不被认出——漏杀有启动前的端口探测兜底，
    /// 误杀没有兜底。
    /// </summary>
    internal static bool ContainsPathSegment(string text, string path)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(path)) return false;
        // 尾部分隔符会让"后面必须有边界"这道判据失真（后面本来就该是分隔符）。
        var needle = path.TrimEnd('\\', '/');
        if (needle.Length == 0) return false;
        for (var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = i + needle.Length;
            if ((i == 0 || IsPathSegmentBoundary(text[i - 1])) &&
                (after == text.Length || IsPathSegmentBoundary(text[after])))
                return true;
        }
        return false;
    }

    /// <summary>路径段边界字符：分隔符、引号、空白、以及命令行里常见的赋值/括号。</summary>
    private static bool IsPathSegmentBoundary(char ch) =>
        ch == '\\' || ch == '/' || ch == '"' || ch == '\'' ||
        char.IsWhiteSpace(ch) || ch == '=' || ch == ';' || ch == ',' ||
        ch == '(' || ch == ')';

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
            // USERPROFILE 解析为空（受限账户/组策略）时，Path.Combine 出来的是
            // **相对路径** ".dsh\profiles\node_modules.lock"——后面的 File.Exists 按
            // 当前工作目录解析，可能命中启动器 exe 目录下的同名文件并把它删掉。
            // 与 LocalAppDir（解析不到直接 throw）、ConfigBackup.CreateSnapshot 的
            // 同款护栏必须是同一口径：这里删的是别人的锁，删错不可逆。
            var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(profileRoot))
            {
                AppendStartupLog("USERPROFILE 解析为空或不是完整路径，跳过孤儿锁清理（避免按相对路径删文件）。");
                return;
            }
            var lockPath = Path.Combine(profileRoot, ".dsh", "profiles", "node_modules.lock");
            // 绝大多数启动这里根本没有锁文件：先判存在再决定要不要付出全量进程扫描的代价。
            if (!File.Exists(lockPath)) return;
            var records = GetProcessRecords();
            // 快照为空（WMI 查询失败/被拦截）时**不能**把 Any()==false 当"没有残留进程"：
            // 那会删掉一个可能仍被活着的引擎持有的锁——删锁正是本方法最不能犯的错
            // （锁没了，并发写者就能同时进场）。宁可跳过：锁若真是孤儿，下次启动
            // WMI 正常时仍会清掉；锁若被持有，跳过恰好避免一次真实的踩踏。
            // 杀进程两条路径对空快照早已按异常处理并留痕（StopHarnessProcessesCore），
            // 这里此前是唯一把空快照当"一切正常"的调用方。
            if (records.Count == 0)
            {
                AppendStartupLog("进程快照为空（WMI 查询失败或被拦截），跳过孤儿锁清理，避免误删仍被持有的锁。");
                return;
            }
            // 判据必须是 MayHoldProfileLock 而**不是** IsHarnessCommand：后者为了"不误杀"
            // 明确排除了桌面客户端，而桌面客户端的引擎**同样**持这把锁。借它判活锁会被删，
            // 两个引擎于是并发写同一份 profile——正是这把锁存在的理由（见 MayHoldProfileLock）。
            if (records.Values.Any(p => MayHoldProfileLock(p.Name, p.CommandLine, userHomeDir))) return;
            File.Delete(lockPath);
        }
        catch (Exception ex) { Swallow.Quiet(ex, "clear-orphan-lock"); }
    }

    private static async Task WaitForPortToCloseAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await IsPortListeningAsync(port, ct)) return;
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 端口是否已被监听（异步版）。此前用 GetActiveTcpListeners()：它在启动等待循环里
    /// 每 250 ms 被调用一次，每次都分配并枚举整张 TCP 表（本机实测 22 ms / 29 个端点，
    /// 进程一多就更贵）。改成对回环地址做一次定向连接探测，成本低一个数量级。
    /// 副作用与旧实现一致：只关心确有进程在该端口 accept。
    ///
    /// 只保留异步版：同步版（Task.WaitAny 阻塞 200ms）没有调用点后删掉了——
    /// 轮询路径每 250 ms 调一次，同步版会把 UI 线程卡掉近一半时间
    /// （回环端口被防火墙 DROP 的机器上每次都等满）。
    /// </summary>
    private static Task<bool> IsPortListeningAsync(int port, CancellationToken ct = default)
        => IsTcpOpenAsync("127.0.0.1", port, 200, ct);

    /// <summary>
    /// Windows 在 Hyper-V / WSL / Docker Desktop 启用后会预留一大段动态端口范围
    /// （典型 49152–65535）。出网连接会随机占用这段里的端口，同时保持 4 分钟
    /// TIME_WAIT，于是"端口莫名被占"且 netstat 里找不到可疑程序。
    /// 报错信息里点出这一层，能省掉大量排查时间。
    /// </summary>
    private static async Task<string?> DescribeDynamicPortRangeAsync(int port)
    {
        var range = await GetDynamicPortRangeAsync();
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
    private static DateTime dynamicPortRangeLastAttempt = DateTime.MinValue;
    private static readonly SemaphoreSlim dynamicPortRangeGate = new(1, 1);

    private static async Task<(int Start, int Count)?> GetDynamicPortRangeAsync()
    {
        // 失败 60 秒后重试：netsh 偶发失败（权限/服务未就绪）不该让之后所有端口
        // 报错都永久缺提示——与 localProxyOpen 的"失败 60 秒后重探"同一策略。
        if (dynamicPortRangeRead && dynamicPortRange is not null) return dynamicPortRange;
        if (dynamicPortRangeRead && dynamicPortRange is null &&
            DateTime.UtcNow - dynamicPortRangeLastAttempt < TimeSpan.FromSeconds(60))
            return null;
        await dynamicPortRangeGate.WaitAsync();
        try
        {
            // 双检：等锁期间可能已有人查过了
            if (dynamicPortRangeRead && dynamicPortRange is not null) return dynamicPortRange;
            if (dynamicPortRangeRead && dynamicPortRange is null &&
                DateTime.UtcNow - dynamicPortRangeLastAttempt < TimeSpan.FromSeconds(60))
                return null;
            dynamicPortRangeRead = true;
            dynamicPortRangeLastAttempt = DateTime.UtcNow;
            var text = await RunCmdAsync(
                "netsh int ipv4 show dynamicport tcp", TimeSpan.FromSeconds(2));
            if (text is null) return null;
            // 中文 Windows 的 netsh 输出的是「启动端口」，不是「起始端口」——后者是
            // 几版文档/译文的写法，两者都不是（实测本机两种代码页下 netsh 都直接
            // 输出英文 "Start Port"）。两个都收：反正匹配到哪个是哪个，
            // 而只写一个的话真实的那台机器上这条提示永远不会出现。
            var startMatch = Regex.Match(text, @"(?:起始|启动)端口\s*:\s*(\d+)|Start Port\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            var countMatch = Regex.Match(text, @"端口数\s*:\s*(\d+)|Number of Ports\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (!startMatch.Success || !countMatch.Success) return null;
            // TryParse 而非 Parse：原来靠外层 catch 吞掉 FormatException，
            // netsh 一旦输出异常大的数字就会走成"整个方法返回 null"。
            if (!int.TryParse(startMatch.Groups[1].Success ? startMatch.Groups[1].Value : startMatch.Groups[2].Value,
                              out var start)) return null;
            if (!int.TryParse(countMatch.Groups[1].Success ? countMatch.Groups[1].Value : countMatch.Groups[2].Value,
                              out var count)) return null;
            dynamicPortRange = (start, count);
            return dynamicPortRange;
        }
        finally { dynamicPortRangeGate.Release(); }
    }

    /// <summary>
    /// 跑一条命令并取回标准输出，全程异步。超时、启动失败、空输出都返回 null。
    /// innerCommand 是不含 cmd 前缀的命令本体；外层统一用
    /// <c>chcp 65001</c> 把控制台代码页切到 UTF-8 再执行——netsh 等命令在中文
    /// Windows 上默认输出 GBK，而这里按 UTF-8 解码，不切页时中文标签会变乱码，
    /// 靠中文正则匹配的解析分支就永远命不中。
    ///
    /// ⚠ 但实测这台机器上 chcp 65001 之后 netsh **直接输出英文** "Start Port"，
    /// 中文分支其实一次都没命中过——本条提示一直是靠英文分支救活的。
    /// 所以调这个命令时不要以为"切页就能命中中文"，两处分支都留着才对。
    ///
    /// 为什么不能写成 <c>ReadToEnd() + WaitForExit(ms)</c>：ReadToEnd 会一直阻塞到
    /// 子进程关闭 stdout 才返回，而它**排在 WaitForExit 前面**——子进程一旦卡住，
    /// 超时判断根本没机会执行，调用它的 UI 线程就被挂住任意长时间。
    /// 而 WaitForExitAsync(token) 在超时/取消时同样是**抛 OperationCanceledException**
    /// 而不是正常返回，所以这里必须显式 catch 并杀掉子进程。
    /// </summary>
    private static async Task<string?> RunCmdAsync(string innerCommand, TimeSpan timeout,
                                                     CancellationToken ct = default)
    {
        Process? proc = null;
        Task<string>? outTask = null;
        Task<string>? errTask = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"chcp 65001 >nul & " + innerCommand + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            // 取消击杀必须挂在**同步执行的取消回调**上（Register 的回调在 Cancel()/
            // CancelAfter 的触发线程上直接跑），不能只依赖下面 OCE catch 里的那份：
            // 关窗时 CancelPendingStart 在 WM_CLOSE 分发期间同步 Cancel()，而 catch
            // 是一条 Post 给 WinForms 上下文的续延——泵在 WM_CLOSE 分发期间不切换，
            // 关窗后这条续延"可能被分发也可能被丢"，被丢时 cmd 及其子进程就成了
            // 无人看管的孤儿（孤儿 npm 继续写 engine.tmp，下次升级删不掉临时目录）。
            // catch 里那份 Kill 保留：它兜注册之前的取消与超时路径，双保险不冲突。
            using var killOnCancel = timeoutCts.Token.Register(() =>
            {
                try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            // 不给 ReadToEndAsync 传 token：一旦被取消它就不会再读完剩余数据，
            // 而杀掉子进程后管道自然会关闭、读取会干净地结束。
            outTask = proc.StandardOutput.ReadToEndAsync();
            errTask = proc.StandardError.ReadToEndAsync();

            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                // 读取任务必须有界收尾（见下），裸 await 在孙进程继承句柄时会永挂。
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }

            // 进程退出了，管道**不一定**读完：孙进程若继承了 stdout 句柄且自己不退出，
            // ReadToEndAsync 会一直挂着——而启动/升级路径对本方法没有外层超时，
            // 一旦挂住界面就永久 busy。输出读不到是可接受的降级（各调用方都有回落），
            // 与 GetToolVersionAsync/DrainQuietlyAsync 同一条纪律。
            var drained = await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(DrainTimeoutMs));
            if (drained is not Task<string[]> done)
            {
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }
            string text;
            try { text = done.Result[0]; }
            catch { return null; }
            // stderr 已由上面一并排干：管道写满会让子进程自己卡死（内容不用）。
            return text;
        }
        catch
        {
            // 其余异常（启动失败等）：同样可能留下孤儿进程与挂着的读取任务。
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            await DrainQuietlyAsync(outTask, errTask);
            return null;
        }
        finally { try { proc?.Dispose(); } catch { } }
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
            // 取消就抛 OCE，别再返回 candidates[0]——那可能是一个明知不达标的旧版，
            // 调用点本来就各自紧跟 cancel 判定/捕获 OCE，"取消"与"解析出了一个 node"
            // 必须是两件事。此前谎返回值只因为两个调用方恰好都不会用它。
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
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
        if (found is not null) return GuardToolPath(found, "npm.cmd");
        throw new FileNotFoundException(
            "未找到 npm.cmd。npm 随 Node.js 一起安装，请重装 Node.js LTS（18 或更高）。", "npm.cmd");
    }

    private static string? ResolvePnpmPath(string? nodePath = null) =>
        GuardToolPath(
            (nodePath is not null ? FindToolBeside(nodePath, "pnpm.cmd") : null) ?? FindTool("pnpm.cmd"),
            "pnpm.cmd");

    /// <summary>corepack 是随 Node 附带的包管理 shim（Node ≥ 16.9），没有真实 pnpm 时退到它。</summary>
    private static string? ResolveCorepackPath(string? nodePath = null) =>
        GuardToolPath(
            (nodePath is not null ? FindToolBeside(nodePath, "corepack.cmd") : null) ?? FindTool("corepack.cmd"),
            "corepack.cmd");

    /// <summary>
    /// 工具路径的纵深防御护栏（纯函数、可单测）：能被拼进 cmd 命令行的路径必须
    /// 既不含 <c>%</c> 也不含引号。
    ///
    /// <c>%</c> 是真正危险的那个：cmd 会对命令行做 <c>%VAR%</c> 展开，而
    /// PATH 里出现**未展开**的 <c>%FOO%</c>（配置写错的机器上不罕见）在
    /// <c>File.Exists</c> 判定下会直接被跳过，可一旦它出现在被引用起来的位置上，
    /// 展开后的 cmd 就去执行了一个与我们意图完全不同的路径——表现为"node 明明装了
    /// 却找不到"，且报错完全指不到真正的原因。
    ///
    /// 与 <see cref="IsSafeNpmValue"/> 同一条纪律（那是给用户可改的 registry 用的），
    /// 区别只在于这里**允许**空白：<c>C:\Program Files\nodejs\npm.cmd</c> 是常态，
    /// 调用点本来就把它整段加了引号。
    /// </summary>
    internal static bool IsSafeToolPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        path!.IndexOf('%') < 0 &&
        path.IndexOf('"') < 0;

    /// <summary>不满足 <see cref="IsSafeToolPath"/> 就给出能照做的报错，而不是让 cmd 去猜。</summary>
    private static string GuardToolPath(string? path, string toolName)
    {
        if (path is null) return null!;
        if (IsSafeToolPath(path)) return path;
        throw new InvalidOperationException(
            $"{toolName} 的路径里含有 cmd 会展开的字符（% 或引号），已中止：\n{path}\n" +
            "这类路径通常来自 PATH 里未展开的 %变量%。请把 %变量% 改成实际路径后重试。");
    }
}
