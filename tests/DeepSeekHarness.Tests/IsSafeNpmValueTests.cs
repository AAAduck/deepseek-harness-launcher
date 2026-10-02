using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// IsSafeNpmValue 的单测：它是"用户可改的 npm 配置（registry）"进 cmd 命令行的
/// 唯一闸门（InstallEngineAsync / GetLatestEngineVersionAsync 里
/// <c>--registry &lt;值&gt;</c> 不加引号直接拼接）。README 明文承诺
/// "读取到的源会做参数校验，含引号或空白的值不采用"——本文件把这承诺钉进构建：
/// 任何一条排除项被顺手删掉，这里立刻红。
/// </summary>
public class IsSafeNpmValueTests
{
    [Theory]
    [InlineData("https://registry.npmjs.org/")]
    [InlineData("https://registry.npmmirror.com")]
    [InlineData("http://localhost:4873")]
    [InlineData("https://registry.npmmirror.com/")]   // 带尾斜杠的常见写法
    public void 正常的_registry地址_放行(string value)
    {
        Assert.True(HarnessForm.IsSafeNpmValue(value));
    }

    [Theory]
    // —— README 承诺的两类：引号与空白 ——
    [InlineData("https://x/\"calc\"")]     // 双引号：能闭合/重开 cmd 的外层引号
    [InlineData("https://x/'a b'")]        // 单引号：对 cmd 无引用作用，一并拒绝
    [InlineData("https://a b/")]           // 空格：把命令行拆成两个参数
    [InlineData("https://x/\t")]           // 制表符（char.IsWhiteSpace）
    [InlineData("https://x/\nhttps://y")]  // 换行：伪造第二条命令
    // —— Unicode 空白：char.IsWhiteSpace 覆盖的不止 ASCII 空格，全角空格/不换行空格同样危险 ——
    [InlineData("https://x/ ")]      // U+00A0 NO-BREAK SPACE
    [InlineData("https://x/　")]      // U+3000 全角空格
    [InlineData("https://x/ ")]      // U+2000 EN QUAD
    // —— cmd 元字符：引号外拼接时能改写命令结构 ——
    [InlineData("https://x/&calc")]        // 命令连接
    [InlineData("https://x/|calc")]        // 管道
    [InlineData("https://x/>file")]        // 输出重定向
    [InlineData("https://x/<file")]        // 输入重定向
    [InlineData("https://x/^W")]           // 转义符
    [InlineData("https://x/%PATH%")]       // % 展开：cmd 变量注入
    // —— 退化和边界 ——
    [InlineData("")]                       // 空串：--registry 后面空着，npm 报错背锅
    [InlineData("     ")]                  // 纯空白
    public void 危险或退化的值_拒绝(string value)
    {
        Assert.False(HarnessForm.IsSafeNpmValue(value));
    }

    [Fact]
    public void 超长值_拒绝_512是上界()
    {
        // npm 允许很长的 URL，但进命令行的值没有理由这么长；按实现钉住 < 512。
        Assert.True(HarnessForm.IsSafeNpmValue(new string('a', 511)));
        Assert.False(HarnessForm.IsSafeNpmValue(new string('a', 512)));
    }
}
