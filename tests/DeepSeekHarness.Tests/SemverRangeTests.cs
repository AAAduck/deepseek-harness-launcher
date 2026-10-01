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
    // —— npm 的预发布门槛：caret/tilde 范围只放行与基准同 major.minor.patch 的
    //    预发布候选。此前不实施这条，0.1.5-rc.9 被判满足 ^0.1.0——护栏在
    //    漏报警的方向出错（本工具最不能犯的那个方向）。——
    [InlineData("0.1.5-rc.9", "^0.1.0", false)]     // 修复的主用例：npm 判 false（数字段更高但三元组不同）
    [InlineData("0.1.0-rc.1", "^0.1.0", false)]     // 同上：三元组不同（patch 位不同）
    [InlineData("0.2.0-rc.1", "^0.1.0", false)]      // minor 也不同
    [InlineData("1.5.0-beta", "^1.0.0", false)]
    [InlineData("4.2.0-rc.1", "^4.0.1", false)]      // 审查报告点名的例子
    // 数字段低于基准的预发布候选走不进门槛——被 >= 基准先挡下（独立路径，顺带钉住）：
    [InlineData("0.1.5-rc.1", "~0.1.5", false)]     // rc.1 < 0.1.5 基准
    [InlineData("0.1.5-rc.9", "~0.1.5", false)]     // 同上：预发布恒小于同数字段正式版
    [InlineData("0.1.5-rc.9", "~0.1.9", false)]     // 数字段更低
    [InlineData("0.1.4", "^0.1.5", false)]           // 非预发布候选不受门槛影响（回归保护）
    public void 预发布候选必须与范围基准同三元组(string candidate, string range, bool? expected)
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

    [Theory]
    // "-0" 是 npm 里"小于下一个正式版"的惯用写法（0.3.0-0 排在正式版之前，
    // 于是 <0.3.0-0 表示"任何 0.3.0 的正式版都不在内"）。它此前只作为组合区间的
    // 右半边出现过，没有被单独钉住。
    [InlineData("0.2.9", "<0.3.0-0", true)]
    [InlineData("0.3.0", "<0.3.0-0", false)]          // 正式版 > rc.0
    [InlineData("0.3.0-0", "<0.3.0-0", false)]         // 精确相等，不是"小于"
    // rc.1 vs 0：同为预发布但标识不同 → 本工具明确选择"判不出来"而不是猜。
    // 这条钉的是**保守方向**：宁可报"未能判定"，也不能凭空说插件不兼容。
    [InlineData("0.3.0-rc.1", "<0.3.0-0", null)]
    public void 小于下一个正式版写法(string candidate, string range, bool? expected)
    {
        Assert.Equal(expected, HarnessForm.SatisfiesRange(candidate, range));
    }

    [Theory]
    // —— 基准本身带预发布标识（^0.1.5-rc.1 / ~0.1.5-rc.1）：判不出来 ——
    // npm 对这种范围没有可套用的明确规则，本工具的既定取舍是"判不出来"，
    // README 在 caret 与 tilde **两边**都承诺了这条。此前只有 caret 有这道守卫、
    // tilde 没有，而 tilde 分支恰恰是最容易被"顺手整理"掉的那一处：
    // 删掉守卫后下面 "0.1.6" + "~0.1.5-rc.1" 会从"未能判定"翻成 **满足**——
    // 护栏在漏报警的方向上出错（本工具最不能犯的那个方向）。
    [InlineData("0.1.5", "^0.1.5-rc.1")]        // 正式版 > 同数字段的预发布基准
    [InlineData("0.1.6", "^0.1.5-rc.1")]        // 数字段更高
    [InlineData("0.1.5-rc.1", "^0.1.5-rc.1")]   // 同为预发布、标识相同
    [InlineData("0.1.5-rc.9", "^0.1.5-rc.1")]   // 同为预发布、标识不同
    [InlineData("1.4.1", "^1.4.1-rc.2")]         // 引擎自己的常用形状
    [InlineData("0.1.5", "~0.1.5-rc.1")]
    [InlineData("0.1.6", "~0.1.5-rc.1")]        // ← 没有守卫时这一条会判成 true
    [InlineData("0.1.5-rc.1", "~0.1.5-rc.1")]
    [InlineData("0.1.5-rc.9", "~0.1.5-rc.1")]
    [InlineData("1.4.1", "~1.4.1-rc.2")]
    public void 范围基准带预发布标识时判不出来(string candidate, string token)
    {
        Assert.Null(HarnessForm.SatisfiesSingle(candidate, token));
        // 走完整范围路径必须是同一个"未能判定"：用户在「升级引擎」确认框里看到的
        // 是这一层的结果，token 级判对了但组合层猜了，一样是错的结论。
        Assert.Null(HarnessForm.SatisfiesRange(candidate, token));
    }

    [Fact]
    public void 基准带预发布标识时_低于基准的候选仍是明确不满足()
    {
        // 钉的是上面那条的**边界**：null 只能从"已经进入范围判定"之后出来。
        // 候选低于基准时（0.1.4 < 0.1.5）比较器给出的是确定答案，此时报"未能判定"
        // 会把一条本来明确的"不满足"降级成"未知"——护栏因此在漏报警方向上出错。
        // 把这两行的期望改成 null 它们就会红：那说明有人把 cmp<0 的收窄挪到了守卫之后。
        Assert.False(HarnessForm.SatisfiesSingle("0.1.4", "^0.1.5-rc.1"));
        Assert.False(HarnessForm.SatisfiesSingle("0.1.4", "~0.1.5-rc.1"));
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
