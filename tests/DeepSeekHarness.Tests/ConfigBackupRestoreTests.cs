using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// <see cref="ConfigBackup.RestoreInto"/> 的端到端测试。
///
/// 为什么必须有：这是**全套件唯一一条"覆盖写用户配置"的路径**，数据安全权重最高，
/// 而它在拆出 RestoreInto 之前硬编码了 $DSH_HOME，于是**一条测试都没有**——
/// "IsWithinRoot 守卫测得很充分"与"这条路径本身没被测过"是两件事：
/// 守卫可以被单独调用，而守卫**之外**的一切（相对路径怎么算、快照里的文件名怎么落到
/// $DSH_HOME、backup-info.txt 与 .tmp 会不会被当配置盖进去、失败怎么记账、
/// 凭据密文能不能解开还原）全靠人肉保证。
///
/// 全部用临时目录，**绝不碰开发者自己的 ~/.dsh**——这正是拆出 RestoreInto 的理由。
/// </summary>
public class ConfigBackupRestoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "dsh-restore-test-" + Guid.NewGuid().ToString("N")[..10]);
    private string Snapshot => Path.Combine(root, "snapshot");
    private string Home => Path.Combine(root, "dsh-home");

    public ConfigBackupRestoreTests()
    {
        Directory.CreateDirectory(Snapshot);
        Directory.CreateDirectory(Home);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void PutInSnapshot(string relative, string content)
    {
        var full = Path.Combine(Snapshot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
    }

    private string ReadFromHome(string relative) =>
        File.ReadAllText(Path.Combine(Home, relative));

    private bool HomeHas(string relative) => File.Exists(Path.Combine(Home, relative));

    [Fact]
    public void 快照里的文件按相同相对路径覆盖回去()
    {
        PutInSnapshot("settings.yaml", "new-settings");
        PutInSnapshot(Path.Combine("profiles", "web", "cordis.patch.yml"), "new-patch");

        var result = ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.Equal(2, result.Restored);
        Assert.False(result.AnyFailed);
        Assert.Equal("new-settings", ReadFromHome("settings.yaml"));
        Assert.Equal("new-patch", ReadFromHome(Path.Combine("profiles", "web", "cordis.patch.yml")));
    }

    [Fact]
    public void 覆盖而不是新增_目标已有的内容被替换()
    {
        File.WriteAllText(Path.Combine(Home, "settings.yaml"), "old");
        PutInSnapshot("settings.yaml", "new");

        ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.Equal("new", ReadFromHome("settings.yaml"));
    }

    [Fact]
    public void backup_info不该被盖进配置目录()
    {
        // 它是给人看的说明文件（"怎么恢复""别外传"），不是配置。
        // 盖进 $DSH_HOME 会在引擎的配置目录里多出一份没人读的说明，
        // 还会被下一次 CreateSnapshot 当成新的备份内容处理。
        PutInSnapshot("backup-info.txt", "时间: ...\n注意: 勿外传\n");
        PutInSnapshot("settings.yaml", "x");

        var result = ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.Equal(1, result.Restored);
        Assert.False(HomeHas("backup-info.txt"));
    }

    [Fact]
    public void tmp残骸不该被盖进配置目录()
    {
        // 写入中途被杀留下的产物。盖进去只会多一份垃圾，
        // 而 ".credentials.yaml.tmp" 这类名字还会被按前缀匹配的读取方捡到。
        PutInSnapshot("settings.yaml.tmp", "half-written");
        PutInSnapshot(".credentials.yaml.tmp", "half-written");
        PutInSnapshot("settings.yaml", "x");

        var result = ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.Equal(1, result.Restored);
        Assert.False(HomeHas("settings.yaml.tmp"));
        Assert.False(HomeHas(".credentials.yaml.tmp"));
    }

    [Fact]
    public void 快照不存在时如实报出来且不写任何东西()
    {
        var before = Directory.GetFileSystemEntries(Home).Length;

        var result = ConfigBackup.RestoreInto(Path.Combine(root, "nope"), Home);

        Assert.Equal(0, result.Restored);
        Assert.True(result.AnyFailed);
        Assert.Contains(result.Failed, f => f.Contains("快照目录不存在"));
        Assert.Equal(before, Directory.GetFileSystemEntries(Home).Length);
    }

    [Fact]
    public void 目标根不是完整路径时拒绝_绝不按相对路径写()
    {
        // USERPROFILE 为空时 DshHome 就是 ".dsh"，GetFullPath 会把它落到当前
        // 工作目录——于是"恢复成功"却写到了别处，界面还报成功数。
        PutInSnapshot("settings.yaml", "x");

        var result = ConfigBackup.RestoreInto(Snapshot, ".dsh");

        Assert.Equal(0, result.Restored);
        Assert.Contains(result.Failed, f => f.Contains("USERPROFILE"));
    }

    [Fact]
    public void 凭据密文会被解开并还原成不带后缀的凭据文件()
    {
        // 快照里存的是 .credentials.yaml.dpapi（DPAPI），落回 $DSH_HOME 时
        // 必须去掉 .dpapi 后缀——否则引擎拿一份没人读的文件当凭据，
        // 表现为"恢复了却登不上"。
        const string plain = "providers:\n  openai:\n    apiKey: sk-secret\n";
        DpapiFile.WriteAllText(Path.Combine(Snapshot, ".credentials.yaml.dpapi"), plain);
        PutInSnapshot("settings.yaml", "x");

        var result = ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.Equal(2, result.Restored);
        Assert.False(result.AnyFailed);
        Assert.True(HomeHas(".credentials.yaml"), "凭据应还原成 .credentials.yaml");
        Assert.False(HomeHas(".credentials.yaml.dpapi"), "不该把密文原名盖进 $DSH_HOME");
        Assert.Equal(plain, ReadFromHome(".credentials.yaml"));
    }

    [Fact]
    public void 解不开的凭据跳过并记账_绝不盖一份读不出来的东西()
    {
        // 快照由另一个 Windows 用户创建时，DPAPI 解不开（Unprotect 抛）。
        // 静默盖一份解不开的东西进 $DSH_HOME 之后，引擎会一直认证失败，
        // 而用户看到的却是"已恢复 N 个文件"。
        // 必须是"带 dpapi 头但密文坏掉"的形状：**不带**头的话会被当成旧版明文
        // 凭据走升级路径（那是刻意保留的向后兼容，见下一条用例）。
        PutInSnapshot(".credentials.yaml.dpapi", "dpapi:这不是合法的base64密文@@@@");
        PutInSnapshot("settings.yaml", "x");

        var result = ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.Equal(1, result.Restored);              // 只有 settings.yaml
        Assert.True(result.AnyFailed);
        Assert.Contains(result.Failed, f => f.Contains("无法解密"));
        Assert.False(HomeHas(".credentials.yaml"), "解不开时绝不能动既有凭据");
    }

    [Fact]
    public void 旧版明文凭据快照仍能还原_向后兼容不能被加密那次改动打断()
    {
        // 加密是后来才加的，早几版的快照里躺的是**明文** .credentials.yaml.dpapi。
        // DpapiFile.ReadAllText 认得出它不是密文、走"旧版明文"分支原样返回，
        // 于是这份快照仍然可用。钉住它——将来若有人"顺手"把明文一律当损坏拒掉，
        // 所有升级前拍下的快照会同时失效，而用户完全不知道自己丢了什么。
        PutInSnapshot(".credentials.yaml.dpapi", "providers:\n  legacy:\n    apiKey: sk-old\n");
        PutInSnapshot("settings.yaml", "x");

        var result = ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.Equal(2, result.Restored);
        Assert.False(result.AnyFailed);
        Assert.Contains("legacy", ReadFromHome(".credentials.yaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void 单个文件失败不影响其余文件_且必须记账()
    {
        // 最常见的失败是"引擎正开着、占着这些文件"。
        // 逐文件 catch 继续是对的，但必须把失败项交出去——只报成功数的话，
        // 用户看到"已恢复 3 个文件"而 $DSH_HOME 停在新旧混合态，比整体失败更难排查。
        PutInSnapshot("settings.yaml", "new");
        PutInSnapshot("settings.yaml.imported", "imported");
        // 造一个写不进去的目标：**目录**占住文件 blocked\x 的落点。此前只建了父目录
        // Home\blocked——写入照样成功，失败路径从未被走进过，这条测试等于空转；
        // 且当时对记账零断言，把 RestoreInto 的失败清单整段删掉它照样绿。
        // AtomicWrite 的最后一步 Move 会落在已存在的**目录**上抛 IOException →
        // 记账分支被真实走进。
        Directory.CreateDirectory(Path.Combine(Home, "blocked", "x"));
        PutInSnapshot(Path.Combine("blocked", "x"), "x");

        var result = ConfigBackup.RestoreInto(Snapshot, Home);

        Assert.True(result.Restored >= 1);
        Assert.True(result.AnyFailed, "失败必须记账：只报成功数会把新旧混合态说成已恢复");
        Assert.Contains(result.Failed, f => f.Contains("blocked"));
        Assert.Equal("new", ReadFromHome("settings.yaml"));
        Assert.Equal("imported", ReadFromHome("settings.yaml.imported"));
    }

    [Fact]
    public void 恢复是原子覆盖_不留半截文件()
    {
        PutInSnapshot("settings.yaml", "brand-new-content");

        ConfigBackup.RestoreInto(Snapshot, Home);

        // 原子写的意义就在这里：目标要么是旧内容、要么是新内容，
        // 不可能是"半个 settings.yaml"——那正是备份机制本来要防的损坏。
        var text = ReadFromHome("settings.yaml");
        Assert.Equal("brand-new-content", text);
        Assert.False(HomeHas("settings.yaml.tmp"), "临时文件不该留在配置目录里");
    }
}