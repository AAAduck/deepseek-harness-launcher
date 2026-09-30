using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// semver 范围判定的单测。
///
/// 这些函数只有一种错误方式：不报编译错、不报运行错，只是在「升级引擎」前
/// 那个"插件会失效"的确认框里给出**错的结论**。而一旦判错，用户要么白升级一次
/// 引擎（插件静默失效），要么被无谓的警告劝退。
///
/// 三态约定（null = 无法判定）同样重要：护栏宁可说"我判不出来"，也不能猜。
/// </summary>
public class SemverRangeTests
{
    [Theory]
    // —— 裸版本号：npm 语义是**精确匹配**，不是"至少" ——
    [InlineData("0.9.0", "0.1.5", false)]      // 修好的关键回归：曾按 >= 解释，护栏在这里漏报
    [InlineData("0.1.5", "0.1.5", true)]
    [InlineData("0.1.6", "0.1.5", false)]
    [InlineData("0.1.5-rc.1", "0.1.5", false)] // 预发布 < 正式版
    [InlineData("0.1.5", "0.1.5-rc.1", false)] // npm 同语义：预发布范围不含正式版
    [InlineData("0.1.5-rc.1", "0.1.5-rc.1", true)]
    public void 裸版本号按精确匹配(string candidate, string range, bool expected)
    {
        Assert.Equal(expected, HarnessForm.SatisfiesRange(candidate, range));
    }

    [Theory]
    [InlineData("0.2.0", ">=0.1.7-rc.1", true)]
    [InlineData("0.1.7", ">=0.1.7-rc.1", true)]
    [InlineData("0.1.5", ">=0.1.7-rc.1", false)]
    [InlineData("0.1.7-rc.1", ">=0.1.7-rc.1", true)]
    [InlineData("0.1.7-rc.2", ">=0.1.7-rc.1", null)] // 同为预发布、标识不同 → 不猜
    [InlineData("0.1.0", ">=0.1.7-rc.1 <0.3.0-0", false)]
    [InlineData("0.2.0", ">=0.1.7-rc.1 <0.3.0-0", true)]
    public void 比较运算符与区间(string candidate, string range, bool? expected)
    {
        Assert.Equal(expected, HarnessForm.SatisfiesRange(candidate, range));
    }

    [Theory]
    [InlineData("0.1.5", "~0.1.5", true)]
    [InlineData("0.1.9", "~0.1.5", true)]      // 同 major+minor 的补丁升级
    [InlineData("0.2.0", "~0.1.5", false)]     // 越过 minor 即出界
    [InlineData("0.1.4", "~0.1.5", false)]
    [InlineData("0.1.5", "~0.1", null)]         // 两段基准：拿不准就不判
    public void tilde范围(string candidate, string range, bool? expected)
    {
        Assert.Equal(expected, HarnessForm.SatisfiesRange(candidate, range));
    }

    [Theory]
    [InlineData("0.1.5", "^0.1.0", true)]
    [InlineData("0.1.9", "^0.1.5", true)]
    [InlineData("0.2.0", "^0.1.0", false)]
    [InlineData("0.2.0", "^0.1.5", false)]
    [InlineData("1.5.0", "^1.0.0", true)]
    [InlineData("2.0.0", "^1.9.9", false)]
    public void caret范围(string candidate, string range, bool? expected)
    {
        Assert.Equal(expected, HarnessForm.SatisfiesRange(candidate, range));
    }

    [Theory]
    [InlineData("0.1.5", ">=0.2.0 || 0.1.5", true)]   // 第二个候选项满足 → 整条满足
    [InlineData("0.1.5", ">=0.2.0 || 0.3.0", false)]  // 全部候选项明确不满足
    [InlineData("0.1.5", ">=0.2.0 || *", null)]        // 有一个候选项判不出来 → 整体不猜
    public void 或组合(string candidate, string range, bool? expected)
    {
        Assert.Equal(expected, HarnessForm.SatisfiesRange(candidate, range));
    }

    [Fact]
    public void 认不出的写法一律返回无法判定_而不是误判满足()
    {
        // x / 通配 / 连字符范围等本工具不支持的写法，方向必须是"判不出来"。
        Assert.Null(HarnessForm.SatisfiesRange("0.1.5", "*"));
        Assert.Null(HarnessForm.SatisfiesRange("0.1.5", "x"));
        Assert.Null(HarnessForm.SatisfiesRange("0.1.5", "workspace:*"));
    }

    [Theory]
    [InlineData("@deepseek-ai/dsh", true)]      // 引擎本体：与引擎同版本发布
    [InlineData("@deepseek-ai/dsh-web", true)]  // 同族
    [InlineData("@deepseek-ai/dshxyz", false)]  // 前缀相同但不是同族
    [InlineData("@deepseek-ai/other", false)]
    [InlineData("cordis", false)]               // cordis 有自己的版本号，拿引擎版本去比毫无意义
    public void 只有与引擎同版本发布的包才拿引擎版本去比(string package, bool expected)
    {
        Assert.Equal(expected, HarnessForm.IsDshVersionedPackage(package));
    }
}
