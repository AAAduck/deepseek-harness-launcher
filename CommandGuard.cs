namespace DeepSeekHarness;

/// <summary>
/// 要被拼进 <c>cmd /d /s /c "…"</c> 的外部输入的唯一闸门：工具路径与 npm 配置值。
/// **纯函数、可单测**——独立成非 UI 的静态类是"纯函数住在纯类型里"的同一纪律
/// （见 <see cref="Semver"/> 头部说明）。
///
/// 这里每一条排除项被"顺手整理"掉一个，编译零反馈——由测试兜住
/// （IsSafeNpmValueTests / RegressionGuardTests）。
/// </summary>
internal static class CommandGuard
{
    /// <summary>
    /// 工具路径的纵深防御护栏：能被拼进 cmd 命令行的路径必须
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
    internal static string GuardToolPath(string? path, string toolName)
    {
        if (path is null) return null!;
        if (IsSafeToolPath(path)) return path;
        throw new InvalidOperationException(
            $"{toolName} 的路径里含有 cmd 会展开的字符（% 或引号），已中止：\n{path}\n" +
            "这类路径通常来自 PATH 里未展开的 %变量%。请把 %变量% 改成实际路径后重试。");
    }

    /// <summary>
    /// 需要拼进 cmd 命令行的值必须校验：含引号/空白/换行都可能把命令行拆坏。
    /// registry 是用户可改的 npm 配置，不能无条件信任。
    /// 它的输出被原样拼进 <c>cmd /d /s /c "npm … --registry &lt;这里&gt;"</c>
    /// （InstallEngineAsync / GetLatestEngineVersionAsync），是"用户可改的 npm 配置
    /// 拆坏/注入命令行"的唯一闸门；README 明文承诺了"含引号或空白的值不采用"。
    /// </summary>
    internal static bool IsSafeNpmValue(string value) =>
        value.Length is > 0 and < 512 &&
        !value.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '&' or '|' or '<' or '>' or '^' or '%');

    /// <summary>
    /// 拼进 cmd 命令行**之前**再核一次 registry。闸门必须在拼接点：
    /// 那个字符串要被塞进 <c>cmd /d /s /c "npm … --registry &lt;这里&gt;"</c>，
    /// 一个引号或 <c>&amp;</c> 就足以把命令行拆坏、甚至注入出第二条命令。
    /// 靠"三个调用方都记得先校验"是纪律不是结构——将来多一个调用方就静默失守。
    /// 抛而不返回 false：registry 来自用户的 ~/.npmrc，不安全就不该继续装/查。
    /// </summary>
    internal static string GuardRegistryForCommandLine(string registry)
    {
        if (!string.IsNullOrEmpty(registry) && IsSafeNpmValue(registry)) return registry;
        throw new InvalidOperationException(
            $"npm registry 的值不能安全地拼进命令行，已中止：{registry}\n" +
            "它含引号、空白或 shell 元字符。请修正 ~/.npmrc 里的 registry 配置。");
    }
}
