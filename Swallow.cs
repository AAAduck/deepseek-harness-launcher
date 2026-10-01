using System.Collections.Concurrent;

namespace DeepSeekHarness;

/// <summary>
/// 统一吞异常的日志帮助函数。
///
/// 全项目有约 60 处 <c>catch { }</c>，集中在后台循环、UI 兜底、进程清理上。
/// 高频路径（1.5 秒刷新、250 ms tail）保持静默——写日志反而变成噪音。
/// 但低频关键路径（快照、恢复、归档、锁清理）至少该留一条痕迹：
/// 磁盘满、权限被撤这类系统性问题不会高频重复，一旦静默就彻底无信号。
///
/// 去重机制：同一 <paramref name="context"/> 在同进程生命周期内，每小时只写一次
/// startup-log，避免同一个异常源把日志刷爆。
/// </summary>
internal static class Swallow
{
    private static readonly ConcurrentDictionary<string, DateTime> LastLogged = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

    /// <summary>静默吞掉异常，低频关键路径在首次发生时记一笔 startup-log。</summary>
    internal static void Quiet(Exception ex, string context)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (LastLogged.TryGetValue(context, out var last) && now - last < Cooldown)
                return;
            LastLogged[context] = now;
            HarnessForm.AppendStartupLog($"[{context}] {ex.GetType().Name}: {ex.Message}");
        }
        catch { }
    }

    // Silent()（高频路径零开销占位）已删除：从无调用点。高频路径保持普通 catch { }
    // 即可——那本身就是零开销的写法，不需要再包一层函数调用。
}
