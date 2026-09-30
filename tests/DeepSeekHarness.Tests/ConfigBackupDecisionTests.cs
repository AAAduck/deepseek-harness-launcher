using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 快照判定与恢复路径守卫的单测。
///
/// 这两处是整个备份机制里仅有的"判错了不报错"的地方：
/// 少拍一份快照 → 用户真正需要还原时才发现没有；路径守卫放行 → 配置写到别处去。
/// </summary>
public class ConfigBackupDecisionTests
{
    private static readonly HashSet<string> Volatile =
        new(StringComparer.OrdinalIgnoreCase) { ".credentials.yaml" };

    private static Dictionary<string, string> Map(params (string Key, string Hash)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Hash, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void 首次运行没有清单_必须拍()
    {
        Assert.True(ConfigBackup.NeedsSnapshot(Map((".credentials.yaml", "a1")), Map(), Volatile));
    }

    [Fact]
    public void 内容完全没变_不拍()
    {
        var now = Map((".credentials.yaml", "a1"), ("settings.yaml", "b1"));
        Assert.False(ConfigBackup.NeedsSnapshot(now, Map((".credentials.yaml", "a1"), ("settings.yaml", "b1")), Volatile));
    }

    [Fact]
    public void 只有易变文件变了_不拍_否则例行token轮换会吃光快照名额()
    {
        Assert.False(ConfigBackup.NeedsSnapshot(
            Map((".credentials.yaml", "a2"), ("settings.yaml", "b1")),
            Map((".credentials.yaml", "a1"), ("settings.yaml", "b1")),
            Volatile));
    }

    [Fact]
    public void 稳定文件内容变了_必须拍()
    {
        Assert.True(ConfigBackup.NeedsSnapshot(
            Map(("profiles/web/cordis.patch.yml", "c2")),
            Map(("profiles/web/cordis.patch.yml", "c1")),
            Volatile));
    }

    [Fact]
    public void 少了稳定文件_必须拍()
    {
        Assert.True(ConfigBackup.NeedsSnapshot(
            Map(("settings.yaml", "b1")),
            Map(("settings.yaml", "b1"), ("profiles/web/cordis.patch.yml", "c1")),
            Volatile));
    }

    [Fact]
    public void 一增一减且数量相同_必须拍_曾经只比数量而漏掉()
    {
        // 换 profile / 改 patch 文件名就是这个形状：数量不变，纯比 count 会判成"没变"，
        // 于是这份变化的还原点根本不会留下——而这恰恰是最需要快照的那种变化。
        Assert.True(ConfigBackup.NeedsSnapshot(
            Map(("settings.yaml", "b1"), ("profiles/desktop/cordis.patch.yml", "d1")),
            Map(("settings.yaml", "b1"), ("profiles/web/cordis.patch.yml", "c1")),
            Volatile));
    }

    [Theory]
    [InlineData(@"C:\Users\me\.dsh\settings.yaml", @"C:\Users\me\.dsh", true)]
    [InlineData(@"C:\Users\me\.dsh\profiles\web\cordis.patch.yml", @"C:\Users\me\.dsh", true)]
    [InlineData(@"C:\Users\me\.dsh-evil\settings.yaml", @"C:\Users\me\.dsh", false)]  // 同前缀兄弟目录
    [InlineData(@"C:\Users\me\.dshevil", @"C:\Users\me\.dsh", false)]
    [InlineData(@"C:\Users\me\.dsh", @"C:\Users\me\.dsh", false)]                   // root 自己不算"内部"
    [InlineData(@"C:\Windows\System32\evil.dll", @"C:\Users\me\.dsh", false)]
    public void 恢复路径守卫(string candidate, string root, bool expected)
    {
        Assert.Equal(expected, ConfigBackup.IsWithinRoot(candidate, root));
    }
}
