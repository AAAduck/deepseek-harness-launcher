using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 版本号白名单的单测。
///
/// 它守的是三个入口：切换版本、删除版本槽、安装引擎时指定版本——三者都会把
/// 这个字符串拼进 <c>engine.&lt;版本&gt;</c> 之后交给 <c>ForceDeleteDirectory</c>
/// 或交给 npm。判松了的代价是**递归删到别处去**或拼出非法命令行。
///
/// 现实来源是本机目录名与 engine-version.txt，风险不高——但也正因为"出不来"，
/// 这条防线被简化掉时没人会发现，所以它比看起来更需要钉住。
/// </summary>
public class VersionTokenTests
{
    [Theory]
    [InlineData("0.1.5-rc.2")]
    [InlineData("latest")]
    [InlineData("1.2.3")]
    [InlineData("broken-20260930-145807")]
    public void 正常版本号放行(string version)
    {
        Assert.True(HarnessForm.IsSafeVersionToken(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空值一律拒绝(string? version)
    {
        Assert.False(HarnessForm.IsSafeVersionToken(version));
    }

    [Theory]
    [InlineData("..")]        // 拼成 engine... 会往上跳
    [InlineData("../evil")]
    [InlineData("a/b")]       // 含分隔符 → 目录会跑到 engine. 之外
    [InlineData(@"a\b")]
    [InlineData("a:b")]       // 冒号是非法文件名字符
    [InlineData("a|b")]
    [InlineData("a\"b")]
    public void 含路径或非法字符一律拒绝(string version)
    {
        Assert.False(HarnessForm.IsSafeVersionToken(version));
    }

    [Fact]
    public void 含空白一律拒绝_报错文案承诺的就是这一条()
    {
        // 空格不在 Path.GetInvalidFileNameChars 里，所以这条约束是被单独写出来的。
        // 删掉它，测试立刻红，而"版本指定不合法"的报错文案仍然在说"不能含空白"。
        Assert.False(HarnessForm.IsSafeVersionToken("0.1.5 rc.2"));
        Assert.False(HarnessForm.IsSafeVersionToken("0.1.5\trc.2"));
    }

    [Fact]
    public void 超长版本号拒绝_它会成为目录名的一部分()
    {
        Assert.False(HarnessForm.IsSafeVersionToken(new string('v', 65)));
        Assert.True(HarnessForm.IsSafeVersionToken(new string('v', 64)));
    }

    [Fact]
    public void 尾点一律拒绝_Windows建目录会剥尾点导致槽名失配()
    {
        // 尾点不在 Path.GetInvalidFileNameChars 里："engine.1.2.3." 会被 Windows
        // 静默建成 "engine.1.2.3"，之后按带尾点的版本名列举/删除永远找不到。
        // 与空格同属"合法但会被系统改写"的形状，删掉这条约束测试必须红。
        Assert.False(HarnessForm.IsSafeVersionToken("1.2.3."));
        Assert.False(HarnessForm.IsSafeVersionToken("latest."));
        // 中间有点是正常的（语义化版本本来就靠点分段），不能误伤。
        Assert.True(HarnessForm.IsSafeVersionToken("1.2.3"));
    }
}
