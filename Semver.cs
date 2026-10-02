using System.Text.RegularExpressions;

namespace DeepSeekHarness;

/// <summary>
/// semver 判定与 npm 版本输出解析。**纯函数、可单测、不碰任何 UI/IO 静态状态**。
///
/// 为什么独立成非 UI 的静态类：主窗体的类型初始化器链着 GDI+ 字体与 LOCALAPPDATA
/// 解析（见 HarnessForm 对惰性化的说明），而这套判定必须能在无 GUI / 无
/// LOCALAPPDATA 的机器（Server Core、容器 CI）上被单测直接调用。"纯函数必须
/// 住在纯类型里"——此前挂在 HarnessForm 上靠惰性化绕开，本类把 semver 全家
/// 一次迁净，StaticCouplingTests 守住的前提由"恰好没耦合"变成"结构上不耦合"。
///
/// 语义纪律（勿动）：三态返回——true 满足 / false 明确不满足 / null 无法判定。
/// 每次"拿不准就返回 null"都是刻意选择（宁可提示"未能判定"，也不猜）；
/// 而这里每次在"漏报警方向"上出过的错（预发布无条件比较、裸版本号按 ≥ 解释、
/// 比较器集合级门槛缺失）都由 tests 钉住，改动前先跑套件。
/// </summary>
internal static class Semver
{
    /// <summary>
    /// `npm view &lt;pkg&gt; version` 的合法输出形态。npm 的 version 字段恒为 semver，
    /// 必以数字开头（1.2.3 / 1.2.3-rc.2 / 1.2.3+build 都过），
    /// 而 npm 自己的 warn / notice / ERR! 文本、以及任何错误摘要都过不了这道闸。
    /// ECMAScript：<c>\d</c> 只认 ASCII，避免全角数字混进来。
    /// </summary>
    private static readonly Regex NpmVersionLineRegex = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.\-+]*)?$",
        RegexOptions.Compiled | RegexOptions.ECMAScript);

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
        // 第四段以**输入**有没有为准（nums.Length 恒为 4，不能用它判断）。
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

        // 先比数字段，数字段相同才看预发布标识：0.1.5-rc.2 < 0.1.7-rc.1 靠的是 5 < 7，
        // 与 rc 无关。预发布只影响"数字段相同"时的排序。
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
    /// "&gt;=0.1.7-rc.1"、"^4.0.1"、"~1.2.3"、裸版本号（= 精确匹配，同 npm 语义）、"A || B"。
    /// 三态返回：true 满足 / false 明确不满足 / null 无法判定。
    ///
    /// 这里必须把"明确不满足"和"无法判定"分开——把"判不出"当"不满足"会凭空
    /// 报出插件不兼容（漏报警方向）。
    /// 规则（三值 Kleene：任一候选项满足 → true；全部明确不满足 → false；
    /// 只要还剩一个判不出来 → null）：
    ///   某个候选项的全部 token 都满足 → true；
    ///   某个候选项里有 token 明确不满足（且没有无法判定的 token 挡在前面）→ 该候选项为 false；
    ///   只要存在"无法判定"的候选项，整体就不能是 false——
    ///   例如 "&gt;=0.2.0 || *"，第一个候选项明确不满足、第二个（通配符）判不出来，
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
            // 空白候选项（形如 "&gt;=2.0.0 || "、或多个连续 "||" 留下的空段）：
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
                // SatisfiesSingle 逐 token 做：那会把 "&gt;=0.1.7-rc.1 &lt;0.3.0-0" 配
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
            // 基准是否带**预发布**必须问 VersionPrerelease，不能用 Contains('-')：
            // "1.2.3+b-1" 是带 build 的正式版，Contains('-') 会把它误判成预发布。
            // VersionPrerelease 先按 '+' 截掉 build 段再找 '-'，与 ParseVersion 的
            // core 截断口径一致——两处口径必须同形。
            if (VersionPrerelease(basis) is not null) return null;
            var v = ParseVersion(basis);
            var c = ParseVersion(candidate);
            if (v is null || c is null) return null;
            // npm 语义：caret/tilde 范围只接受与基准同 major.minor.patch 的预发布候选
            // （如 ^4.0.1 只放行 4.0.1-xxx）。不实施这条会让 0.1.5-rc.9 判满足 ^0.1.0
            // ——漏报警方向（本工具最不能犯的）。这里是**明确的 false**（npm 规则本身），
            // 与"拿不准返回 null"的取舍不冲突。
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

        // 裸版本号 = **精确匹配**（npm/semver 语义："1.2.3" 是"恰好这个版本"）。
        // 按 >= 解释会让引擎 0.9.0 判满足写死的 "0.1.5"——危险方向漏报。
        var bare = CompareVersionStrings(candidate, token);
        return bare is null ? null : bare == 0;
    }

    /// <summary>
    /// 预发布候选是否进得了以 (major, minor, patch) 为基准的范围 token。
    /// npm 规则：预发布候选只在"与某个比较对象共享同一 [major, minor, patch] 三元组"
    /// 时才被 caret/tilde 这类范围接受。返回 false = npm 语义下明确不满足（不是猜）。
    /// 非预发布候选与解析不了的候选不受此门槛限制，维持原有判定路径。
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
    /// 否则整项明确不满足（false，不是猜）：裸比较器（<c>&gt;=0.2.0</c>）的集合
    /// 不接受任何预发布候选（npm 语义，peer 装不上），只做数字比较会在漏报警方向出错。
    /// <paramref name="comparators"/> 是该候选项里全部"比较器形态 token"（含裸精确
    /// 版本）的基准三元组与预发布标记；caret/tilde 不参与：它们对预发布候选的门槛
    /// 已在自己分支内实施（<see cref="PrereleaseAllowedInRange"/>），基准带预发布时的
    /// 既定取舍是"返回 null"，轮不到这道门槛说话。
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
    /// 从 pnpm 的 .pnpm 存放区目录名里取出干净的版本号。纯函数、可单测。
    ///
    /// 两代命名法都要认：
    ///   <c>@deepseek-ai+dsh@0.1.5</c>            —— scoped 包，斜杠换加号
    ///   <c>dsh@1.2.3(react@18.3.1)</c>          —— 旧式 peer 变体
    ///   <c>dsh@1.2.3_react@18.3.1</c>          —— 新式 peer 变体
    ///
    /// 分隔版本号的那个 '@' 必须是**包名之后的第一处**：LastIndexOf 会在新式 peer
    /// 变体下取到 peer 的版本号，比出彻底无关的另一个包。
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
    /// 从 `npm view &lt;pkg&gt; version` 的结果里取版本号（纯函数、可单测）。
    /// 这个值会一路流到「停掉正在跑的引擎 → 装 staging → 替换正式目录」——判错的
    /// 后果不是报错，是引擎被停掉、替换失败、界面报一句驴唇不对马马的错。
    /// stdout/stderr 拼接取末行的旧写法有三条路能把垃圾喂进来：
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
}
