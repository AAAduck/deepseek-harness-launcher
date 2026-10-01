using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// link: 依赖目标解析的单测（1.4.3）。
///
/// 此前断链预检把 link: 后的原始字符串直接塞给 Directory.Exists——相对路径按
/// **启动器的当前工作目录**解析，而 pnpm 的语义是以**包所在目录**（web profile）
/// 为基准。一个完全有效的 link:../plugin 会被判成断链，进而整轮插件更新被跳过：
/// 检测自己诱发了它要防的失败。这条口径必须钉住，否则下一个"顺手整理路径"的人
/// 会把它改回原样，且不会有任何报错。
/// </summary>
public class LinkTargetTests
{
    private const string Profile = @"C:\Users\me\.dsh\profiles\web";

    [Theory]
    [InlineData("link:../dsh-free-search")]
    [InlineData("link:../dsh-improved-inline-edit")]
    public void 相对目标以profile为基准_而不是进程当前工作目录(string spec)
    {
        var resolved = HarnessForm.ResolveLinkTarget(Profile, spec.Substring(5));
        // Path.Combine 保留 ".."，由 Exists/OS 在检查时解析——这里钉住的是"基准是
        // profile"这一语义本身：解析结果必须以 profile 开头。
        Assert.StartsWith(Profile, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(spec.Substring(5), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 当前目录形态的相对目标_落在profile之内()
    {
        var resolved = HarnessForm.ResolveLinkTarget(Profile, "./local-plugin");
        Assert.Equal(Path.Combine(Profile, "./local-plugin"), resolved);
    }

    [Fact]
    public void 绝对目标_原样检查_不再二次拼接()
    {
        var absolute = @"D:\yule\work\DSH插件\dsh-free-search";
        Assert.Equal(absolute, HarnessForm.ResolveLinkTarget(Profile, absolute));
    }
}
