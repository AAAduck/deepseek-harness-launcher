namespace DeepSeekHarness;

/// <summary>
/// 引擎版本槽大小的人读格式。**纯函数、可单测**。
///
/// 独立成非 UI 的静态类只有一个理由：单测要调它，而 EngineVersionsForm 的类型
/// 初始化器会 new 三个 GDI+ Font——在无字体/无 GUI 的机器（Windows Server Core、
/// 容器 CI、精简版 Windows）上，一次纯字符串函数的调用就会红成一条与被测逻辑
/// 毫无关系的 TypeInitializationException。纯函数必须住在纯类型里，别为省一个
/// 文件把它挂回窗体上。
/// </summary>
internal static class HumanSize
{
    /// <summary>
    /// 原先一律 <c>N0</c>：不足 1 MB 的槽（半截安装、被删到一半的目录、
    /// 只剩元数据的槽）会显示成 "0 MB"——一个看起来像"这个版本是空的"、
    /// 实则只是没统计到的结论。1 MB 以下给一位小数、1 MB 以上才取整。
    /// 统计失败（枚举抛异常）时返回 0，此时显示"—"而不是 "0 MB"。
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        const double mb = 1024.0 * 1024.0;
        return bytes < mb
            ? $"{bytes / mb:0.0} MB"
            : $"{bytes / mb:N0} MB";
    }
}
