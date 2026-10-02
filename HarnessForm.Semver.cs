// ── HarnessForm 的「插件兼容性检查与 semver 判定」部分 ──────────────────────────────────────────
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
    internal static Version? ParseVersion(string? text)
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
        // 第四段以**输入**有没有为准。此前误写成 nums.Length（恒为 4，条件永真）——
        // 结果恰好正确（三段输入时 nums[3] 默认 0），但那是靠数组默认值兜底，不是判断本身对。
        return new Version(nums[0], nums[1], nums[2], parts.Length > 3 ? nums[3] : 0);
    }

    /// <summary>
    /// 取 semver 预发布标识（'-' 之后、'+' 之前）；没有返回 null。
    /// 必须先按 '+' 截掉 build 段再找 '-'："1.2.3+b-x" 是**带 build 的正式版**，
    /// 在整串里找 '-' 会把 build 段里的连字符当成预发布标识，与 <see cref="ParseVersion"/>
    /// 在第一个 '-' 或 '+' 处截断 core 的口径不一致——同一个版本字符串，
    /// core 判成正式版、预发布却判出 "x"，比较结果就会差一位（1.2.3+b-x 曾被
    /// 当成 1.2.3 的预发布版，^1.2.3 误报"不满足"）。
    /// </summary>
    private static string? VersionPrerelease(string s)
    {
        s = s.Trim();
        var plus = s.IndexOf('+');
        var stem = plus >= 0 ? s[..plus] : s;
        var i = stem.IndexOf('-');
        return i < 0 ? null : stem[(i + 1)..];
    }

    /// <summary>S1 &gt; S2 → 1；相等 → 0；S1 &lt; S2 → -1；无法比较 → null。</summary>
    internal static int? CompareVersionStrings(string a, string b)
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
        var pa = VersionPrerelease(a);
        var pb = VersionPrerelease(b);
        if (pa is null && pb is null) return 0;
        if (pa is null) return 1;
        if (pb is null) return -1;
        return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase) ? 0 : null;
    }

    /// <summary>
    /// 判定 candidateVersion 是否满足声明的范围。支持的形式：
    /// ">=0.1.7-rc.1"、"&gt;=0.1.7-rc.1 &lt;0.3.0-0"、"^4.0.1"、"~1.2.3"、
    /// 裸版本号（= 精确匹配，同 npm 语义）、"A || B"。
    /// 三态返回：true 满足 / false 明确不满足 / null 无法判定。
    /// 本方法及下面几个 semver 辅助是 internal 而非 private，只为一件事：
    /// 能被 tests/DeepSeekHarness.Tests 直接单测。这里每次"拿不准就返回 null"
    /// 都是刻意选择（宁可让用户看到"未能判定"，也不猜）——而这类语义一旦被
    /// 顺手改坏，编译器和运行时都不会有反应。
    ///
    /// 这里必须把"明确不满足"和"无法判定"分开：之前只要有一个 token 判定不出来
    /// 就整体返回 null，导致"已知不兼容"被降级成"未知"，护栏会漏报。
    /// 规则（三值 Kleene：任一候选项满足 → true；全部明确不满足 → false；
    /// 只要还剩一个判不出来 → null）：
    ///   某个候选项的全部 token 都满足 → true；
    ///   某个候选项里有 token 明确不满足（且没有无法判定的 token 挡在前面）→ 该候选项为 false；
    ///   只要存在"无法判定"的候选项，整体就不能是 false——
    ///   例如 ">=0.2.0 || *"，第一个候选项明确不满足、第二个（通配符）判不出来，
    ///   整体必须报 null 而非 false。原先只要存在一个明确不满足的候选项就返回 false，
    ///   等于把"我判不出来"当成"确定不满足"，会凭空报出插件不兼容的警告。
    /// </summary>
    internal static bool? SatisfiesRange(string candidate, string range)
    {
        var anyAlternativeFailed = false;
        var anyAlternativeUnknown = false;
        foreach (var alternative in range.Split("||", StringSplitOptions.RemoveEmptyEntries))
        {
            // 这个候选项里全部"比较器形态 token"的基准（供下面的集合级预发布门槛用）。
            var comparators = new List<(int Major, int Minor, int Build, bool HasPrerelease)>();
            var allSatisfied = true;   // 目前为止每个 token 都满足
            var anyUnknown = false;    // 出现过无法判定的 token
            var tokenCount = 0;
            foreach (var token in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                tokenCount++;
                NoteComparatorBasis(token, comparators);
                var verdict = SatisfiesSingle(candidate, token);
                if (verdict is null) { anyUnknown = true; break; }
                if (verdict.Value) continue;
                allSatisfied = false;
                break;
            }
            // 空白候选项（形如 ">=2.0.0 || "、或多个连续 "||" 留下的空段）：
            // 一个 token 都没有，循环体从不执行，allSatisfied 停在初值 true——
            // 于是这个空段被判成"恒满足"，整条范围直接返回 true。npm 语义下空段
            // 确实匹配一切，可本工具的三态纪律是"拿不准就说拿不准"：把一段我们
            // 根本没读懂的内容判成"满足"，方向恰好是**误报**（护栏漏警）。
            // 所以空段记为 unknown，让整体落到 null（提示"未能判定"）而不是 true。
            if (tokenCount == 0) { anyAlternativeUnknown = true; continue; }
            if (allSatisfied && !anyUnknown)
            {
                // npm 的预发布门槛是**比较器集合级**的（node-semver Range.test 的收尾
                // 规则），必须等全部 token 都数字满足之后在这一层统一裁决——不能塞进
                // SatisfiesSingle 逐 token 做：那会把 ">=0.1.7-rc.1 <0.3.0-0" 配
                // 0.1.7-rc.2 这种"集合里有同三元组预发布基准"的合法组合误判成不满足
                // （凭空多警告）。
                if (PrereleaseAdmittedByComparatorSet(candidate, comparators)) return true;
                allSatisfied = false;
                anyAlternativeFailed = true;
                continue;
            }
            if (anyUnknown) { anyAlternativeUnknown = true; continue; }
            anyAlternativeFailed = true;
        }
        // "无法判定"优先于"明确不满足"：只要还有一个候选项判不出来，
        // 整体就不能断言 false（否则会把本来满足的范围报成插件不兼容）。
        return anyAlternativeFailed && !anyAlternativeUnknown ? false : null;
    }

    /// <summary>
    /// 比较器前缀 → 比较种类。<see cref="SatisfiesSingle"/> 与
    /// <see cref="NoteComparatorBasis"/> 共用这一份——两边各写一份迟早漂移。
    /// </summary>
    private static readonly (string Prefix, int Op)[] ComparatorOps =
        { (">=", 1), ("<=", 2), (">", 3), ("<", 4), ("=", 0) };

    /// <summary>
    /// 单个范围 token 的**数字**判定。npm 对预发布候选还有一道**比较器集合级**的门槛，
    /// 那必须由 <see cref="SatisfiesRange"/> 在整个候选项（比较器集合）上统一裁决
    /// （规则是"集合里至少有一个比较器的基准带预发布且与候选同三元组"，单看一个
    /// token 无从谈起）——所以本方法对预发布候选只回答数字比较的结果，不要在
    /// 这里单独加预发布守卫。
    /// </summary>
    internal static bool? SatisfiesSingle(string candidate, string token)
    {
        token = token.Trim();
        if (token.Length == 0) return null;

        foreach (var (prefix, op) in ComparatorOps)
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
            // 基准是否带**预发布**必须问 VersionPrerelease，不能用 basis.Contains('-')：
            // 后者在 build 元数据里有连字符时会误判——"1.2.3+b-1" 是带 build 的**正式版**，
            // 却被当成"基准带预发布"而返回 null。于是 ^1.2.3+b-1 永远得到"无法判定"，
            // 而 npm 对它有明确答案（1.5.0 满足 ^1.2.3+b-1）。方向安全（不误判），
            // 但白白丢能力，且提示的是"未能判定"这种没法排查的话。
            // VersionPrerelease 先按 '+' 截掉 build 段再找 '-'，与 ParseVersion 的 core
            // 截断口径一致（见该方法的注释）——两处口径分叉过一次，正是这个 bug 的来源。
            if (VersionPrerelease(basis) is not null) return null;
            var v = ParseVersion(basis);
            var c = ParseVersion(candidate);
            if (v is null || c is null) return null;
            // npm 语义：caret/tilde 这类**范围**只接受与基准同 major.minor.patch 的预发布
            // 候选（如 ^4.0.1 只放行 4.0.1-xxx）。此前不实施这条，0.1.5-rc.9 会被判满足
            // ^0.1.0——护栏在漏报警的方向上出错（本工具最不能犯的那个方向）。
            // 语义修正后这里是**明确的 false**（npm 对"预发布不进范围"的定义），
            // 与"拿不准返回 null"的取舍并不冲突：这条不是拿不准，是规则本身。
            if (!PrereleaseAllowedInRange(candidate, v.Major, v.Minor, v.Build)) return false;
            var majorMatters = v.Major > 0;
            var minorMatters = !majorMatters && v.Minor > 0;
            if (majorMatters) return c.Major == v.Major;
            if (minorMatters) return c.Major == 0 && c.Minor == v.Minor;
            return c.Major == 0 && c.Minor == 0 && c.Build == v.Build;
        }

        if (token.StartsWith('~'))
        {
            // tilde：>= 基准，且不越过 minor 位（~1.2.3 → >=1.2.3 <1.3.0）。
            // 基准只写了两段（~1.2）时 ParseVersion 解析不了，按"无法判定"返回——
            // 三态里 unknown 的方向是安全的（提示"未能判定"，而不是误判成满足）。
            // 基准本身带预发布标识（~0.1.5-rc.1）同样返回 null，与 caret 分支对称：
            // npm 对这种范围没有可套用的明确规则，本工具的既定取舍是"判不出来"。
            // 此前只有 caret 有这道守卫，README 却在两种范围上都承诺了它。
            var basis = token[1..].Trim();
            var cmp = CompareVersionStrings(candidate, basis);
            if (cmp is null) return null;
            if (cmp < 0) return false;
            // 同 caret 分支：用 VersionPrerelease 判"基准带预发布"，别用 Contains('-')，
            // 否则 "~1.2.3+b-1" 这类带 build 的正式版基准会被误判成预发布（理由见上）。
            if (VersionPrerelease(basis) is not null) return null;
            var v = ParseVersion(basis);
            var c = ParseVersion(candidate);
            if (v is null || c is null) return null;
            // 同 caret：预发布候选必须与基准同 major.minor.patch 才进得了范围（npm 规则）。
            if (!PrereleaseAllowedInRange(candidate, v.Major, v.Minor, v.Build)) return false;
            return c.Major == v.Major && c.Minor == v.Minor;
        }

        // 裸版本号 = **精确匹配**。npm/semver 里 "1.2.3" 的含义是"恰好这个版本"，
        // 不是"至少这个版本"：之前按 >= 解释，peerDependencies 写死 "0.1.5" 时
        // 引擎 0.9.0 会被误判成"满足"——护栏恰好在危险方向上漏报。
        var bare = CompareVersionStrings(candidate, token);
        return bare is null ? null : bare == 0;
    }

    /// <summary>
    /// 预发布候选是否进得了以 (major, minor, patch) 为基准的范围 token。
    /// npm 规则：预发布候选只在"与某个比较对象共享同一 [major, minor, patch] 三元组"
    /// 时才被 caret/tilde 这类范围接受。返回 false = npm 语义下明确不满足（不是猜）。
    /// 非预发布候选与解析不了的候选不受此门槛限制，维持原有判定路径。
    /// internal（而非 private）：边界要被单测钉住——这条规则曾在漏报警方向出过错。
    /// </summary>
    internal static bool PrereleaseAllowedInRange(string candidate, int major, int minor, int patch)
    {
        // 非预发布候选不受这条门槛限制。
        if (VersionPrerelease(candidate) is null) return true;
        var c = ParseVersion(candidate);
        if (c is null) return true;   // 解析不了的候选维持原判定路径（不额外收紧）
        return c.Major == major && c.Minor == minor && c.Build == patch;
    }

    /// <summary>
    /// npm 的**比较器集合级**预发布门槛（node-semver Range.test 的收尾规则）：
    /// 候选带预发布时，一个候选项（比较器集合）只有在「集合内至少有一个比较器的基准
    /// **带预发布**、且与候选同 [major, minor, patch] 三元组」时才放行；
    /// 否则整项明确不满足（false，不是猜）。此前裸比较器（<c>&gt;=0.2.0</c>）对预发布
    /// 候选只做数字比较，<c>0.3.0-rc.1</c> 被判满足 <c>&gt;=0.2.0</c>——而 npm 语义下
    /// 这个集合不接受任何预发布（peer 不满足、装不上），护栏恰在漏报警方向出错。
    /// <paramref name="comparators"/> 是该候选项里全部"比较器形态 token"（含裸精确
    /// 版本）的基准三元组与预发布标记；caret/tilde 不参与：它们对预发布候选的门槛
    /// 已在自己分支内实施（<see cref="PrereleaseAllowedInRange"/>），基准带预发布时的
    /// 既定取舍是"返回 null"，轮不到这道门槛说话。
    /// 纯函数、可单测——这条规则和 <see cref="PrereleaseAllowedInRange"/> 一样，
    /// 曾在漏报警方向出过错。
    /// </summary>
    internal static bool PrereleaseAdmittedByComparatorSet(
        string candidate,
        IReadOnlyList<(int Major, int Minor, int Build, bool HasPrerelease)> comparators)
    {
        if (VersionPrerelease(candidate) is null) return true;   // 非预发布候选不受此门槛限制
        var c = ParseVersion(candidate);
        if (c is null) return true;                              // 解析不了的候选维持原判定路径
        foreach (var (major, minor, build, hasPrerelease) in comparators)
            if (hasPrerelease && major == c.Major && minor == c.Minor && build == c.Build) return true;
        return false;
    }

    /// <summary>
    /// 收集一个 token 若为"比较器形态"（运算符比较器或裸精确版本）时其基准的三元组
    /// 与预发布标记，供 <see cref="PrereleaseAdmittedByComparatorSet"/> 在候选项层面
    /// 统一裁决。运算符前缀与 <see cref="SatisfiesSingle"/> 共用 <see cref="ComparatorOps"/>；
    /// 裸 token（无运算符、非 caret/tilde）在 SatisfiesSingle 里按精确比较器处理，
    /// 同样参与集合——否则 "0.1.5-rc.1" 精确匹配会因集合里没有可对认的比较器被误拒。
    /// caret/tilde 与解析不了的 token 不收集（理由见 PrereleaseAdmittedByComparatorSet）。
    /// </summary>
    private static void NoteComparatorBasis(
        string token, List<(int Major, int Minor, int Build, bool HasPrerelease)> comparators)
    {
        token = token.Trim();
        var basis = token;
        var isComparator = false;
        foreach (var (prefix, _) in ComparatorOps)
        {
            if (!token.StartsWith(prefix, StringComparison.Ordinal)) continue;
            basis = token[prefix.Length..].Trim();
            isComparator = true;
            break;
        }
        if (!isComparator && !token.StartsWith('^') && !token.StartsWith('~')) isComparator = true;
        if (!isComparator) return;
        if (ParseVersion(basis) is not { } basisVersion) return;   // 解析不了的基准不参与门槛
        comparators.Add((basisVersion.Major, basisVersion.Minor, basisVersion.Build,
            VersionPrerelease(basis) is not null));
    }

    /// <summary>
    /// 候选引擎版本 与 需求 是否能比较。
    /// 只有 DSH 自身那一族（"@deepseek-ai/dsh" 与 "@deepseek-ai/dsh-*"）与引擎同版本发布。
    /// 别的一律不是：实测本机引擎里 cordis=4.0.2、schemastery=3.18.2、cosmokit=1.8.3、
    /// node-addon-system=0.1.2，各有自己的版本号。把引擎版本 0.2.0-rc.2 拿去比
    /// "^4.0.1"（cordis）永远不成立——那正是早期版本会误报"4 项不满足"的原因。
    /// </summary>
    internal static bool IsDshVersionedPackage(string packageName)
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
            // ② pnpm 的 .pnpm/<名字>@<版本>/node_modules/<名字>（版本号就在目录名里）
            var pnpmDir = Path.Combine(modules, ".pnpm");
            if (!Directory.Exists(pnpmDir)) return null;
            // pnpm 把 scoped 包写成 `@scope+name`（斜杠换加号），所以这里必须用
            // **完整包名**去拼 glob：按最后一段（leaf）拼的话，scoped 包永远匹配不上
            // ——而本方法收集到的需求全是 @deepseek-ai/* ，也就是整条兜底路形同虚设。
            var pnpmLeaf = packageName.Replace('/', '+');
            foreach (var dir in Directory.GetDirectories(pnpmDir, pnpmLeaf + "@*"))
            {
                var version = ParsePnpmDirVersion(Path.GetFileName(dir));
                if (version is not null) return version;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 pnpm 的 .pnpm 存放区目录名里取出干净的版本号。纯函数、可单测。
    ///
    /// 两代命名法都要认：
    ///   <c>@deepseek-ai+dsh@0.1.5</c>            —— scoped 包，斜杠换加号
    ///   <c>dsh@1.2.3(react@18.3.1)</c>          —— 旧式 peer 变体
    ///   <c>dsh@1.2.3_react@18.3.1</c>          —— 新式 peer 变体
    ///
    /// 分隔版本号的那个 '@' 必须是**包名之后的第一处**。此前用 LastIndexOf：
    /// 新式变体下会取到 **peer 的版本**（"18.3.1"），连同注释自述要修的那个例子
    /// 一起取错——交给 semver 比出来的结论是彻底无关的另一个包。
    /// 取不出合法版本返回 null（宁可判"无法判定"，也不把乱七八糟的目录名当版本）。
    /// </summary>
    internal static string? ParsePnpmDirVersion(string? dirName)
    {
        if (string.IsNullOrEmpty(dirName)) return null;
        // 从下标 1 起找：下标 0 的 '@' 是 scoped 包名自身的那个，不能当分隔符。
        var at = dirName.IndexOf('@', 1);
        if (at <= 0 || at == dirName.Length - 1) return null;
        var version = dirName[(at + 1)..];
        // peer 变体后缀在版本号之后还跟着一段，两代命名法的分隔符不同。
        var suffix = version.IndexOfAny(new[] { '(', '_' });
        if (suffix >= 0) version = version[..suffix];
        return version.Length > 0 && ParseVersion(version) is not null ? version : null;
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
        Process? proc = null;
        Task<string>? outTask = null;
        Task<string>? errTask = null;
        try
        {
            var npm = ResolveNpmPath(node);
            using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            queryCts.CancelAfter(TimeSpan.FromSeconds(EngineQueryTimeoutSeconds));

            // 注册表要单独取：RunCmdAsync 是异步的，不能再塞进下面的初始化器里。
            var registry = await ResolveNpmRegistryAsync(node, queryCts.Token);
            // 同 InstallEngineAsync：闸门设在拼接点，不靠调用方自觉。
            registry = GuardRegistryForCommandLine(registry);
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c \"\"{npm}\" view {EnginePackageName} version --registry {registry}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var nodeDir = Path.GetDirectoryName(node);
            // 同 StartHarnessAsync：读父进程 PATH，只在缺时前置 nodeDir。
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(nodeDir) && !path.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(nodeDir, StringComparer.OrdinalIgnoreCase))
                psi.Environment["PATH"] = nodeDir + ";" + path;
            ConfigureOptionalProxy(psi);

            proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            // 取消击杀挂同步回调（理由见 RunCmdAsync 同款注释）：关窗丢续延时
            // 不能把孤儿 npm 留在后台。
            using var killOnCancel = queryCts.Token.Register(() =>
            {
                try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            // 与 RunCmdAsync 同一套纪律（理由见那里的注释，此处此前是反面教材）：
            // ① stderr 必须读掉——npm 往里写多了管道写满，子进程自己会卡死；
            // ② 不给 ReadToEndAsync 传 token——取消后剩余数据没人读；
            // ③ 超时/取消必须 Kill 整棵树——只 Dispose 会留下后台挂着的孤儿 npm。
            outTask = proc.StandardOutput.ReadToEndAsync();
            errTask = proc.StandardError.ReadToEndAsync();
            try { await proc.WaitForExitAsync(queryCts.Token); }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }

            // 进程退出了，管道**不一定**读完：孙进程若继承了 stdout 句柄且自己不退出，
            // ReadToEndAsync 会一直挂着——裸 await 会让「升级」永久 busy。版本查不出来
            // 是可接受的降级（保持当前引擎），界面卡死不是。与 GetToolVersionAsync
            // 已确立的排干纪律同一条（那里连更简单的 node --version 都防了）。
            var drained = await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(DrainTimeoutMs));
            if (drained is not Task<string[]> done)
            {
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }
            string stdout, stderr;
            try { stdout = done.Result[0]; stderr = done.Result[1]; }
            catch { return null; }
            // stderr 只进日志、绝不参与取版本（见 ParseNpmVersionOutput）。
            if (stderr.Trim().Length > 0) AppendStartupLog($"查询 {EnginePackageName} 版本时 npm 写到 stderr：{stderr.Trim()}");
            int exitCode;
            try { exitCode = proc.ExitCode; }
            catch { return null; }
            return ParseNpmVersionOutput(stdout, exitCode);
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
    /// 从 `npm view &lt;pkg&gt; version` 的结果里取版本号（纯函数、可单测）。
    /// 拆出来的理由与 <see cref="MatchesHarnessCommand"/> 同源：这个值会一路流到
    /// 「停掉正在跑的引擎 → 装 staging → 替换正式目录」——判错的后果不是报错，
    /// 是引擎被停掉、替换失败、界面报一句驴唇不对马马的错。原实现把 stdout 与
    /// stderr 拼起来取末行，于是三条独立的路都能把垃圾喂进去：
    ///
    /// ① **不看退出码**：npm 失败时 stdout 也可能有内容（错误摘要、缓存回显），
    ///    它会被当成"最新版"一路带回。
    /// ② **混入 stderr**：`npm WARN config registry ...`、`npm ERR! code E404`
    ///    都走 stderr。查询成功时，末尾一条 warn 就足以把真正的版本号顶掉。
    /// ③ **不校验形态**：末行是什么就用什么，直到 InstallEngineAsync 的
    ///    IsSafeVersionToken 才失败——而那已经在"引擎已停"之后了。
    ///
    /// 现在三道闸全在这一处：退出码必须为 0，**只认 stdout**，取到的末行必须
    /// 是 semver 形态。任何一条不满足就返回 null，调用方走"保持当前引擎"的降级路，
    /// 精心设计的那条降级路径才真正可达。
    /// </summary>
    internal static string? ParseNpmVersionOutput(string stdout, int exitCode)
    {
        if (exitCode != 0) return null;                       // ① 查询失败，不是"查到了"
        var text = (stdout ?? string.Empty).Trim();           // ② stderr 根本不进来
        if (text.Length == 0) return null;
        var last = text.Split('\n').Last().Trim();
        return NpmVersionLineRegex.IsMatch(last) ? last : null;  // ③ 形态闸
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
        EnterBusy(upgradeButton, "升级中", cts);
        // swapped：引擎目录是否已经被换成新版本（catch 里据此说人话，见那里的三态）。
        // installed：换上去的那个版本号，用于失败提示里点名到底是哪个版本在盘上。
        var swapped = false;
        var installed = "";
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
                    MessageBox.Show(this,
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
            // 纵深防御，且必须在"停引擎"之前：ParseNpmVersionOutput 已经只放行 semver，
            // 这里再卡一道 IsSafeVersionToken，是为了让**任何**取版本路径的异常都无法把
            // 垃圾值带到 StopHarnessProcessesAsync 之后才失败——那时引擎已停、无法自愈。
            // 有了它，下面那句"保持当前引擎"才真的是一条可达的降级路。
            if (!IsSafeVersionToken(latest))
            {
                AppendStartupLog($"拒绝升级：查到的“最新版本”不是合法版本 token（{latest}）");
                SetInfo("查到的版本号不合法，已中止升级，保持当前引擎");
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

                // 弹窗挂到主窗体上（与"引擎已锁定"、ShowError 同一条纪律）：这个弹窗
                // 出现在最长可达 60 秒的 npm 查询之后，恰是最容易"用户已切走窗口"的
                // 时点——无属主的框可能被压到别的窗口后面，表现为"点了没反应"。
                var go = MessageBox.Show(this, lines.ToString(), "升级可能影响插件",
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
            // 放后台线程：与 RunStartAsync 的快照同一条纪律。
            await Task.Run(() => ConfigBackup.CreateSnapshot($"升级引擎前（{current ?? "未安装"} → {latest}）"));
            await InstallEngineAsync(node, latest, await ResolveNpmRegistryAsync(node, cts.Token), cts.Token, current);
            if (cts.IsCancellationRequested) return;

            // 替换已经落地：引擎目录此刻**装的就是新版本**。记下来给 catch 用——
            // 此后任何异常（含紧接着的启动失败）都不能再说"当前引擎未被改动"。
            swapped = true;
            installed = ReadEngineVersion(engineDir) ?? latest;

            SetInfo($"引擎已升级到 {installed}，正在重启…");
            await StartHarnessAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!closing && !IsDisposed)
            {
                // 尾部提示必须区分**三态**，不能只看 Directory.Exists：
                //
                // ① 还没替换（swapped == false）：当前引擎确实原封未动。
                // ② 替换成功、重启失败：磁盘上**已经是新版本**了。此前这里按
                //    Directory.Exists 判成"未被改动，仍可正常使用"——那是一句明确的假话：
                //    用户据此以为一切照旧，而新引擎会在下次启动静默生效；真出问题了他
                //    也想不到该用「版本管理」切回去。
                // ③ 引擎目录不存在：替换与回滚都失败（engine.old 也搬不回来）。
                var tail = !swapped
                    ? "当前引擎未被改动，仍可正常使用。"
                    : Directory.Exists(engineDir)
                        ? $"⚠ 引擎已替换为 {installed}，但本次启动失败——**当前引擎已被改动**。\n" +
                          "新引擎已在盘上，下次启动会直接使用它；当前引擎仍可正常使用。\n" +
                          "若新版本不合用，用「版本管理」可切回上一版本（替换前的版本被保留为 engine.old）。"
                        : "⚠ 引擎目录当前不存在（替换与回滚都没成功）。" +
                          "下次启动会自动重新安装，约 214 MB、1–2 分钟；历史对话在盘上，不会丢。";
                ShowError("引擎升级失败", ex.Message + "\n\n" + tail);
            }
        }
        finally
        {
            // 与 RunStartAsync 同一纪律：CTS 由创建它的操作负责释放；
            // EndBusy 判 busyOwner（取消会清空 startCts，拿它判就会漏掉复位）。
            if (ReferenceEquals(startCts, cts)) startCts = null;
            if (ReferenceEquals(busyOwner, cts))
            {
                busyOwner = null;
                EndBusy();
            }
            try { cts.Dispose(); } catch { }
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
        // AttributesToSkip 必须含 ReparsePoint，理由与 DirectorySizeBytes 同源（见那里）：
        // ① 指向引擎目录**外部**的 junction 会让这里改掉**外部文件**的只读属性——
        //    用户从没要求我们碰那些文件，而"清只读"是一个带副作用的写操作；
        // ② 环形 junction 让 AllDirectories 永不终止，挂在后台线程上直到进程结束。
        // ForceDeleteDirectory 的下一步是 Directory.Delete(recursive)——**递归删除会跟随
        // junction 吗？**NTFS 上删除目录联接点只删链接本身，不删目标，所以真正的暴露面
        // 正是这里这一步（它会跟着链接走到目标）。跳过 ReparsePoint 后，被清只读的
        // 永远只是这份引擎目录里真正属于它的文件。
        foreach (var file in Directory.EnumerateFiles(
                     dir, "*",
                     new System.IO.EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                         IgnoreInaccessible = true,
                     }))
        {
            try
            {
                var attr = File.GetAttributes(file);
                if (attr.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
            catch { }
        }
    }
}
