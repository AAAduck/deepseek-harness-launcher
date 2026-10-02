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
    public void 引擎的cmd包装层_命中_stdio重定向就靠它()
    {
        // 真实启动形态：cmd /d /s /c ""node.exe" "…bin.js" web … >> engine-stdio.log 2>&1"。
        // 包装层与 node 本体都必须命中，否则「停止」杀不干净。
        Assert.True(Match("cmd.exe",
            $@"""C:\Program Files\nodejs\node.exe"" ""{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js"" web --port {Port}"));
    }

    [Fact]
    public void 编辑器打开了引擎目录下的文件_不命中_否则关窗停止会整树杀掉编辑器()
    {
        // 真实误伤面（1.4.3 修复）：此前"命令行含引擎目录"对**任意进程名**生效，
        // 用编辑器打开 bin.js 这么普通的动作就会让该编辑器在「停止」/关窗时被
        // 连树带孙杀掉。匹配必须同时核对进程类型与实际引擎入口。
        Assert.False(Match("Code.exe",
            $@"""C:\Program Files\Microsoft VS Code\Code.exe"" ""{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js"" --standalone"));
    }

    [Fact]
    public void node跑了用户自己放在引擎目录下的脚本_不命中()
    {
        // 进程类型对了（node.exe），但命令行里没有引擎内的 dsh 包目录——
        // 那不是我们的引擎，是用户恰好把脚本存在了引擎目录下。
        Assert.False(Match("node.exe", $@"""{EngineDir}\tools\my-script.js"""));
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
    public void desktop_host排除项_独立钉住_不含app_asar()
    {
        // ⚠ 上一条用例**测不到** dsh-desktop-host 那条排除：它的命令行同时含
        // app.asar，两条排除里 app.asar 先命中。所以把 desktop-host 的排除整行删掉，
        // 上一条照样绿——套件自述的"删掉任何排除项必须红"在这条上是假的。
        // 这条刻意**不含 app.asar**，进程名也换成 node.exe（客户端宿主确实是 node），
        // 于是命中与否只由 dsh-desktop-host 那一条决定。
        // 这条形状是真实的：pnpm 布局下 @deepseek-ai/dsh-desktop-host 可能被链接到
        // profile 自己的 node_modules，路径里就没有 app.asar 了。
        Assert.False(Match("node.exe",
            $@"""{UserHome}\.dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\index.js"" web --port {Port}"));
        // 反向钉住：去掉 desktop-host 这段标识后，同一条命令行**必须命中**——
        // 否则说明上面那条是靠别的排除项蒙对的，这条测试什么也没钉住。
        Assert.True(Match("node.exe",
            $@"""{UserHome}\.dsh\node_modules\@deepseek-ai\dsh-plain\lib\index.js"" web --port {Port}"));
    }

    [Fact]
    public void app_asar排除项_独立钉住_不含desktop_host()
    {
        // 与上一条对称：这条只由 app.asar 决定（不含 dsh-desktop-host 字样）。
        Assert.False(Match("node.exe",
            $@"""C:\Users\me\AppData\Roaming\DeepSeek Harness\resources\app.asar\dsh\lib\bootstrap.js"" web --port {Port}"));
    }

    [Fact]
    public void 引擎包目录精确匹配_早于端口收窄_这条语义本身要钉住()
    {
        // 既定语义：命令行里带着**本启动器引擎内**的 dsh 包目录，就是确定性证据，
        // 不再要求命令行提到本启动器端口。它早于端口收窄 return true，是有意为之
        // （端口收窄是给"宽松匹配"兜底的）。写一条显式用例钉住这个次序：
        // 免得后人把两道闸调换，误伤"用户自定义 --port 启动本启动器引擎"这一档。
        Assert.True(Match("node.exe",
            $@"""{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js"" web --port 9999"));
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

    [Fact]
    public void 端口收窄_这条用例是它唯一的护栏_整行删掉它必须红()
    {
        // 注意上一条其实**测不到端口**：那条命令行里 dsh 后面紧跟一个引号，
        // 宽泛正则要求 dsh 与 web 之间只能有空白，所以它在正则那一步就已经出局了。
        // 把端口那行整条删掉，那条用例照样绿——守卫其实没被钉住。
        // 这条补上：路径无空格（node 常见形状，正则会命中）+ 命令行里没有任何端口。
        Assert.False(Match("node.exe", $@"{UserHome}\tools\dsh web"));
    }

    [Fact]
    public void 端口只是前缀相同的另一个端口_不命中_否则会误杀用户自己的进程()
    {
        // 真实误杀面：端口收窄原先写成 c.Contains("3080")，于是 "30801" 也算命中。
        // 用户在 %USERPROFILE% 下自己装一份 dsh 跑在 30801 是很正常的形态
        // （路径无空格 → 命令行不加引号 → 宽泛正则照样命中），
        // 结果点一次「停止」就把用户自己的进程整树杀掉。
        Assert.False(Match("node.exe", $@"{UserHome}\tools\dsh web --port {Port}1"));
    }

    [Theory]
    [InlineData(@"C:\x\dsh web --port 3080", true)]      // 空格分隔
    [InlineData(@"C:\x\dsh web --port=3080", true)]     // 等号分隔
    [InlineData(@"C:\x\dsh web 3080", true)]             // 裸端口
    [InlineData(@"C:\x\dsh web --port 30801", false)]    // 前缀相同的别的端口
    [InlineData(@"C:\x\dsh web --port 3080x", false)]    // 紧跟字母
    [InlineData(@"C:\x\dsh web --port 80", false)]       // 完全不同的端口
    [InlineData(@"C:\x\node_modules\@deepseek-ai\dsh@0.1.5-rc.2\lib\bin.js", false)]  // 版本号里的数字不算
    [InlineData(@"C:\x\dsh@0.1.5-rc.2 web", false)]     // 同上
    // \w/\d 必须是 ASCII 语义，但**方向**要选对：默认 .NET 的 \w 含 Unicode，
    // 于是"端口紧邻一个汉字"会被判成没提端口 → 漏杀真残留（这条收窄是"宁可漏、
    // 不可误杀"，但漏也不能漏得莫名其妙）。ECMAScript 把它反转成命中，方向正确。
    // 真正的分界仍然是数字/字母：30801、3080x 都必须被放过。
    [InlineData(@"C:\x\dsh web --port 3080的进程", true)]
    [InlineData(@"C:\x\dsh web --port ８", false)]       // 全角数字不是端口（\d 退化为 ASCII）
    public void 端口必须是完整的数字token(string commandLine, bool expected)
    {
        Assert.Equal(expected, HarnessForm.MentionsLauncherPort(commandLine, Port));
    }

    [Fact]
    public void 空命令行在端口收窄处也不命中()
    {
        Assert.False(HarnessForm.MentionsLauncherPort(string.Empty, Port));
    }

    [Fact]
    public void 空命令行_不命中()
    {
        // 纯函数必须自己挡脏输入。此前靠 WMI 侧的 ?? string.Empty 兜着，
        // 意味着单独调用它时一个 null 就能把整个查杀流程炸掉。
        Assert.False(Match("node.exe", string.Empty));
    }

    [Fact]
    public void 空引擎目录_不命中_否则任何进程都会被当成自己的引擎()
    {
        // 灾难性的输入：c.Contains("") 恒为 true，于是系统上**每一个**进程
        // 都会被判定成"本启动器装的引擎"并整树杀掉。原实现没有任何防线。
        Assert.False(HarnessForm.MatchesHarnessCommand("notepad.exe", @"C:\notes\x.txt", string.Empty, UserHome, Port));
    }

    [Fact]
    public void 空用户目录_不放行所有用户_归属不清就不动手()
    {
        // 原判断写成 `userHomeDir.Length > 0 && !c.Contains(userHomeDir)`：
        // 空串时这道收窄被**跳过**，恰好把"归属不清就不动手"的既定语义反转成
        // "放行所有用户"。这里钉住的是修正后的语义。
        Assert.False(HarnessForm.MatchesHarnessCommand("node.exe",
            @"C:\Users\someone-else\x\node_modules\@deepseek-ai\dsh\lib\bin.js web --port 3080",
            EngineDir, string.Empty, Port));
    }

    [Fact]
    public void 空用户目录也照样认得出本启动器自己装的引擎_这条是回归钉子()
    {
        // USERPROFILE 缺失 / 用户配置文件 hive 未加载 / 受限容器里，
        // GetFolderPath(UserProfile) 返回空串是文档写明的行为。
        // engineDir 这条精确匹配**不需要** userHomeDir：命令行里带着本启动器的
        // 引擎目录本身就是确定性的证据。曾经把"userHomeDir 为空就返回 false"
        // 提到函数开头，结果在这类机器上连自己的引擎都杀不掉——残留引擎占住端口、
        // 孤儿 node_modules.lock 永远清不掉。这条用例就是防那个回归再来的。
        Assert.True(HarnessForm.MatchesHarnessCommand("node.exe",
            $@"""{EngineDir}\node_modules\@deepseek-ai\dsh\lib\bin.js"" web --no-open --host 127.0.0.1 --port {Port}",
            EngineDir, string.Empty, Port));
    }
}
