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
    /// <summary>
    /// 直接引用生产的那一份，**不抄副本**。
    /// 此前这里硬编码了一个同内容的 HashSet，于是"生产把某个文件加进/移出易变集"
    /// 这类改动会让下面所有用例照样绿——正是本文件开头要防的那种静默漂移。
    /// </summary>
    private static readonly ISet<string> VolatileFiles = ConfigBackup.VolatileConfigFiles;

    private static Dictionary<string, string> Map(params (string Key, string Hash)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Hash, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void 易变文件清单没有悄悄变长()
    {
        // 凭据是唯一会随每次引擎启动被重写的文件（token 轮换）。一旦有人把别的
        // 文件也列进来，那些文件的例行改写就会开始消耗 8 个快照名额，
        // 真正需要留住的还原点反而被挤掉。这条用例让它必须先改这里再改生产。
        Assert.Equal(new[] { ".credentials.yaml" },
            VolatileFiles.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 首次运行没有清单_必须拍()
    {
        Assert.True(ConfigBackup.NeedsSnapshot(Map((".credentials.yaml", "a1")), Map(), VolatileFiles));
    }

    [Fact]
    public void 内容完全没变_不拍()
    {
        var now = Map((".credentials.yaml", "a1"), ("settings.yaml", "b1"));
        Assert.False(ConfigBackup.NeedsSnapshot(now, Map((".credentials.yaml", "a1"), ("settings.yaml", "b1")), VolatileFiles));
    }

    [Fact]
    public void 只有易变文件变了_不拍_否则例行token轮换会吃光快照名额()
    {
        Assert.False(ConfigBackup.NeedsSnapshot(
            Map((".credentials.yaml", "a2"), ("settings.yaml", "b1")),
            Map((".credentials.yaml", "a1"), ("settings.yaml", "b1")),
            VolatileFiles));
    }

    [Fact]
    public void 稳定文件内容变了_必须拍()
    {
        Assert.True(ConfigBackup.NeedsSnapshot(
            Map(("profiles/web/cordis.patch.yml", "c2")),
            Map(("profiles/web/cordis.patch.yml", "c1")),
            VolatileFiles));
    }

    [Fact]
    public void 少了稳定文件_必须拍()
    {
        Assert.True(ConfigBackup.NeedsSnapshot(
            Map(("settings.yaml", "b1")),
            Map(("settings.yaml", "b1"), ("profiles/web/cordis.patch.yml", "c1")),
            VolatileFiles));
    }

    [Fact]
    public void 稳定文件被全删_必须拍_这恰恰是最该留还原点的时刻()
    {
        // 上一条测的是"少了一个"。这里是"一个都不剩"：hashes 为空、previous 非空。
        // 纯比数量会判成"数量不同 → 要拍"，纯比内容会判成"都没变 → 不拍"，
        // 两个方向都可能错，必须单独钉住。
        Assert.True(ConfigBackup.NeedsSnapshot(Map(), Map(("settings.yaml", "b1")), VolatileFiles));
    }

    [Fact]
    public void 键的大小写不同_视为没变_否则同一份配置会被反复判定为变了()
    {
        // 生产用的是 OrdinalIgnoreCase（Windows 文件系统不区分大小写）。
        // 这里若判成"变了"，就会每次启动都多拍一份，内容一模一样。
        Assert.False(ConfigBackup.NeedsSnapshot(
            Map(("Settings.yaml", "b1")), Map(("settings.yaml", "b1")), VolatileFiles));
    }

    [Fact]
    public void 一增一减且数量相同_必须拍_曾经只比数量而漏掉()
    {
        // 换 profile / 改 patch 文件名就是这个形状：数量不变，纯比 count 会判成"没变"，
        // 于是这份变化的还原点根本不会留下——而这恰恰是最需要快照的那种变化。
        Assert.True(ConfigBackup.NeedsSnapshot(
            Map(("settings.yaml", "b1"), ("profiles/desktop/cordis.patch.yml", "d1")),
            Map(("settings.yaml", "b1"), ("profiles/web/cordis.patch.yml", "c1")),
            VolatileFiles));
    }

    [Fact]
    public void 某文件本轮缺席_必须拍_读取或复制失败的文件靠这条自动重试()
    {
        // CreateSnapshot 对"哈希没算出来 / 复制没成功"的文件的记账方式就是
        // "本轮 manifest 里它缺席"。上一轮有、本轮没有 → 键集合变化 → 必须拍，
        // 这样瞬时失败（文件被引擎短暂占用）才会在下一轮被自动重试，
        // 而不是靠占位符哈希被判成"永久变化"每轮重拍。
        Assert.True(ConfigBackup.NeedsSnapshot(
            Map(("settings.yaml", "b1")),
            Map(("settings.yaml", "b1"), (".credentials.yaml", "a1")),
            VolatileFiles));
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

    [Theory]
    // —— null/空串退化：守卫必须返回 false，不能抛 ——
    // 调用方（Restore）传入 null 的路径段时，守卫若抛 ArgumentNullException，
    // 会被外层 catch 吞成"读取快照目录失败"，把一条本该静默跳过的越界路径
    // 变成整个恢复动作的报错中止——方向反了。
    [InlineData(null, @"C:\Users\me\.dsh")]
    [InlineData(@"C:\Users\me\.dsh\settings.yaml", null)]
    [InlineData(null, null)]
    [InlineData("", @"C:\Users\me\.dsh")]
    [InlineData(@"C:\Users\me\.dsh\settings.yaml", "")]
    public void 恢复路径守卫_null或空串一律拒绝(string? candidate, string? root)
    {
        Assert.False(ConfigBackup.IsWithinRoot(candidate!, root!));
    }

    [Theory]
    // —— 未展开的 ".." 必须在守卫内部被规范化掉 ——
    // 原实现只比"前缀 + 第 N 位是分隔符"，这三条全都会判成 true。当时没出事，
    // 纯粹因为 Restore 恰好在调用前做了 Path.GetFullPath；而本守卫的文档
    // （以及 Restore 的注释）点名的正是"一个 ../ 就可能写到别处去"这个场景。
    [InlineData(@"C:\Users\me\.dsh\..\evil\x", @"C:\Users\me\.dsh", false)]
    [InlineData(@"C:\Users\me\.dsh\..\..\Windows\evil.dll", @"C:\Users\me\.dsh", false)]
    // 规范化后仍要落在 root 内：这样才能继续放行正常的子路径
    [InlineData(@"C:\Users\me\.dsh\sub\..\ok.yaml", @"C:\Users\me\.dsh", true)]
    [InlineData(@"C:\Users\me\.dsh\profiles\..\settings.yaml", @"C:\Users\me\.dsh", true)]
    // root 带尾分隔符 / root 是盘符根：这两种形状原先会被判成越界，
    // 也就是"恢复动作全量跳过"，同样是不报错的静默失败。
    [InlineData(@"C:\Users\me\.dsh\settings.yaml", @"C:\Users\me\.dsh\", true)]
    [InlineData(@"C:\Users\me\x.yaml", "C:\\", true)]
    // UNC（网络盘/重定向到文件服务器的 %USERPROFILE%）：TrimEnd 去掉尾分隔符之后
    // 前缀判断仍然必须成立，否则整个恢复动作会被静默跳过。
    [InlineData(@"\\server\share\profiles\web\cordis.patch.yml", @"\\server\share", true)]
    [InlineData(@"\\server\share\profiles\web\cordis.patch.yml", @"\\server\share\", true)]
    [InlineData(@"\\server\other\x.yaml", @"\\server\share", false)]
    // root 写成裸反斜杠或盘符相对时必须拒。绝对不能拿候选路径自己的盘符根去补：
    // 那等于把"必须在这个根里"变成"必须在任意盘里"，检查直接作废。
    [InlineData(@"D:\evil\x.yaml", @"\", false)]
    [InlineData(@"C:\evil\x.yaml", @"\\", false)]
    [InlineData(@"C:\evil\x.yaml", "C:", false)]        // 盘符相对：GetFullPath 会按 CWD 展开
    [InlineData(@"C:\evil\x.yaml", @"..\dsh", false)]  // 相对路径：同上
    public void 恢复路径守卫_规范化与边界形状(string candidate, string root, bool expected)
    {
        Assert.Equal(expected, ConfigBackup.IsWithinRoot(candidate, root));
    }
}
