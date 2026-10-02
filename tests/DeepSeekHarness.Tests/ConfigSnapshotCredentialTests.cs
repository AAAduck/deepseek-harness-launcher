using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 凭据快照加密判定的单测（<see cref="ConfigBackup.CopyCredentialIntoSnapshot"/>）。
///
/// 为什么必须有：这条判定错了**不报错**——异常被逐文件 catch 收进 failedCopies，
/// 界面上只剩一句"快照已完成"。而它真的错过一次：条件写成与**源文件**状态比较
/// （alreadyEncrypted != dst 是否密文）后，真值表两个方向全反——
///   • 明文源 + DPAPI 正常（常态）→ 必然抛假异常 → 凭据永远进不了 manifest →
///     NeedsSnapshot 每轮按"键缺席"判要拍 → 8 个名额被无差别轮换；
///   • DPAPI 真失效 → 明文落进快照，文件名还叫 .dpapi。
/// 测试套件当时没有任何用例覆盖凭据分支，所以全绿通过。本文件把"明文源 + DPAPI
/// 正常"这个此前从被测过的分支钉死。
///
/// 端到端走 CreateSnapshot 本体做不了：DshHome/BackupRoot 是按当前用户环境计算的
/// 静态属性，测试重定向不进去——所以测被拆出来的这个可注入路径的内核。
/// </summary>
public class ConfigSnapshotCredentialTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "dsh-credential-test-" + Guid.NewGuid().ToString("N")[..10]);
    private string Dst => Path.Combine(root, "snapshot.credentials.yaml.dpapi");

    public ConfigSnapshotCredentialTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 明文源加DPAPI正常_快照里必须是密文_且不抛()
    {
        // 这正是此前必然抛假异常、导致凭据从进不了快照的**常态路径**。
        var plain = "providers:\n  deepseek:\n    apiKey: sk-test-123\n";

        var ex = Record.Exception(() => ConfigBackup.CopyCredentialIntoSnapshot(plain, Dst));

        Assert.Null(ex);
        Assert.True(DpapiFile.IsEncryptedFile(Dst), "快照里落明文凭据是本类最不能犯的错");
    }

    [Fact]
    public void 快照里的密文能原样读回()
    {
        var plain = "token: abc-def-ghi\n";

        ConfigBackup.CopyCredentialIntoSnapshot(plain, Dst);

        // 恢复路径走的就是 DpapiFile.ReadAllText：读得回原文才算真的可还原。
        Assert.Equal(plain, DpapiFile.ReadAllText(Dst));
    }

    [Fact]
    public void 已是密文的源再入快照_往返仍无损()
    {
        // 用户手工放进 .credentials.yaml 的旧版密文会被再包一层；
        // 恢复时解开一层得到的必须是原始密文串，写回 .credentials.yaml 后
        // 仍是合法的可解密文件——不支持这个形状，升级前的旧快照会全部失效。
        var inner = "token: legacy\n";

        ConfigBackup.CopyCredentialIntoSnapshot(inner, Dst);          // 第一层：模拟"已是 dpapi 串"
        var firstCipher = File.ReadAllText(Dst);
        var dst2 = Path.Combine(root, "twice.credentials.yaml.dpapi");
        ConfigBackup.CopyCredentialIntoSnapshot(firstCipher, dst2);   // 第二层：快照再包一层

        var restored = DpapiFile.ReadAllText(dst2);
        Assert.Equal(firstCipher, restored);
        // 解开一层后写回 .credentials.yaml，再按正常读取解开——两层都要能走通。
        var homeFile = Path.Combine(root, ".credentials.yaml");
        File.WriteAllText(homeFile, restored!);
        Assert.Equal(inner, DpapiFile.ReadAllText(homeFile));
    }
}
