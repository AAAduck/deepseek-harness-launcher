// ── HarnessForm 的「引擎版本槽：归档、engine.old 认领协议、恢复」部分 ──────────────────────────────────────────
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

    // ---- 引擎版本槽 ---------------------------------------------------------

    private const string EngineSlotPrefix = "engine.";

    private static string EngineSlotDirFor(string version) => Path.Combine(LocalAppDir, EngineSlotPrefix + version);

    /// <summary>broken- 槽名加序号岔开：同一秒内两次归档失败会撞名，Directory.Move 抛 IOException。</summary>
    private static string BrokenSlotDirFor()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = EngineSlotDirFor("broken-" + stamp);
        for (var n = 2; Directory.Exists(candidate); n++)
            candidate = EngineSlotDirFor($"broken-{stamp}-{n}");
        return candidate;
    }

    /// <summary>
    /// 版本号必须是一个纯粹的目录名片段：它会被拼成 <c>engine.&lt;版本&gt;</c>
    /// 再交给 ForceDeleteDirectory 递归删除。来源虽是真实目录名（风险很低），
    /// 但删除不可逆，这里作为纵深防御卡一道。
    /// internal（而非 private）是为了能被单测直接钉住：它守着的正是"递归删除"，
    /// 而这五条约束里任何一条被"顺手简化"掉，测试都会红——比如去掉空白检查后
    /// 带空格的版本号会进得来，而报错文案仍然承诺着"不能含空白"。
    /// </summary>
    internal static bool IsSafeVersionToken(string? version) =>
        !string.IsNullOrWhiteSpace(version) &&
        version.Length <= 64 &&
        // 空格并不在 Path.GetInvalidFileNameChars 里，而安装失败的报错文案承诺了
        // "不能含空白"——文案说到的就要真检查（纵深防御，成本一行）。
        !version.Any(char.IsWhiteSpace) &&
        version == Path.GetFileName(version) &&
        !version.Contains("..", StringComparison.Ordinal) &&
        version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        // Windows 建目录会静默剥掉尾点（"engine.1.2.3." 实际建成 "engine.1.2.3"），
        // 之后列举/删除按带尾点的名字找就永远找不到——槽目录名失配。
        // 尾点也不在 GetInvalidFileNameChars 里，与空格同是"合法但会被系统改写"的形状。
        !version.EndsWith('.');

    /// <summary>目录占用，MB 粒度。用 EnumerateFiles 避免一次性把所有 FileInfo 建出来。
    /// 与 ForceDeleteDirectory 同一个理由走 \\?\ 扩展前缀：LongPathsEnabled 默认关闭的
    /// 机器上，引擎 node_modules 的深路径同样会超过 MAX_PATH，不加前缀时枚举抛异常
    /// 被吞掉，版本列表的大小列就静默显示"—"。这里的路径来自我们自己的目录规划
    /// （绝对路径），前缀化是安全的。</summary>
    private static long DirectorySizeBytes(string dir)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(
                         ToExtendedPath(dir), "*",
                         new System.IO.EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             // 跳过 junction / 符号链接。指向引擎目录**外部**的链接会让
                             // 这里把外部文件的大小算进来（"大小"列虚高、据此算配额也虚高）；
                             // 而**环形** junction（a → 父目录 → a）会让 AllDirectories
                             // 永远枚举下去——后台线程上的死循环，busy 随之永不解除。
                             // 代价只是"链接指向处的大小没算"，而那本就不属于这份引擎。
                             // ConfigBackup 的恢复路径已做同款 ReparsePoint 防护，这里对齐。
                             AttributesToSkip = FileAttributes.ReparsePoint,
                             IgnoreInaccessible = true,
                         }))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 把升级后留下的 engine.old 归档成 engine.&lt;版本号&gt; 槽。
    /// 跨进程互斥不靠锁文件，靠**同卷目录改名的原子性**认领：抢到的人才许做
    /// "删同名槽 + 归档"，把"删"这个破坏性动作关进只有一个人能进的临界区
    /// （两个执行者交错曾把上一版本整份丢掉，见 DESIGN-NOTES.md §4）。
    /// </summary>
    private static void MigrateEngineOldToSlot()
    {
        // 同进程内也要互斥。用户点「启动」的那次归档（RunStartAsync 里 Task.Run 后不等待）
        // 与每小时那轮定时归档（RefreshStatusAsync 里 `_ =` 丢出去）可以并跑，两者都只看
        // Directory.Exists 就往下走。锁只管本进程；跨进程仍然只能靠下面的改名认领。
        // 临界区里还有版本切换/删除（见 migrateGate 声明处的说明），它们持锁时间
        // 可能长到秒级（整目录删除），本方法在后台线程上同步等待即可，不占 UI 线程。
        migrateGate.Wait();
        try
        {
            try
            {
                // 先把上次死在认领之后的残留挪回原位（不挪的话它既不参与版本列举、
                // 也不参与收尾，就是一份用户看不见的 214 MB）。
                RestoreStaleClaims();

                // 三条规则的顺序本身就是要点（见 PlanEngineOldArchive），这里只照着执行。
                var plan = PlanEngineOldArchive(
                    Directory.Exists(engineDir),
                    Directory.Exists(engineOldDir),
                    Directory.Exists(engineMigratingDir));

                // ② 收尾。**这一路必须自己先认领**（见 ArchiveMigratingLeftover）。
                if (plan.FinishClaimed) ArchiveMigratingLeftover();

                // 收尾之后 engine.old 可能已经被上一轮处理掉了（也可能没动），再确认一次。
                if (!plan.ClaimOld || !Directory.Exists(engineOldDir)) return;
                try
                {
                    // 认领直接落**私有名**（engine.migrating.<guid>）：共享名会被另一
                    // 会话的收尾路当"无主残留"无条件抢走，最坏交错把对方刚填好的槽
                    // 再整棵删掉（三者皆空、上一版本丢失）。认领副本的三个读取方
                    // （版本槽列举排除、RecoverEngineSwap 恢复源、RestoreStaleClaims）
                    // 都已按认领副本语义处理它。
                    var claimed = Path.Combine(LocalAppDir, EngineClaimPrefix + Guid.NewGuid().ToString("N")[..8]);
                    Directory.Move(engineOldDir, claimed);
                    // NTFS 同卷改名**保留**目录的创建时间，而陈旧判定用的正是创建时间
                    // ——改名后必须刷新，否则 30 分钟守卫从未真正生效。
                    try { Directory.SetCreationTimeUtc(claimed, DateTime.UtcNow); } catch { }
                    if (ArchiveClaimedDir(claimed)) return;
                    // 归档没成：挪回共享名 engine.migrating，让下一轮收尾路立即重试——
                    // 与 ArchiveMigratingLeftover 的失败分支同一语义，绝不能就地删
                    // （里面装的是上一版本）。
                    try { if (Directory.Exists(claimed)) Directory.Move(claimed, engineMigratingDir); }
                    catch { }
                }
                catch (IOException) { return; }                        // 已被别人认领 / 源刚好没了
                catch (UnauthorizedAccessException) { return; }
            }
            catch (Exception ex) { Swallow.Quiet(ex, "migrate-engine-old"); }
        }
        finally { migrateGate.Release(); }
    }

    // 同进程内也要互斥。用户点「启动」的那次归档（RunStartAsync 里 Task.Run 后不等待）
    // 与每小时那轮定时归档（RefreshStatusAsync 里 `_ =` 丢出去）可以并跑，两者都只看
    // Directory.Exists 就往下走。用 SemaphoreSlim 而不是 lock：切换/删除版本
    // （ActivateEngineVersionAsync / DeleteEngineVersionAsync）也要进同一个临界区，
    // 而它们的进入点在 await 之后（异步等待锁，lock 做不到）；归档侧在后台线程上
    // 同步 Wait，两侧串行化的是同一批 engine.<版本> 槽目录的删改。
    private static readonly SemaphoreSlim migrateGate = new(1, 1);

    /// <summary>
    /// engine.migrating 的收尾入口：**先原子改名认领（私有名），抢到才动**。
    /// 不认领的话，两个执行者并跑时后到者的 ForceDeleteDirectory 会把先到者
    /// 刚归档好的槽整棵删掉（时间线见 DESIGN-NOTES.md §4）。
    /// </summary>
    private static void ArchiveMigratingLeftover()
    {
        var claimed = ClaimMigratingDir();
        if (claimed is null) return;      // 别人抢到了（跨进程），本轮不碰
        if (ArchiveClaimedDir(claimed)) return;   // 这份数据已被处置（或已不存在）

        // 归档没成（Move 失败 / 版本读不出）：**挪回 engine.migrating，不能就地删**。
        // 里面装的是上一版本，删掉等于把它永久丢掉——而这正是认领机制本来要防的损失。
        // 挪回去之后下一轮会重试，环境检测也仍然看得见它（认领副本是看不见的）。
        try
        {
            if (Directory.Exists(claimed)) Directory.Move(claimed, engineMigratingDir);
        }
        catch { }
    }

    /// <summary>
    /// 把 engine.migrating 原子改名成一份"私有的"，成功者独占本轮收尾。
    /// 私有名是这套协议的关键：收尾路只会对**自己抢到的**目录动手，
    /// 别人正拿着归档的数据它永远碰不到（engine.old 的认领现在也直接落私有名，
    /// 共享名 engine.migrating 只作为失败回退与陈旧回收的落点）。
    /// </summary>
    private static string? ClaimMigratingDir()
    {
        if (!Directory.Exists(engineMigratingDir)) return null;
        var claimed = Path.Combine(LocalAppDir, EngineClaimPrefix + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.Move(engineMigratingDir, claimed);
            // 同 MigrateEngineOldToSlot 的认领：刷新创建时间。同卷改名保留创建时间，
            // 不刷的话 RestoreStaleClaims 的 30 分钟陈旧判定会把刚抢到的活认领
            // 当成陈旧残留挪走。
            try { Directory.SetCreationTimeUtc(claimed, DateTime.UtcNow); } catch { }
            return claimed;
        }
        catch (IOException) { return null; }            // 已被别人认领走了
        catch (UnauthorizedAccessException) { return null; }
        catch (Exception ex) { Swallow.Quiet(ex, "claim-migrating"); return null; }
    }

    /// <summary>当前所有认领目录（engine.migrating.&lt;guid&gt;）。</summary>
    private static IEnumerable<string> ClaimDirs()
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(LocalAppDir, EngineClaimPrefix + "*"); }
        catch { yield break; }
        foreach (var dir in dirs) yield return dir;
    }

    /// <summary>
    /// 把上次归档被打断留下的认领目录挪回 engine.migrating，让本轮正常归档它。
    /// **不直接删**：进程死在"认领之后、归档之前"时，那份目录里装的正是上一版本，
    /// 删掉等于丢掉可回退副本。仍可能被别人持有的用创建时间挡开（归档是秒级动作）。
    /// </summary>
    private static void RestoreStaleClaims()
    {
        foreach (var dir in ClaimDirs())
        {
            try
            {
                if (Directory.GetCreationTimeUtc(dir) > DateTime.UtcNow - ClaimStaleAfter) continue;
                if (Directory.Exists(engineMigratingDir)) continue;    // 已有落点，这一份只能等下一轮
                Directory.Move(dir, engineMigratingDir);
            }
            catch { }
        }
    }

    /// <summary>
    /// 这份认领是否已经陈旧（创建时间早于 <see cref="ClaimStaleAfter"/>）。
    /// 与 <see cref="RestoreStaleClaims"/> 共用同一道判据；读不出创建时间时按
    /// "陈旧"处理——那正是 <see cref="RestoreStaleClaims"/> 的兜底方向（多回收一次，
    /// 而它挪的是 engine.migrating，与本方法的恢复源不冲突）。
    /// </summary>
    private static bool IsStaleClaim(string dir)
    {
        try { return Directory.GetCreationTimeUtc(dir) <= DateTime.UtcNow - ClaimStaleAfter; }
        catch { return true; }
    }

    /// <summary>归档要依次做的两步。</summary>
    internal readonly record struct ArchivePlan(bool FinishClaimed, bool ClaimOld);

    /// <summary>
    /// 这一轮归档该做什么（纯函数、可单测）。三条规则，顺序本身是防丢数据的关键：
    ///
    /// ① 活动引擎缺失时**一律不动**——那是 <see cref="RecoverEngineSwap"/> 的活，
    ///    它优先把 engine.old 搬回活动目录；在这里抢着归档会让它无源可搬。
    /// ② engine.migrating 存在就说明上一次没收尾，**先**把它归档掉：它占着双份磁盘
    ///    （实测每份约 214 MB），又因为不参与版本列举而用户在界面上根本看不见——
    ///    这是典型的"坏了也不报错"，所以必须先收尾。
    /// ③ 之后才认领 engine.old。两者同时存在时也是先 ② 再 ③：收尾完认领才有落点，
    ///    否则 Move 会因为目标目录已存在而失败（失败也是安全的：只是本轮不归档）。
    /// </summary>
    internal static ArchivePlan PlanEngineOldArchive(bool engineExists, bool oldExists, bool migratingExists)
    {
        if (!engineExists) return new ArchivePlan(false, false);
        return new ArchivePlan(migratingExists, oldExists);
    }

    /// <summary>
    /// 为一次"占用 engine.tmp"做准备：把上一轮留在那里的东西清干净。
    /// engine.tmp 是 <see cref="RecoverEngineSwap"/> 的最后恢复源——进程死在
    /// 「切换版本」两次改名之间时，它装着切换前完整活动引擎的唯一副本。
    /// 所以：完整引擎挪 broken- 槽保住（可逆），半截安装残骸才删——不可逆的
    /// 只能是"删掉唯一副本"，不能是"多一个 broken- 槽"。
    /// </summary>
    private static void PreserveOrDiscardStagedEngine()
    {
        if (!Directory.Exists(engineStageDir)) return;
        if (!IsCompleteEngineInstall(engineStageDir))
        {
            ForceDeleteDirectory(engineStageDir);
            return;
        }
        try
        {
            var kept = BrokenSlotDirFor();
            Directory.Move(engineStageDir, kept);
            AppendStartupLog($"engine.tmp 里是一份完整引擎，已保留为 {Path.GetFileName(kept)}");
        }
        catch (Exception ex)
        {
            // 连改名都失败（目标已存在、权限被撤）：宁可留下残骸，也不删这份数据。
            AppendStartupLog("engine.tmp 里的完整引擎无法移入版本槽，已原样保留：" + ex.Message);
        }
    }

    /// <summary>
    /// 归档**已被本进程认领**的那份数据。不在则什么都不做。</summary>
    ///
    /// 两条调用路径，两种认领方式，但进来时都满足同一个前提："此刻没有别人持有它"——
    /// 要么本进程刚用改名抢到（engine.old → engine.migrating，或收尾路的私有认领），
    /// 要么它本就是上一轮认领成功后留下的遗留（那种情况下别的进程也已经不在了）。
    /// 收尾路**不能**直接对 engine.migrating 调本方法：那份数据是无主的，
    /// 两个执行者并跑时后到者会删掉先到者刚归档好的槽（见 <see cref="ArchiveMigratingLeftover"/>）。
    ///
    /// 返回值：true = 这份数据已被处置完（归档成槽 / 删掉 / 本来就不存在），
    /// false = 仍然原样留在 <paramref name="claimedDir"/> 里。调用方据此决定
    /// 要不要把它挪回 engine.migrating 让下一轮重试——**绝不能在失败时就地删除**，
    /// 里面装的是上一版本。
    /// </summary>
    private static bool ArchiveClaimedDir(string claimedDir)
    {
        try
        {
            if (!Directory.Exists(claimedDir)) return true;   // 已被别人处理掉

            var oldVersion = ReadEngineVersion(claimedDir);
            if (oldVersion is null)
            {
                // 读不出版本（装了一半）：不能确定它属于哪个槽，保守地留着让用户自己决定。
                // 注意它此时位于 engine.migrating（或收尾路的认领副本）而非 engine.old，
                // 日志要把位置说清楚。
                AppendStartupLog($"{Path.GetFileName(claimedDir)} 无法读出引擎版本，保留原样未归档");
                return false;
            }
            // 纵深防御：version 来自目录里那份 package.json——被篡改/损坏成含 "../"
            // 的字符串时，EngineSlotDirFor(oldVersion) 经规范化可能指到 DeepSeekHarness
            // 之外，下面的 ForceDeleteDirectory 会**递归删除**目标。用户可编辑的
            // engine-version.txt 与对话框入参都过 IsSafeVersionToken，这个来源不能例外；
            // 不合法就走"保留原样"分支，让环境检测把它显示出来，由用户决定。
            if (!IsSafeVersionToken(oldVersion))
            {
                AppendStartupLog($"{Path.GetFileName(claimedDir)} 的版本号「{oldVersion}」不是合法目录名，保留原样未归档");
                return false;
            }

            if (string.Equals(ReadEngineVersion(engineDir), oldVersion, StringComparison.OrdinalIgnoreCase))
            {
                // 与活动版本同一个版本号，留两份纯属浪费磁盘。
                ForceDeleteDirectory(claimedDir);
                return true;
            }

            var slot = EngineSlotDirFor(oldVersion);
            ForceDeleteDirectory(slot);
            Directory.Move(claimedDir, slot);
            AppendStartupLog($"已把上一版本 {oldVersion} 归档为可切换版本");
            return true;
        }
        catch (Exception ex) { Swallow.Quiet(ex, "archive-claimed-dir"); return false; }
    }

    /// <summary>
    /// 目录名（<c>engine.</c> 之后的部分）算不算一个**版本槽**。纯函数、可单测——
    /// 它决定哪些目录会出现在「版本管理」里，而认错的后果是双向的：
    /// 把中转目录当版本槽列出来，用户会看到一个叫 "migrating.1a2b3c4d" 的怪条目；
    /// 反过来把真版本槽漏掉，那份 214 MB 就成了用户看不见也管不了的东西。
    ///
    /// 排除清单：<c>old</c> / <c>tmp</c> 是中转目录，<c>migrating</c> 及其
    /// <c>migrating.&lt;guid&gt;</c> 认领副本同理（认领只在归档期间存在，是瞬时状态）。
    /// 三者一律不区分大小写：这三个目录都由本程序以小写创建，判别的现实风险接近零，
    /// 但这个函数的职责就是"把不像版本槽的挡在门外"，让三种判据用同一套规则
    /// 比为其中两条要严更划算——否则日后有人只改其中一条，比对错更难发现。
    /// </summary>
    internal static bool IsEngineSlotName(string? version) =>
        !string.IsNullOrEmpty(version) &&
        !version.Equals("old", StringComparison.OrdinalIgnoreCase) &&
        !version.Equals("tmp", StringComparison.OrdinalIgnoreCase) &&
        !version.StartsWith("migrating", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 列出已安装的引擎版本。**必须放到后台执行**：每个版本都要 DirectorySizeBytes
    /// 递归遍历一遍 node_modules（本机实测 2.5 万个文件），两个版本就是几万次
    /// FileInfo.Length。同步做在 UI 线程上，"版本管理"窗口会冻结数秒并被 Windows
    /// 判为未响应——而那时对话框还没画出来，连沙漏都看不到。
    ///
    /// 本方法及其下游是**纯只读**的：目录改名（engine.old 归档）已移到启动路径，
    /// 这里只剩 Directory.GetDirectories + 统计大小。
    ///
    /// 但它同样进 <see cref="migrateGate"/>：只读不代表"与破坏性操作无关"。
    /// 「版本管理」的 ConfirmClose 允许"关窗后台继续跑"，于是两个对话框实例可以
    /// 同时活着——它们各自的 UI 层 <c>busy</c> 布尔量互不相识，**数据层才是真正的
    /// 互斥**（切换 / 删除 / 归档三者都已在闸内，见 migrateGate 声明处的说明）。
    /// 不进闸的话，列举与"递归删 2.5 万文件"交错，`File.Exists(bin.js)` 会在删到一半时
    /// 返回 false → 那个版本条目凭空消失；`DirectorySizeBytes` 则只统计到一部分，
    /// 大小列显示一个偏小的数字。两者都不报错，只是给用户一份撕裂的快照。
    ///
    /// 等待是安全的：闸的持有者一定在做磁盘 IO（改名 / 删除 / 归档），
    /// 界面此时本就显示"正在…"，多等几秒不会造成新的卡顿。
    /// </summary>
    internal Task<IReadOnlyList<EngineVersionEntry>> GetInstalledEngineVersionsAsync() =>
        Task.Run(() =>
        {
            migrateGate.Wait();
            try { return GetInstalledEngineVersionsCore(); }
            finally { migrateGate.Release(); }
        });

    private IReadOnlyList<EngineVersionEntry> GetInstalledEngineVersionsCore()
    {
        var result = new List<EngineVersionEntry>();

        var activeVersion = ReadEngineVersion(engineDir);
        if (activeVersion is not null)
        {
            result.Add(new EngineVersionEntry(
                activeVersion, engineDir, true,
                DirectorySizeBytes(engineDir),
                Directory.GetCreationTime(engineDir)));
        }

        string[] slotDirs;
        try { slotDirs = Directory.GetDirectories(LocalAppDir, EngineSlotPrefix + "*"); }
        catch { return result; }   // 根目录都列不出来：返回已有的活动版本即可

        // try 收窄到单个目录体内：某个槽损坏（权限被撤、Defender 占着、半截删除）
        // 不该中止整个枚举——原实现整个 foreach 一个 try，一个坏目录就让版本列表
        // 静默缺掉其后所有条目，用户在「版本管理」里看到的比实际少。
        foreach (var dir in slotDirs)
        {
            try
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith(EngineSlotPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var version = name[EngineSlotPrefix.Length..];
                if (version.Length == 0) continue;
                // "engine.old" / "engine.tmp" / "engine.migrating[.<guid>]" 不是版本槽，跳过。
                if (!IsEngineSlotName(version)) continue;
                if (!File.Exists(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"))) continue;
                result.Add(new EngineVersionEntry(
                    version, dir, false,
                    DirectorySizeBytes(dir),
                    Directory.GetCreationTime(dir)));
            }
            catch { /* 单个坏目录跳过，别拖垮整份列表 */ }
        }

        return result;
    }

    /// <summary>
    /// 上次替换若在两步之间被打断（engine 已改名、staging 还没顶上），把 engine.old
    /// 改回来，避免"引擎凭空消失"。必须先于 MigrateEngineOldToSlot 跑，否则
    /// "活动目录缺失"的中间态会被归档动作掩盖掉。
    ///
    /// 兜底按 engine.old → engine.migrating → 陈旧的认领副本 → engine.tmp（最后）
    /// 的顺序认领恢复源：不认的话，活动引擎缺失时只剩重装一条路（214 MB），而
    /// 那份能用的版本正躺在旁边。所有恢复源一视同仁地过完整性检查——放行半截
    /// 安装会把 UI 内无解的死局一路传下去；认不了源时 EnsureEngineAsync 本来
    /// 就会走重装自愈（代价只是一次安装）。
    /// </summary>
    private void RecoverEngineSwap()
    {
        try
        {
            if (Directory.Exists(engineDir)) return;
            // 认领目录必须过"陈旧"这道闸，与 RestoreStaleClaims 同一纪律：
            // 另一个进程可能**正在**归档它（认领只在归档期间存在，秒级），把它
            // 抢来当恢复源就会在对方写到一半时把数据搬走。此处原本没有这道守卫，
            // 两处口径不一致。30 分钟足够区分"别人正拿着"与"上次死在认领之后"。
            foreach (var source in new[] { engineOldDir, engineMigratingDir }
                         .Concat(ClaimDirs().Where(IsStaleClaim))
                         .Append(engineStageDir))
            {
                if (!Directory.Exists(source)) continue;
                if (!IsCompleteEngineInstall(source))
                {
                    AppendStartupLog($"{Path.GetFileName(source)} 不是完整引擎（缺入口/版本/直接依赖），不作为恢复源");
                    continue;   // 半截安装残骸：不是可回退副本，交给重装路径清理
                }
                Directory.Move(source, engineDir);
                AppendStartupLog($"已从 {Path.GetFileName(source)} 恢复引擎目录");
                return;
            }
        }
        catch (Exception ex) { Swallow.Quiet(ex, "recover-engine-swap"); }
    }

    /// <summary>
    /// 把引擎装到 staging 目录，全部成功后才替换正式目录。
    /// staging 的意义：安装被中断或失败时，正在能用的那一份永远不受影响
    /// （npx 那条路做不到——半成品残骸原地修不好，只能整体删掉重下）。
    /// 替换用"旧目录先改名、staging 顶上、再删旧目录"三步，任一步失败都能恢复。
    ///
    /// <paramref name="expectedActiveVersion"/> 是替换开始时读到的活动版本，
    /// 替换前会在 migrateGate 内**再核对一次**：跨会话时另一启动器可能已经换过引擎，
    /// 那时不该拿陈旧判断去覆盖它（null = 不校验，用于"活动引擎本来就不存在"的首装）。
    /// </summary>
    private async Task InstallEngineAsync(
        string node, string versionSpec, string registry, CancellationToken ct, string? expectedActiveVersion = null)
    {
        // versionSpec 会进 package.json，而 engine-version.txt 里的版本锁可能是
        // 用户手写的。不校验的话一个引号就能拼出非法 JSON，报错却由 npm 背锅、极难定位。
        if (!IsSafeVersionToken(versionSpec))
            throw new InvalidOperationException(
                $"引擎版本指定不合法：{versionSpec}\n" +
                "应为纯版本号（如 0.1.5-rc.2）或 latest，不能含空白、引号或路径分隔符。");

        var npm = ResolveNpmPath(node);
        // 后台线程：首装/重试时这是一次 2.5 万文件的递归删除。staging 的清点、
        // 建目录、写 package.json **全程持 migrateGate**：与「切换版本」的持闸
        // 临界区互踩会互毁出半截目录/丢唯一副本。npm 本体（可能跑几分钟）刻意
        // 放在闸外——闸只保护目录级原子操作，不值得拉长到分钟级；安装期间
        // engine.tmp 被切换动过的残余由替换前的持闸复查兜住。取消路径的清理
        // 同样持闸（见下方 OCE catch）。
        await migrateGate.WaitAsync(CancellationToken.None);
        try
        {
            await Task.Run(PreserveOrDiscardStagedEngine, CancellationToken.None);
            Directory.CreateDirectory(engineStageDir);

            // 预置最小 package.json：npm 在清单齐全的目录里会写 package-lock.json，
            // 配合 --prefer-offline 命中本地 cacache，重装基本不重新下载。
            // 这里写的 spec 就是最终 spec（调用方传精确版本号，不是 latest/caret），
            // 再配合下面的 --save-exact，manifest 与 lock 才会真正一致 → 装出来的版本可复现。
            // 用序列化器生成，不再手工拼 JSON 字符串。
            File.WriteAllText(
                Path.Combine(engineStageDir, "package.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["name"] = "dsh-engine",
                    ["private"] = true,
                    ["version"] = "0.0.0",
                    ["dependencies"] = new Dictionary<string, string> { [EnginePackageName] = versionSpec },
                }, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
        }
        finally { migrateGate.Release(); }

        // 闸门设在拼接点（GuardRegistryForCommandLine 的理由见那里）。
        registry = CommandGuard.GuardRegistryForCommandLine(registry);
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
        // 同 StartHarnessAsync：读父进程 PATH，缺 nodeDir 时才前置，不做整体覆盖。
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
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

            // 取消击杀挂同步回调（理由见 RunCmdAsync 同款注释）：关窗丢掉 OCE 续延时，
            // npm 整棵树会带着 engine.tmp 继续跑，下次升级的 ForceDeleteDirectory
            // 就撞上"目录被占"。回调 + 下面 catch 双保险。
            using var killOnCancel = installCts.Token.Register(() =>
            {
                try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            await proc.WaitForExitAsync(installCts.Token);
            // ⚠ 进程退出 ≠ 输出处理完：OutputDataReceived / ErrorDataReceived 是
            // **异步**投递的，进程一退出，管道里剩下的行还排在线程池队列上没派发。
            // 带不带 token 的 WaitForExitAsync 都**没有**"等异步输出处理完"的保证——
            // 那是无参**同步** WaitForExit() 才有的文档语义。此前这里调的是
            // WaitForExitAsync(None)，进程已死时立即返回已完成任务，等于没等，
            // 于是下面三处 RecentOutputSummary() 恰好在失败时缺掉最关键的
            // npm error 尾巴，用户拿到的是一段不完整的报错上下文。
            // 改用同步 WaitForExit()（Task.Run 包住，不占 UI 线程），再配
            // DrainTimeoutMs 兜底：孙进程若继承了管道写端且自己不退出，EOF 迟迟
            // 不来，不能为它挂死升级流程——与 GetLatestEngineVersionAsync 已确立的
            // 排干兜底同一条纪律。
            var drained = Task.Run(() =>
            {
                try { proc.WaitForExit(); }
                catch (Exception ex) { Swallow.Quiet(ex, "install-output-drain"); }
            });
            await Task.WhenAny(drained, Task.Delay(DrainTimeoutMs));

            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"引擎安装失败（npm 退出码 {proc.ExitCode}）。\n{RecentOutputSummary()}");

            var staged = ReadEngineVersion(engineStageDir);
            if (staged is null)
                throw new InvalidOperationException(
                    "安装结束但引擎清单不可读，已放弃替换（当前引擎未受影响）。\n" + RecentOutputSummary());

            // npm 退出码 0 **不等于**装完整了：它逐包解包、没有事务性，机器在
            // 解包途中被强杀/断电不会留下非零退出码。缺依赖的半截 staging 一旦顶上，
            // 用户手上就只剩一份"启动永远 Cannot find module"的引擎，而
            // EnsureEngineAsync / 「升级」/「版本管理」此后都会认定它已安装——
            // 死局。这里在**替换之前**多验一次（见 IsCompleteEngineInstall）。
            var missingDeps = FindMissingEngineDependencies(
                ReadEngineDependencyNames(engineStageDir), MakeEngineDependencyProbe(engineStageDir)).ToList();
            if (missingDeps.Count > 0)
                throw new InvalidOperationException(
                    $"安装结束但依赖不完整（缺 {missingDeps.Count} 个：{string.Join("、", missingDeps.Take(5))}" +
                    (missingDeps.Count > 5 ? " 等）" : "）") +
                    "，已放弃替换（当前引擎未受影响）。\n" + RecentOutputSummary());

            // 替换三步全程持 migrateGate：切版本/删槽/归档改的都是同一批目录，
            // 交错轻则 Move 失败、重则"升级假成功"（旧引擎被顶上、界面报已就绪）。
            // 持闸后再复查活动版本（null = 不校验）：migrateGate 只管本进程，
            // 跨会话靠这道比对兜住。与 ActivateEngineVersionAsync 持闸后复查同一纪律。
            await migrateGate.WaitAsync(ct);
            try
            {
                if (expectedActiveVersion is not null)
                {
                    var confirmed = ReadEngineVersion(engineDir);
                    if (!string.Equals(confirmed, expectedActiveVersion, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "安装期间活动引擎已被其他启动器改动，已放弃替换（当前引擎未受影响）。" +
                            "请重新点一次「升级」。");
                }

                // 替换三步：清掉上上次的 engine.old → 现行目录改名 → staging 顶上。
                // 刻意**不删**换下来的 engine.old：升级后发现插件不兼容时，
                // 把 engine 删掉、engine.old 改名回来即可完整回退到上一个能用的版本。
                // 代价是引擎目录占双份（实测每份约 214 MB），换来的是可回退。
                //
                // 第三步失败必须**当场**把 engine.old 搬回去，不能只留给下次启动的
                // RecoverEngineSwap：否则这一轮用户手上就是"引擎凭空消失"，
                // 要等到下次双击才自愈。搬回去的写法与 ActivateEngineVersionAsync 里
                // 处理 engineStageDir 的那一段刻意保持一致——同一个坑修一次不够，
                // 两条路径都得堵上。
                await Task.Run(() => ForceDeleteDirectory(engineOldDir), CancellationToken.None);
                if (Directory.Exists(engineDir))
                {
                    // 只有**完整**的旧引擎才值得留作回退副本。此刻在位的 engine 若是半截
                    // 安装（升级前就已损坏、或上次替换落了残骸），把它改名成 engine.old 只会
                    // 用一份坏副本占掉"可回退"这个位置——而这里正是重装来修复它的路径。
                    if (IsCompleteEngineInstall(engineDir))
                        Directory.Move(engineDir, engineOldDir);
                    else
                    {
                        AppendStartupLog("在位的引擎目录不完整，不留作回退副本，直接清理后替换");
                        await Task.Run(() => ForceDeleteDirectory(engineDir), CancellationToken.None);
                    }
                }
                try
                {
                    Directory.Move(engineStageDir, engineDir);
                }
                catch
                {
                    try
                    {
                        if (Directory.Exists(engineOldDir)) Directory.Move(engineOldDir, engineDir);
                    }
                    catch (Exception rollbackEx)
                    {
                        AppendStartupLog("替换引擎失败且回退也失败：" + rollbackEx.Message);
                    }
                    // 无论回滚成不成功，staging 都是这一轮的残骸，必须清掉。
                    // Directory.Move 抛的是 IOException / UnauthorizedAccessException，
                    // 不是 OperationCanceledException，所以下面那个 catch 里的清理
                    // **不会**覆盖到这里——而回滚成功时留下的是一份完整有效的 214 MB，
                    // 用户界面却一切正常，最容易被彻底忘掉。
                    try { await Task.Run(() => ForceDeleteDirectory(engineStageDir), CancellationToken.None); } catch { }
                    throw;
                }
            }
            finally { migrateGate.Release(); }
            InvalidateEngineVersionCache();   // 引擎目录已换新，mtime 缓存必须失效
            SetInfo($"引擎已就绪：{staged}（上一版本已保留，可在「版本管理」里切回）");
        }
        catch (OperationCanceledException)
        {
            // 取消/超时：必须杀掉 npm 整棵树，否则它会留在后台继续装
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            // 清理必须自带 catch：ForceDeleteDirectory 会把"目录被占用"包装成
            // InvalidOperationException 抛出来，让它顶掉本该上抛的 OperationCanceledException
            // ——用户点了取消，看到的却是"升级失败"，方向完全反了（见下面注释）。
            //
            // 删除本身**持 migrateGate**：后台「切换版本」的持闸临界区此刻可能正把
            // 旧活动引擎（唯一副本）暂存在 engine.tmp 里等归档——无闸强删会把那份
            // 唯一副本删掉，而切换随后的告警还在承诺"已原样保留在 engine.tmp"。
            // 取闸用 None：OCE 不会在 651 的持闸 try 内产生（里面没有可取消的等待），
            // 走到这里时闸要么从未被本方法持有、要么已由 finally 释放，不存在自等待。
            await migrateGate.WaitAsync(CancellationToken.None);
            try { await Task.Run(() => ForceDeleteDirectory(engineStageDir)); } catch { }
            finally { migrateGate.Release(); }
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
        // stdout/stderr 两条线程并发到这一步：CompareExchange 保证只有真正更新了戳记的那条
        // 线程会走到 SetInfo——两条同时通过节流检查、把界面连刷两次的情况被挡在门外。
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref lastInstallInfoAtTicks);
        if (nowTicks - lastTicks < TimeSpan.FromMilliseconds(200).Ticks) return;
        if (Interlocked.CompareExchange(ref lastInstallInfoAtTicks, nowTicks, lastTicks) != lastTicks) return;
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
    private static readonly SemaphoreSlim registryGate = new(1, 1);

    // 失败负缓存（UTC ticks 走 Interlocked，与 processRecordCacheAtTicks 同一纪律）：
    // npm 缺失、配置读不出、或配置值不安全时，60 秒内不再重跑 `npm config get registry`。
    // 一次升级路径上本方法最多被调 3 次（EnsureEngineAsync / GetLatestEngineVersionAsync /
    // InstallEngineAsync），不缓存失败时同样的失败要付 3 次（每次 5 秒超时预算）；
    // 成功结果走 effectiveRegistry 永久缓存，互不干扰。
    private static long registryFailureAtTicks;
    private static readonly TimeSpan RegistryFailureCacheTtl = TimeSpan.FromSeconds(60);

    private static async Task<string> ResolveNpmRegistryAsync(string node, CancellationToken ct)
    {
        var cached = Volatile.Read(ref effectiveRegistry);
        if (cached is not null) return cached;
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref registryFailureAtTicks) < RegistryFailureCacheTtl.Ticks)
            return DefaultRegistry;

        await registryGate.WaitAsync();
        try
        {
            cached = Volatile.Read(ref effectiveRegistry);
            if (cached is not null) return cached;
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref registryFailureAtTicks) < RegistryFailureCacheTtl.Ticks)
                return DefaultRegistry;

            string? value = null;
            try
            {
                // ResolveNpmPath 找不到 npm 时抛 FileNotFoundException：行为保持
                // "回落官方源"，但**必须留痕**——"配了镜像为什么没生效"这类问题
                // 的唯一线索就在这条日志里，静默降级正是本项目要消灭的形状。
                var npm = ResolveNpmPath(node);
                var text = await RunCmdAsync(
                    $"\"{npm}\" config get registry", TimeSpan.FromSeconds(5), ct);
                if (text is not null)
                {
                    var candidate = text.Trim().Split('\n').Last().Trim();
                    if (CommandGuard.IsSafeNpmValue(candidate)) value = candidate;
                    else AppendStartupLog(
                        "[npm-registry] 用户配置的 registry 不能安全地拼进命令行（含引号/空白/shell 元字符），" +
                        "本次回落官方源：" + candidate);
                }
            }
            catch (Exception ex)
            {
                Swallow.Quiet(ex, "npm-registry-resolve");
            }

            if (value is not null)
            {
                Volatile.Write(ref effectiveRegistry, value);
                return value;
            }
            // 失败记入负缓存：60 秒内的后续调用直接回落官方源，不再重跑子进程。
            Interlocked.Exchange(ref registryFailureAtTicks, DateTime.UtcNow.Ticks);
            return DefaultRegistry;
        }
        finally { registryGate.Release(); }
    }

    // IsSafeNpmValue / GuardRegistryForCommandLine 已迁到 CommandGuard：
    // 它们是"外部输入进 cmd 命令行"的通用闸门，不该只属于引擎安装这一域。

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
}
