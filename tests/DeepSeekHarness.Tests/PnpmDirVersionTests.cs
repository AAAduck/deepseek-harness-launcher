using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 从 pnpm 存放区目录名里取版本号的单测。
///
/// 这个字符串的落点只有一处，但后果很重：它是插件兼容检查里"当前实际装着的版本"
/// （<c>ResolveInstalledPackageVersion</c> 的兜底路），拿去和 peerDependencies
/// 声明的范围比。取错版本**不报错**：比出来的结论平平无奇地出现在「升级引擎」
/// 那个确认框里，用户要么白升级一次（插件静默失效），要么被无谓的警告劝退。
///
/// 事故本体：分隔版本号的那个 '@' 必须是**包名之后的第一处**。此前用 LastIndexOf，
/// 新式 peer 变体 <c>dsh@1.2.3_react@18.3.1</c> 于是取到 **peer 的版本 18.3.1**——
/// 那是彻底无关的另一个包的版本，却被交给 semver 去比。
/// </summary>
public class PnpmDirVersionTests
{
    [Theory]
    [InlineData("dsh@1.2.3", "1.2.3")]                              // 最简形状
    [InlineData("@deepseek-ai+dsh@0.1.5", "0.1.5")]                // scoped：斜杠换加号
    [InlineData("dsh@1.2.3(react@18.3.1)", "1.2.3")]               // 旧式 peer 变体
    [InlineData("dsh@1.2.3_react@18.3.1", "1.2.3")]               // 新式 peer 变体 ← 事故本体
    [InlineData("dsh@1.2.3(@types+react@18.3.1)", "1.2.3")]        // peer 是 scoped 包
    [InlineData("dsh@1.2.3_@types+react@18.3.1", "1.2.3")]
    [InlineData("@deepseek-ai+dsh@0.2.0-rc.2", "0.2.0-rc.2")]      // 引擎本体常是预发布版
    public void 取出包自己的版本号(string dirName, string expected)
    {
        Assert.Equal(expected, Semver.ParsePnpmDirVersion(dirName));
    }

    [Fact]
    public void 新式peer变体_取到的是包自己的版本而不是peer的()
    {
        // 单独再钉一次，因为它是唯一一条"改错了也不会在别处露馅"的形状：
        // 18.3.1 是个完全合法的版本号，取错之后整条链路照跑，semver 也照给结论。
        // 把实现改回 LastIndexOf，上面 Theory 里带 peer 的五条会一起翻红。
        Assert.Equal("1.2.3", Semver.ParsePnpmDirVersion("dsh@1.2.3_react@18.3.1"));
        // 顺带钉住"下标 0 的那个 '@' 不能当分隔符"：scoped 包
        // "@deepseek-ai+dsh@0.1.5" 只有一个版本来源，从下标 1 起找才对。
        Assert.Equal("0.1.5", Semver.ParsePnpmDirVersion("@deepseek-ai+dsh@0.1.5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dsh")]                    // 根本没有 '@'
    [InlineData("@deepseek-ai+dsh")]       // scoped 包名，没跟版本
    [InlineData("dsh@")]                   // '@' 收尾：后面什么都没有
    [InlineData("dsh@latest")]              // 不是版本号
    [InlineData("dsh@1.2")]                // 两段：ParseVersion 不认（与 semver 范围同一把尺）
    [InlineData("dsh@1.2.3.4.5")]          // 五段：同样不认
    [InlineData("dsh@not-a-version")]
    public void 取不出合法版本_返回无法判定_而不是把乱七八糟的目录名当版本(string? dirName)
    {
        // 方向是"宁可判不出来"：返回一个随便什么字符串都会进入 semver 比较，
        // 而 semver 对解析不了的输入给的是 null，最终显示成"未能判定"——
        // 但那时错误已经绕了一圈，且丢掉了本可以早点说清楚的机会。
        Assert.Null(Semver.ParsePnpmDirVersion(dirName));
    }

    [Fact]
    public void peer变体后缀被切掉之后_剩下的仍必须是能解析的版本()
    {
        // 后缀切分在（'(' 或 '_'）上切，切完还得过一遍 ParseVersion 才放行。
        // 少这一步的话，"dsh@whatever" 与 "dsh@..." 这类目录名会把垃圾当成版本
        // 一路带到兼容性检查里，而那条检查全程不报错。
        Assert.Null(Semver.ParsePnpmDirVersion("dsh@junk(react@18.3.1)"));
        Assert.Null(Semver.ParsePnpmDirVersion("dsh@junk_react@18.3.1"));
    }
}
