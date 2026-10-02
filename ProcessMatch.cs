using System.Globalization;
using System.Text.RegularExpressions;

namespace DeepSeekHarness;

/// <summary>
/// 进程匹配内核：杀谁、谁可能持锁。**纯函数、可单测、不读任何静态状态**——
/// 命令行与目录全部走参数。
///
/// 为什么独立成非 UI 的静态类："杀错进程"是本项目最贵的一个判断：会崩掉用户
/// 正在用的桌面客户端、会误杀别的登录会话的引擎，而它错了不会报错。这类逻辑
/// 必须被单测钉住，而钉住的前提是调用它不必初始化主窗体的 GDI+ 字体与
/// LOCALAPPDATA 解析（见 <see cref="Semver"/> 头部说明）——纯函数必须住在纯类型里。
///
/// 失手方向的既定纪律：**杀进程的两条判据宁可漏杀**（真残留占着端口有启动前的
/// 端口探测兜底，误杀没有）；<see cref="MayHoldProfileLock"/> 是唯一的例外——
/// 它漏认的代价是删掉活锁，因此方向刻意相反。
/// </summary>
internal static class ProcessMatch
{
    /// <summary>
    /// 命令行里"独立的数字 token"。端口收窄专用：必须**整体**等于本启动器的端口才算数。
    /// 前后都不允许紧邻数字、小数点或任何单词字符，于是 "30801" 被当成一个整体而不是
    /// "3080"，"3080x" 也不会被截成 "3080"。
    ///
    /// <b>ECMAScript 标志不能去掉</b>：.NET 默认的 <c>\w</c>/<c>\d</c> 含 Unicode——
    /// 实测 <c>\w</c> 匹配「的」、<c>\d</c> 匹配全角「０」。那会让"端口紧邻一个汉字"
    /// 被判成"没提端口"，也就是**漏杀真残留**：而这条收窄的失手方向不该是这个。
    /// ECMAScript 下两者退化为 ASCII，端口紧邻汉字照样命中（这才是想要的），
    /// 真正的分界仍然落在数字与字母上——30801、3080x 都会被放过。
    /// </summary>
    private static readonly Regex PortTokenRegex = new(
        @"(?<![\w.])[0-9]{1,5}(?![\w.])",
        RegexOptions.Compiled | RegexOptions.ECMAScript);

