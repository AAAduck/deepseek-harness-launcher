using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 退出清扫（关窗时结束引擎）的匹配内核。
///
/// 这是**第二条**杀进程的路径，与「停止」按钮那条（<see cref="ProcessMatchTests"/>）
/// 各改各的——它们已经分叉过一次（空串护栏只补在了一条上），所以两边都要有测试。
/// 失手形态：要么漏杀（残留占端口，下次启动有报错兜住），要么误杀
/// （桌面客户端白屏、或关窗时把不相干的进程整树带走）。后者的代价高得多。
/// </summary>
public class EngineSweepTests
{
    private const string EngineDir = @"C:\Users\me\AppData\Local\DeepSeekHarness\engine";

    private static bool Match(string name, string commandLine, string engineDir = EngineDir) =>
        ProcessMatch.MatchesEngineProcess(name, commandLine, engineDir);

    [Fact]
    public void 本启动器拉起的引擎_命中()
    {
        Assert.True(Match("node.exe",
            $@"""C:\Program Files\nodejs\node.exe"" ""{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js"" web --port 3080"));
    }

    [Fact]
    public void 编辑器打开了引擎目录下的文件_不命中_与停止路径同一口径()
    {
        // 与 ProcessMatchTests 的同名用例互为镜像：两条杀进程路径已经分叉过一次，
        // 误伤面（编辑器以引擎目录下文件为参数）两边都必须钉住。
        Assert.False(Match("Code.exe",
            $@"""C:\Program Files\Microsoft VS Code\Code.exe"" ""{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js"""));
    }

    [Fact]
    public void node跑了用户自己放在引擎目录下的脚本_不命中_与停止路径同一口径()
    {
        Assert.False(Match("node.exe", $@"""{EngineDir}\tools\my-script.js"""));
    }

    [Fact]
    public void 启动器自身_不命中_不能把自己杀了()
    {
        Assert.False(Match("DeepSeekHarness.exe", @"C:\app\DeepSeekHarness.exe"));
    }

    [Fact]
    public void 桌面客户端的引擎宿主_绝不命中_哪怕命令行里也出现了引擎目录字样()
    {
        // 排除优先于包含：真实事故里这条命令行同时含 app.asar 与 @deepseek-ai/dsh，
        // 被当成"我们的引擎"整树杀掉，客户端当场白屏。这里刻意让两个标记同时出现，
        // 钉住"排除在前"的次序——否则有人把三行排除挪到末尾就会翻车。
        Assert.False(Match("DeepSeek Harness.exe",
            $@"C:\Program Files\DeepSeek Harness\resources\app.asar\dsh\{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js web"));
    }

    [Fact]
    public void 别的机器上装的同一款dsh_不命中_退出清扫没有宽松兜底()
    {
        Assert.False(Match("node.exe",
            @"C:\tools\dsh\node_modules\@deepseek-ai\dsh\lib\bin.js web --port 3080"));
    }

    [Fact]
    public void engineDir为空_一律不命中_否则关窗时会把所有进程整树杀掉()
    {
        // 灾难分支：Contains("") 恒为 true，指望调用方永远传对不靠谱。
        // 把这行删掉，这条例子里除第一条外全部会翻红。
        Assert.False(Match("node.exe", @"C:\Windows\System32\svchost.exe -k netsvcs", engineDir: ""));
    }

    [Theory]
    [InlineData("", @"C:\x")]
    [InlineData("node.exe", "")]
    public void 空名字或空命令行_不命中_纯函数连脏输入都不能炸(string name, string cmd)
    {
        Assert.False(Match(name, cmd));
    }
}
