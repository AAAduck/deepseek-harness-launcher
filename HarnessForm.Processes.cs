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
        //
        // 退役 + 清字段 + 删文件三步必须与 tail 线程的写入（HandleProcessLine）
        // 同持 authUrlGate：否则"tail 通过代际核对后被 OS 挂起几秒，恢复后把刚删掉
        // 的 web-url.txt 重写成过期 token"的窄缝始终敞着。锁内重新核对令牌后，
        // 要么 tail 先写完（这里的清理随后覆盖它，终态正确），要么退役先发生
        //（tail 的锁内核对失败，不再写）——check-then-act 的缝就此焊死。
        lock (authUrlGate)
        {
            RetireEngineTail();
            // 引擎进程没了，上一份认证链接就是过期 token。不在这里清空的话，
            // 「重启」/「升级后重启」的等待循环会在下一个引擎还没输出任何日志时
            // 因 authenticatedUrl != null 立刻"成功返回"，浏览器先弹出过期 token 的失败页。
            authenticatedUrl = null;
            TryDeleteUrlFile();
        }
        // 刚杀完必须让进程快照缓存作废：否则紧接着的 ClearOrphanProfileLock
        // 拿到的是"杀之前"的缓存（1 秒 TTL，而端口通常几百毫秒内就关、等不到过期），
        // 把已死进程当成活残留，拒绝清孤儿锁——恰好复现这个功能本来要防的启动失败。
        InvalidateProcessRecordCache();
    }

    private void StopHarnessProcessesCore()
    {
        var records = GetProcessRecords();
        // GetProcessRecords 拿不到或**残缺**时返回 null——不是抛异常。半截快照做
        // 定向清扫只会静默漏杀，不如只用句柄那条必中的路；空/残缺都要留痕
        // （"界面说停了、引擎其实还在 3080 上活着"正是无痕迹时查不到的故障）。
        if (records is null)
            AppendStartupLog("进程快照残缺或不可用（WMI 中途故障）：本轮只结束本启动器自己拉起的引擎进程树。");
        else if (records.Count == 0)
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

        var snapshot = records ?? new Dictionary<int, ProcessRecord>();
        var seeds = snapshot.Values.Where(IsHarnessCommand).Select(x => x.Id).ToHashSet();
        // dshProcess 只在上面读一次（取不在这里用：句柄那条路已经直接杀过了）。
        var all = ProcessMatch.CollectProcessTreeIds(seeds, snapshot);

        foreach (var id in all.OrderByDescending(x => x))
        {
            // Process 对象持有内核句柄，用完即收、不等 GC。
            // PID 复用防护：GetProcessById 之后带容差比对 StartTime（见
            // ProcessMatch.IsSameProcessStart）。宁可漏掉一个残留，也不能误杀
            // 同 PID 的新进程。
            if (!snapshot.TryGetValue(id, out var expected)) continue;
            // WMI 没给出 CreationDate 时回退成 DateTime.MinValue，于是
            // ProcessMatch.IsSameProcessStart 必然判不等 → 这个进程**永远杀不掉、且无任何痕迹**。
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
                if (!ProcessMatch.IsSameProcessStart(victim.StartTime, expected.StartTime))
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

    // CollectProcessTreeIds 已迁到 ProcessMatch（纯函数内核；树收集的防伪规则见那里）。

    /// <summary>
    /// 关窗退出时结束引擎。与「停止」按钮的区别：这里必须**快**，因为它在窗体的
    /// 关闭路径上，阻塞会让窗口卡住不消失。
    /// 先直接杀掉已知的进程树（不查 WMI）；WMI 定向清扫**只在复用路径**上补一次——
    /// 复用的引擎是上一个（可能已崩溃的）实例拉起的，dshProcess 为 null，
    /// 这次扫描是关窗时结束它的唯一手段。本实例自己拉起的引擎树已被句柄整树
    /// 击杀（句柄即同一性证明），再扫一遍只是重复回答已知的事：白白把 ~140 ms
    /// 的 WMI 全量查询压进关窗的 UI 线程（WMI 受损的机器上可到数秒）。
    /// 更陌生的残留（别处启动、连复用探针都没对上的）宁可漏掉——下次启动的
    /// 端口预检会给出明确报错兜住（见 ProcessMatch.MatchesEngineProcess 的注释）。
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
        //
        // 退役必须与停止路径（StopHarnessProcessesAsync）同持 authUrlGate：tail 线程
        // 可能正持锁在"锁内核对令牌通过之后、写文件完成之前"——此时裸调
        // Interlocked.Increment 与它并发，写侧锁内的核对看到的仍是旧令牌，
        // 关窗后盘上就多了一份过期 token 的 web-url.txt。锁内退役后，写侧要么在
        // 退役前完成（终态是文件被写回，但进程随即退出、下次启动有探针验证兜底），
        // 要么核对失败不再写——两条杀进程路径的纪律就此一致。
        lock (authUrlGate)
        {
            RetireEngineTail();
        }

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
            var records = GetProcessRecords();
            if (records is null)
            {
                // 残缺快照不进清扫：枚举半途失败时"没看到"不等于"不在"，
                // 与「停止」对空/残缺快照的留痕降级同一口径。
                Swallow.Quiet(
                    new InvalidOperationException("进程快照残缺（WMI 中途故障），关窗清扫跳过定向扫描"),
                    "kill-engine-sweep-incomplete");
                return;
            }
            foreach (var record in records.Values.Where(IsEngineProcess))
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
                    if (!ProcessMatch.IsSameProcessStart(victim.StartTime, record.StartTime)) continue;
                    victim.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { Swallow.Quiet(ex, "kill-process"); }
            }
        }
        catch (Exception ex) { Swallow.Quiet(ex, "kill-engine-sweep"); }
    }

    // IsSameProcessStart / IsEngineProcessShape / IsElectronProcessShape /
    // MayHoldProfileLock / EnginePackageDirUnder / MatchesEngineProcess
    // 已迁到 ProcessMatch（两条杀进程路径共用的判定内核与进程形态收窄，见该类头部说明）。

    /// <summary>
    /// 只认「命令行里带本启动器引擎目录」的进程，用于退出清扫。
    /// 与 IsHarnessCommand 同样必须先排除桌面客户端：它的引擎宿主命令行里
    /// 也含 @deepseek-ai/dsh，但那是客户端自己的，不是我们启动的。
    /// </summary>
    private bool IsEngineProcess(ProcessRecord p) =>
        ProcessMatch.MatchesEngineProcess(p.Name, p.CommandLine, engineDir);

    /// <summary>
    /// 全进程快照（含命令行）。WMI 带 CommandLine 的全量查询在本机实测约 140 ms，
    /// 而一次启动里 StopHarnessProcessesAsync 与 ClearOrphanProfileLock 会各要一份；
    /// 缓存 1 秒即可让两者共用同一次查询，又不至于让快照过期到影响"找出残留进程"的准确性。
    /// 缓存字段**刻意不加锁**：并发下最坏是多跑一次 140 ms 查询、或短暂读到旧快照——
    /// 两者都不改变决策正确性（Kill 前还有 StartTime 容差兜底），而加锁反倒会把
    /// WMI 查询挂进另一条线程的等待路径。
    /// </summary>
    private static Dictionary<int, ProcessRecord>? processRecordCache;
    // 缓存时刻存成 UTC ticks（long）走 Interlocked：8 字节 DateTime 跨线程裸读不保证
    // 原子，Volatile.Read/Write 又只接受引用类型——本文件对 8 字节跨线程状态的统一纪律。
    private static long processRecordCacheAtTicks;
    /// <summary>
    /// engine.migrating 定时归档的节流器：RefreshStatusAsync 每 1.5 秒跑一次，
    /// 这里挂一个每小时一次的 check，确保长期只复用不重启的用户也不会让
    /// engine.migrating 永久占着约 214 MB。
    /// </summary>
    private static DateTime lastMigrateAttemptAt = DateTime.MinValue;
    private static readonly TimeSpan MigrateThrottle = TimeSpan.FromHours(1);

    private static Dictionary<int, ProcessRecord>? GetProcessRecords()
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
        var complete = true;
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
            // 个进程的机器降级成"什么都没有"。代价是这一份**不完整**：
            // complete 置 false，整个快照按"拿不到"处理（不返回给下游、不发布缓存）。
            foreach (var item in results.Cast<ManagementObject>())
            {
                using (item)
                {
                    try
                    {
                        var id = Convert.ToInt32(item["ProcessId"]);
                        var parent = Convert.ToInt32(item["ParentProcessId"]);
                        // PID 复用防护：StartTime 用于在 Kill 前比对——PID 被系统复用时 StartTime 必然不同。
                        // ⚠ CreationDate **不是 UTC**：ManagementDateTimeConverter.ToDateTime
                        // 返回 Kind=Unspecified 的**本地挂钟时间**（DMTF 串尾部带 +480
                        // 这类本地偏移，本机实测样本 "20261002101841.683036+480"）。
                        // 另一侧 Process.StartTime 是 Kind=Local 的本地挂钟时间——
                        // 两边 Ticks 可比靠的是"都是本地挂钟"。
                        // 所以这里**绝对不能**补一句 ToUniversalTime()：那会把它推到
                        // 差 8 小时的远端，于是 ProcessMatch.IsSameProcessStart 对每一个真正的目标
                        // 都返回 false →「停止」静默空转。
                        var startTime = item["CreationDate"] is string creationStr
                            ? ManagementDateTimeConverter.ToDateTime(creationStr)
                            : DateTime.MinValue;
                        result[id] = new ProcessRecord(id, parent, item["Name"] as string ?? string.Empty, item["CommandLine"] as string ?? string.Empty, startTime);
                    }
                    catch (Exception ex) { Swallow.Quiet(ex, "process-record-row"); complete = false; }
                }
            }
        }
        catch (Exception ex)
        {
            // 整份快照拿不到：留痕，而不是把一个空/半截字典静悄悄交给下游。
            Swallow.Quiet(ex, "process-records");
            complete = false;
        }

        // **残缺的快照一律当"拿不到"处理：返回 null、不发布缓存**。下游把这份快照
        // 当"全量进程表"用：停止/清扫会漏掉没被枚举到的残留（静默漏杀、无痕），
        // 孤儿锁清理会把"没看到持锁者"读成"没人持锁"→ 删掉活锁——锁没了，
        // 并发写者就能同时进场，正是本文件最不能犯的错。此前只挡"空"快照，
        // 半截快照（Count != 0）两头都绕了过去；逐行丢行时甚至还会被发布进缓存，
        // 与上面的逐条 try 注释自相矛盾。返回 null（而不是把半截字典交出去）让
        // 调用方显式选择降级路径；不缓存的代价只是下一次调用重跑一次 WMI
        //（本机约 140ms），残缺快照的代价是"漏杀 + 误删锁"，两者不可比。
        if (!complete) return null;
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
        ProcessMatch.MatchesHarnessCommand(p.Name, p.CommandLine, engineDir, userHomeDir, DefaultPort);

    // MatchesHarnessCommand / MentionsLauncherPort / ContainsPathSegment / IsPathSegmentBoundary
    // 已迁到 ProcessMatch（纯函数内核与它的正则住在一起，见该类头部说明）；
    // 下面的 IsHarnessCommand 只是"补上 engineDir / userHomeDir / DefaultPort 三个
    // 本机静态值"的薄包装。

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
            // 快照为空或**残缺**时不能把"没看到持锁者"当"没人持锁"：删锁正是本方法
            // 最不能犯的错（锁没了，并发写者就能同时进场）。宁可跳过——孤儿锁下次
            // 启动 WMI 正常时仍会被清掉（半截快照事故见 DESIGN-NOTES.md §3）。
            if (records is null)
            {
                AppendStartupLog("进程快照残缺（WMI 中途故障），跳过孤儿锁清理，避免误删仍被持有的锁。");
                return;
            }
            if (records.Count == 0)
            {
                AppendStartupLog("进程快照为空（WMI 查询失败或被拦截），跳过孤儿锁清理，避免误删仍被持有的锁。");
                return;
            }
            // 判据必须是 MayHoldProfileLock 而**不是** IsHarnessCommand：后者为了"不误杀"
            // 明确排除了桌面客户端，而桌面客户端的引擎**同样**持这把锁。借它判活锁会被删，
            // 两个引擎于是并发写同一份 profile——正是这把锁存在的理由（见 MayHoldProfileLock）。
            if (records.Values.Any(p => ProcessMatch.MayHoldProfileLock(p.Name, p.CommandLine, userHomeDir))) return;
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

    /// <summary>
    /// 动态端口范围查询结果。**引用类型 + Volatile**：(int,int)? 是 12 字节结构，
    /// 跨线程裸读不保证原子——撕裂读会得到 HasValue=true 但 Start/Count 半新半旧
    /// 的值（后果仅是提示文案算错范围，但同文件 line 345 一带刚为 8 字节 DateTime
    /// 立了纪律，12 字节更不能裸奔）。比照 processRecordCache 的做法走
    /// "引用类型快照 + Volatile"那一支：唯一写入点在闸内，读侧免锁。
    /// </summary>
    private sealed record DynamicPortRangeSnapshot(int Start, int Count);

    private static DynamicPortRangeSnapshot? dynamicPortRange;
    // “已探测过”标志与“上次尝试时刻”跨线程读写（调用方分布在 UI 线程与后台线程）。
    // bool 可 volatile；8 字节时刻拆成 UTC ticks 走 Interlocked——与本文件
    // processRecordCacheAtTicks / lastInstallInfoAtTicks 已立的纪律同一形状，
    // 不裸奔 DateTime（撕裂读会得到一个既不是旧值也不是新值的时刻）。
    private static volatile bool dynamicPortRangeRead;
    private static long dynamicPortRangeLastAttemptTicks;
    private static readonly SemaphoreSlim dynamicPortRangeGate = new(1, 1);

    private static async Task<(int Start, int Count)?> GetDynamicPortRangeAsync()
    {
        // 失败 60 秒后重试：netsh 偶发失败（权限/服务未就绪）不该让之后所有端口
        // 报错都永久缺提示——与 localProxyOpen 的"失败 60 秒后重探"同一策略。
        var cached = Volatile.Read(ref dynamicPortRange);
        if (dynamicPortRangeRead && cached is not null) return (cached.Start, cached.Count);
        if (dynamicPortRangeRead && cached is null &&
            DateTime.UtcNow.Ticks - Interlocked.Read(ref dynamicPortRangeLastAttemptTicks) < TimeSpan.FromSeconds(60).Ticks)
            return null;
        await dynamicPortRangeGate.WaitAsync();
        try
        {
            // 双检：等锁期间可能已有人查过了
            cached = Volatile.Read(ref dynamicPortRange);
            if (dynamicPortRangeRead && cached is not null) return (cached.Start, cached.Count);
            if (dynamicPortRangeRead && cached is null &&
                DateTime.UtcNow.Ticks - Interlocked.Read(ref dynamicPortRangeLastAttemptTicks) < TimeSpan.FromSeconds(60).Ticks)
                return null;
            dynamicPortRangeRead = true;
            Interlocked.Exchange(ref dynamicPortRangeLastAttemptTicks, DateTime.UtcNow.Ticks);
            var text = await RunCmdAsync(
                "netsh int ipv4 show dynamicport tcp", TimeSpan.FromSeconds(2));
            if (text is null) return null;
            // netsh 的中文标签随版本/代码页在「启动端口」「起始端口」与英文
            // "Start Port" 之间漂——全部都收，只写一个的话真实机器上永远命不中。
            var startMatch = Regex.Match(text, @"(?:起始|启动)端口\s*:\s*(\d+)|Start Port\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            var countMatch = Regex.Match(text, @"端口数\s*:\s*(\d+)|Number of Ports\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (!startMatch.Success || !countMatch.Success) return null;
            // TryParse 而非 Parse：原来靠外层 catch 吞掉 FormatException，
            // netsh 一旦输出异常大的数字就会走成"整个方法返回 null"。
            if (!int.TryParse(startMatch.Groups[1].Success ? startMatch.Groups[1].Value : startMatch.Groups[2].Value,
                              out var start)) return null;
            if (!int.TryParse(countMatch.Groups[1].Success ? countMatch.Groups[1].Value : countMatch.Groups[2].Value,
                              out var count)) return null;
            var range = new DynamicPortRangeSnapshot(start, count);
            Volatile.Write(ref dynamicPortRange, range);
            return (range.Start, range.Count);
        }
        finally { dynamicPortRangeGate.Release(); }
    }

    /// <summary>
    /// 跑一条命令并取回标准输出，全程异步。超时、启动失败、空输出都返回 null。
    /// innerCommand 是不含 cmd 前缀的命令本体；外层先 <c>chcp 65001</c> 切 UTF-8
    /// （netsh 在中文 Windows 默认输出 GBK，不切页则中文解析分支永远命不中——
    /// 但别指望切页一定命中中文，英文分支同样要留）。不能写成
    /// <c>ReadToEnd() + WaitForExit(ms)</c>：ReadToEnd 阻塞到 stdout 关闭且排在
    /// 超时判断之前，子进程一卡 UI 线程就被无限挂住；WaitForExitAsync 在超时/取消时
    /// 是抛 OCE 而不是正常返回，必须显式 catch 并杀树。时间线见 DESIGN-NOTES.md §9。
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
            // 取消击杀必须挂在**同步执行的取消回调**上（Register 的回调在触发线程上
            // 直接跑）：关窗期间 Post 的 OCE 续延"可能被分发也可能被丢"，被丢时
            // cmd 及其子进程就是无人看管的孤儿。catch 里那份 Kill 兜注册前的路径，
            // 双保险不冲突。
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
        var found = FindAllTools("node.exe", 4);
        // 与 npm/pnpm/corepack 同一道护栏（见 CommandGuard.IsSafeToolPath）：node 的路径会被拼进
        // `cmd /d /s /c ""<node>" …"`，含 % 或引号的路径不该继续走。现实中 File.Exists
        // 不展开 %VAR%，这类候选几乎不可能出现在 FindAllTools 的结果里——这是契约
        // 护栏而非现实风险，但护栏必须落在"选谁"这一刻，而不是指望调用点记得。
        // 跳过要留痕：否则"装了 Node 却报未找到"没有任何线索。
        var candidates = new List<string>(found.Count);
        foreach (var candidate in found)
        {
            if (CommandGuard.IsSafeToolPath(candidate)) candidates.Add(candidate);
            else AppendStartupLog("node.exe 候选路径含 cmd 会展开的字符（% 或引号），已跳过：" + candidate);
        }
        if (candidates.Count == 0)
        {
            if (found.Count > 0)
                throw new InvalidOperationException(
                    $"找到 {found.Count} 个 node.exe，但路径都不能安全地拼进命令行（含 % 或引号）：\n" +
                    string.Join("\n", found) + "\n" +
                    "这类路径通常来自 PATH 里未展开的 %变量%。请把 %变量% 改成实际路径后重试，" +
                    $"或把正确的目录写进：{Path.Combine(LocalAppDir, "node-dir.txt")}");
            throw new FileNotFoundException(
                "未找到 node.exe。请先安装 Node.js LTS（18 或更高，https://nodejs.org），" +
                "安装完成后重新打开本程序。\n" +
                $"若 Node 装在非标准目录，可把该目录写进：{Path.Combine(LocalAppDir, "node-dir.txt")}",
                "node.exe");
        }

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
        if (found is not null) return CommandGuard.GuardToolPath(found, "npm.cmd");
        throw new FileNotFoundException(
            "未找到 npm.cmd。npm 随 Node.js 一起安装，请重装 Node.js LTS（18 或更高）。", "npm.cmd");
    }

    private static string? ResolvePnpmPath(string? nodePath = null) =>
        CommandGuard.GuardToolPath(
            (nodePath is not null ? FindToolBeside(nodePath, "pnpm.cmd") : null) ?? FindTool("pnpm.cmd"),
            "pnpm.cmd");

    /// <summary>corepack 是随 Node 附带的包管理 shim（Node ≥ 16.9），没有真实 pnpm 时退到它。</summary>
    private static string? ResolveCorepackPath(string? nodePath = null) =>
        CommandGuard.GuardToolPath(
            (nodePath is not null ? FindToolBeside(nodePath, "corepack.cmd") : null) ?? FindTool("corepack.cmd"),
            "corepack.cmd");

    // IsSafeToolPath / GuardToolPath 已迁到 CommandGuard（npm/pnpm/corepack 与 node
    // 候选路径在内，所有要进 cmd 命令行的路径都过同一道闸，见该类头部说明）。
}