    /// <summary>
    /// 残留进程匹配的宽松正则（早期 npx / dsh.cmd 时代的残留兜底）。
    /// static readonly + Compiled：每个进程都要过一遍它们，内联字面量每次调用
    /// 都要重新解析模式。
    /// </summary>
    private static readonly Regex DshCommandRegex = new(
        @"(^|[\\/\s])dsh(?:\.cmd)?(?:[\\/](?:lib|bin))?\s+(?:web|--profile\s+web)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NpxDshCommandRegex = new(
        @"\bnpx(?:\.cmd)?\b.*\b(?:@deepseek-ai[\\/]dsh|dsh)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 引擎目录下 dsh 包的完整路径。命令行里出现它，进程才可能与本引擎有关——
    /// 这是比"引擎目录"更精确一档的锚点：引擎目录本身会被任何"以该目录下文件为
    /// 参数"的进程撞上（编辑器、node 跑用户自己放在引擎目录下的脚本……）。
    /// </summary>
    internal static string EnginePackageDirUnder(string engineDir) =>
        Path.Combine(engineDir, "node_modules", "@deepseek-ai", "dsh");

    /// <summary>
    /// <see cref="HarnessForm.IsHarnessCommand"/> 的纯函数内核：吃参数、不读任何静态状态。
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
        // 客户端宿主形如 "…\DeepSeek Harness.exe" --expose-internals
        //   …\resources\app.asar\dsh\…，其命令行同时含 `app.asar\dsh` 和
        // `\dsh\`+空白+web，宽泛正则会把它误判成残留 dsh web 进程并整树杀掉，
        // 用户正开着的客户端直接白屏崩溃（时间线见 DESIGN-NOTES.md §3）。
        // 启动器只需要管自己拉起的引擎，客户端的引擎归客户端管。
        if (c.Contains("app.asar", StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Contains("dsh-desktop-host", StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Contains("dsh-subprocess", StringComparison.OrdinalIgnoreCase)) return false;

        // 进程形态收窄：引擎及其包装层只可能是 node / cmd / npx（两条杀进程路径
        // 共用同一判定）。编辑器、资源管理器等任何其他进程名直接出局——匹配必须
        // 同时核对进程类型与实际引擎入口，不能仅凭目录子串（否则编辑器打开了
        // bin.js 也会被整树杀掉）。
        if (!IsEngineProcessShape(name)) return false;

        // 本启动器自己拉起的引擎：命令行里带着**引擎内的 dsh 包目录**——比"引擎
        // 目录"更精确一档的锚点（目录本身会被任何"以该目录下文件为参数"的进程
        // 撞上，见 IsEngineProcessShape 处的注释）。与退出清扫同一口径。
        if (c.Contains(EnginePackageDirUnder(engineDir), StringComparison.OrdinalIgnoreCase)) return true;

        // 宽松匹配只对 node / npx / cmd 生效，进程形态已由 IsEngineProcessShape
        // 统一收窄（此前这里各算一份 isNode/isNpx，两边迟早漂移）。

        // 宽松匹配只认**本用户**的残留（互斥体是 Local\ 每会话一个，管不到别的
        // 会话；不加限定会把别人会话的进程整树杀掉）。误杀别人进程的代价远大于
        // 漏杀一个残留——真残留占着端口有启动前报错兜底。
        //
        // **这道收窄必须待在这里、不能提到函数开头**：引擎包目录精确匹配不需要
        // userHomeDir（命令行带着自己的引擎目录本身就是确定性证据），而把
        // "userHomeDir 为空就返回 false"提前会让 USERPROFILE 缺失的机器连自己的
        // 引擎都杀不掉。空串也必须拒——放行空串等于把"归属不清就不动手"反转成
        // "放行所有用户"。段级比较而不是子串（Dan/Daniel 前缀事故见
        // DESIGN-NOTES.md §3；拼音用户名前缀极常见，不是边缘情形）。
        if (string.IsNullOrEmpty(userHomeDir) ||
            !ContainsPathSegment(c, userHomeDir)) return false;

        if (c.Contains("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("@deepseek-ai\\dsh", StringComparison.OrdinalIgnoreCase)) return true;

        // 两条宽松正则只为兜早期 npx / dsh.cmd 时代留下的残留。再收窄一道：
        // 命令行里必须出现**恰好等于**本启动器端口的独立数字 token——子串写法会把
        // "--port 30801" 算命中，用户自己跑在 30801 的 dsh 会被一次「停止」整树杀掉
        // （真实的误杀面，见 DESIGN-NOTES.md §3）。
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
    /// 从种子出发沿 ParentId 边收集整棵进程树（含种子）。**纯函数、可单测**——
    /// "杀哪些进程"是本项目最贵的判断（会崩掉用户正在用的桌面客户端），
    /// 而它错了不报错，必须被单测钉住。
    ///
    /// **边也要防伪**：已退出进程的 PID 被复用后，孤儿进程的 ParentId 指向引擎，
    /// 不校验创建时间就会把无关进程连同整棵子树误杀。真实子进程必然**晚于**父进程
    /// 创建，创建时间不晚于父进程的一律不收编；CreationDate 缺失（MinValue）的没有
    /// 对照依据，同样不收编——代价只是漏收一个残留，与"宁可漏杀"的信条同向。
    /// </summary>
    internal static HashSet<int> CollectProcessTreeIds(
        IReadOnlyCollection<int> seeds, IReadOnlyDictionary<int, ProcessRecord> records)
    {
        var all = new HashSet<int>(seeds);
        var queue = new Queue<int>(seeds);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!records.TryGetValue(parent, out var parentRecord)) continue;
            foreach (var child in records.Values
                         .Where(x => x.ParentId == parent && x.StartTime > parentRecord.StartTime)
                         .Select(x => x.Id))
                if (all.Add(child)) queue.Enqueue(child);
        }
        return all;
    }

    /// <summary>
    /// 两个"进程创建时间"读数是否指向同一个进程的启动。纯函数、可单测——
    /// 它守着两条杀进程路径（「停止」与关窗清扫），判错了不会报错。
    ///
    /// 为什么必须带容差而不是严格相等：两个读数精度不同（WMI 微秒 vs Process
    /// 100ns），同一个进程的两次读数恒差 0–0.9 µs——严格相等会把"同一个进程"
    /// 判成"PID 被复用"，「停止」大概率空转且不报错（时间线见 DESIGN-NOTES.md §3）。
    ///
    /// ⚠ 两个读数都是**本地挂钟时间**，比的是 Ticks 差、不涉及时区。别给 WMI 那侧
    /// 补 ToUniversalTime()——差值会变 8 小时量级，对每个真正的目标都返回 false。
    /// 容差 1ms：比最大读数差大三个数量级；PID 复用后新进程的创建时间必然相差
    /// 秒级以上，防护语义不变。
    /// </summary>
    internal static bool IsSameProcessStart(DateTime a, DateTime b) =>
        Math.Abs((a - b).Ticks) <= TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// 引擎进程的镜像名形态：node（引擎本体）、cmd（stdio 重定向到 engine-stdio.log
    /// 的包装层）、npx（早期残留的启动方式）。其余进程名——编辑器、资源管理器、任何
    /// GUI 工具——即便命令行里带着引擎目录下的文件路径（用户用编辑器打开了 bin.js
    /// 是最现实的形状），也不是引擎，绝不能进杀进程名单。两条杀进程路径共用本判定。
    /// </summary>
    internal static bool IsEngineProcessShape(string name) =>
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
    internal static bool IsElectronProcessShape(string name) =>
        name.StartsWith("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("electron.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个进程**可能**正持有 ~/.dsh/profiles/node_modules.lock（纯函数、可单测）。
    ///
    /// 为什么不能借用 <see cref="HarnessForm.IsHarnessCommand"/>：那条判据为了"不误杀"而明确排除了
    /// 桌面客户端（app.asar / dsh-desktop-host / dsh-subprocess 一律出局）。
    /// 而这把锁**恰恰就是桌面客户端的引擎在引导时持有的**——客户端与本启动器共用同一个
    /// %USERPROFILE%\.dsh\profiles。客户端正持锁引导时用户双击启动本启动器，
    /// 借来的判据看不到任何"活着的引擎" → 活锁被删 → 两个引擎并发写同一份 profile，
    /// 正是这把锁要防的踩踏。
    ///
    /// 两条判据的方向因此相反，这里也是：
    ///   • 杀进程判据（MatchesHarnessCommand / MatchesEngineProcess）宁可漏杀（杀错不可逆）；
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
        // 只认目录子串的话，编辑器以引擎目录下文件为参数同样命中、会被整树带走。
        if (!IsEngineProcessShape(name)) return false;
        return commandLine.Contains(EnginePackageDirUnder(engineDir), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// WMI 快照里的一条进程记录。**顶层类型而非嵌套**：它是 <see cref="ProcessMatch"/>
/// 各内核的输入形状，测试与快照读取方都要直接构造它，挂在窗体类型上没有意义。
/// </summary>
internal sealed record ProcessRecord(int Id, int ParentId, string Name, string CommandLine, DateTime StartTime);
