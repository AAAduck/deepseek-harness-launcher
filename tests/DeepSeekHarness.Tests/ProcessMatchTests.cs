using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 进程匹配内核的单测。这是整个启动器里最贵的一个判断：命中即"连整个进程树一起杀"，
/// 判错的后果分两类——杀错（用户的 DSH 桌面客户端白屏崩溃）比漏杀严重得多，
/// 而两种错法都不会报错，只在用户眼前发生。
///
/// 所以这里的每条用例都对应一次真实事故或一个真实误伤面，改匹配规则前先看这里。
/// </summary>
public class ProcessMatchTests
{
    // 与本机形态一致的假路径：引擎装在 %LOCALAPPDATA%\DeepSeekHarness\engine，
    // 而它一定在 %USERPROFILE% 之下——宽松匹配正是靠这一点区分"本用户"与"别的会话"。
    private const string EngineDir = @"C:\Users\me\AppData\Local\DeepSeekHarness\engine";
    private const string UserHome = @"C:\Users\me";
    private const int Port = 3080;

    private static bool Match(string name, string commandLine) =>
        HarnessForm.MatchesHarnessCommand(name, commandLine, EngineDir, UserHome, Port);

    [Fact]
    public void 本启动器自己装的引擎_命中()
    {
        Assert.True(Match("node.exe",
            $@"""{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js"" web --port 3080"));
    }

    [Fact]
    public void 启动器自身_不命中_免得把自己杀了()
    {
        Assert.False(Match("DeepSeekHarness.exe", @"C:\app\DeepSeekHarness.exe"));
    }

    [Fact]
    public void 桌面客户端的引擎宿主_绝不命中_否则客户端renderer会崩()
    {
        // 真实事故：这条命令行同时含 app.asar\dsh 和 dsh + 空白 + web，
        // 被宽泛正则当成"残留 dsh web 进程"整树杀掉，客户端当场白屏。
        var cmd = @"C:\Program Files\DeepSeek Harness\resources\app.asar\dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\index.js";
        Assert.False(Match("DeepSeek Harness.exe", cmd));
    }

    [Fact]
    public void dsh_subprocess_不命中()
    {
        Assert.False(Match("node.exe",
            $@"""{UserHome}\.dsh\node_modules\@deepseek-ai\dsh-subprocess\bin\x.js"" web"));
    }

    [Fact]
    public void 别的登录会话的引擎_不命中_否则一个会话能杀掉另一个()
    {
        // 互斥体是 Local\（每会话一个），管不到别的会话；
        // 宽松匹配若不限本用户路径，本会话点「停止」会去杀别人正在跑的引擎。
        Assert.False(Match("node.exe",
            @"C:\Users\someone-else\AppData\Local\DeepSeekHarness\engine\node_modules\@deepseek-ai\dsh\lib\bin.js web --port 3080"));
    }

    [Fact]
    public void 非node进程即使命令行提到dsh_web_也不命中()
    {
        Assert.False(Match("notepad.exe", @"C:\notes\dsh web.txt"));
    }

    [Fact]
    public void 早期npx时代残留_命中_否则旧残留会一直占着端口()
    {
        // 真实的早期残留长这样：npx.cmd 的完整路径在 %APPDATA%\npm 下，
        // 也就是在 %USERPROFILE% 之下——宽松匹配正是靠这一点认出"是自己人"。
        Assert.True(Match("cmd.exe",
            $@"cmd /d /s /c ""{UserHome}\AppData\Roaming\npm\npx.cmd"" -y @deepseek-ai/dsh web --port {Port}"));
    }

    [Fact]
    public void 无法归属到本用户的命令行_不杀_归属不清就不动手()
    {
        // 命令行里一个用户路径都没有（走 PATH 直接调 npx 的典型形状），
        // 那它就分不清是哪个会话的——按"误杀代价远大于漏杀"，宁可不杀：
        // 真残留占着端口时，启动前的端口探测会给出明确报错兜住。
        Assert.False(Match("cmd.exe", $@"cmd /d /s /c ""npx -y @deepseek-ai/dsh web --port {Port}"""));
    }

    [Fact]
    public void 只提到dsh但没有本启动器端口_不命中()
    {
        // 端口是最后一道收窄：没有它，一个恰好在跑 node 且命令行带 dsh 的
        // 无关进程会被整树杀掉（误杀远重于漏杀）。
        Assert.False(Match("node.exe", $@"""{UserHome}\tools\dsh"" web"));
    }
}
