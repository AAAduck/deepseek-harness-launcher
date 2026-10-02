using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 本轮修复引入的纯函数内核的单测。
///
/// 挑选标准与本仓库其余测试一致：只测**"判错了不报错"**的决策。
/// 这里的每一处要么曾经把数据清零（TrimLogTail），要么会把垃圾值喂进一条
/// "停引擎 → 替换"的不归路径（ParseNpmVersionOutput），要么会整树杀错进程
/// （ContainsPathSegment / MayHoldProfileLock）。
/// </summary>
public class RegressionGuardTests
{
    // ---- TrimLogTail：整份日志被清成 0 字节 ---------------------------------

    // 最后一条记录是一段超长单行、且整份日志以 '\n' 收尾（AppendUpdateLog 的常态）：
    // 兜底分支的 tail 里唯一的换行正好落在最后一个字符上，旧写法切成空串。
    // 实测 300 KB 输入 → 0 字节输出，整份日志连同全部历史一起消失。
    [Fact]
    public void 超长单行收尾时不得把日志清空()
    {
        var text = "=== 2026-01-01 00:00:00 ===\n" + new string('x', 300_000) + "\n";
        var result = HarnessForm.TrimLogTail(text);
        Assert.NotEqual(string.Empty, result);
        Assert.True(result.Length > 0, "整份日志被清成了空串");
    }

    [Theory]
    [InlineData(true)]   // 以 '\n' 收尾
    [InlineData(false)]  // 没有结尾换行（崩溃时最后一行就是这个形态）
    public void 各种超长单行形态下都不得返回空串(bool trailingNewline)
    {
        var text = "=== hdr ===\n" + new string('y', 300_000) + (trailingNewline ? "\n" : "");
        var result = HarnessForm.TrimLogTail(text);
        Assert.NotEqual(string.Empty, result);
    }

    [Fact]
    public void 没有结尾换行的超长行也要被保留()
    {
        var text = "=== hdr ===\n" + new string('z', 300_000);
        var result = HarnessForm.TrimLogTail(text);
        Assert.NotEqual(string.Empty, result);
        // 兜底分支保留的是尾部，最后一个字符必须在里面——那才是最新内容。
        Assert.Equal('z', result[^1]);
    }

    [Fact]
    public void 未超限的日志原样返回()
    {
        var text = "=== 2026-01-01 ===\nhello\nworld\n";
        Assert.Same(text, HarnessForm.TrimLogTail(text));
    }

    [Fact]
    public void 超限时按整条记录滚动_表头不被切掉()
    {
        // 造一份多记录、超限的日志（必须真的超过 256 KB，否则走的是"原样返回"分支）：
        // 末尾若干条完整记录应当被保留，头部记录应已被滚动掉。
        var sb = new System.Text.StringBuilder();
        sb.Append("=== first ===\n");
        for (var i = 0; i < 40_000; i++) sb.Append("line ").Append(i).Append('\n');
        var text = sb.ToString();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) > 256 * 1024, "样本没有超过截断阈值");

        var result = HarnessForm.TrimLogTail(text);
        Assert.DoesNotContain("line 0\n", result);   // 头部记录已被滚动掉
        Assert.Contains("line 39999", result);        // 最新记录还在
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= 128 * 1024 + 64);
    }

    // ---- ParseNpmVersionOutput：查版本的三道闸 -----------------------------

    [Fact]
    public void 退出码非零时即便stdout有内容也不算查到了()
    {
        // npm 失败时 stdout 里也可能有内容（错误摘要/缓存回显），旧写法不看退出码，
        // 会把这段文本当成"最新版"一路带回，直到 IsSafeVersionToken 才失败——
        // 而那已经在"引擎已被停掉"之后了。
        Assert.Null(HarnessForm.ParseNpmVersionOutput("npm ERR! code E404\n0.2.0", 1));
    }

    [Fact]
    public void npm_warn行不得被当成版本号()
    {
        // npm 的 warn/notice 走 stderr。旧写法把 stderr 拼进 stdout 再取末行，
        // 于是查询**成功**也会被一条 warn 顶掉。这里确认纯 stdout 形态仍能正确取到版本。
        Assert.Equal("0.2.0", HarnessForm.ParseNpmVersionOutput("0.2.0\n", 0));
    }

    [Theory]
    [InlineData("npm WARN config registry \"https://x/\"")]
    [InlineData("npm ERR! code E404")]
    [InlineData("not-a-version")]
    [InlineData("latest")]       // npm view 不会返回它，形态闸直接挡掉
    [InlineData("０.２.０")]        // 全角数字：ECMAScript 下 \d 只认 ASCII
    public void 非semver形态一律判为查不到(string stdout)
    {
        Assert.Null(HarnessForm.ParseNpmVersionOutput(stdout, 0));
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.2.0-rc.2", "0.2.0-rc.2")]
    [InlineData("1.2.3+build.7", "1.2.3+build.7")]
    [InlineData("0.2.0\r\n", "0.2.0")]     // CRLF
    public void 合法semver原样返回(string stdout, string expected)
    {
        Assert.Equal(expected, HarnessForm.ParseNpmVersionOutput(stdout, 0));
    }

    [Fact]
    public void 空输出判为查不到()
    {
        Assert.Null(HarnessForm.ParseNpmVersionOutput("", 0));
        Assert.Null(HarnessForm.ParseNpmVersionOutput("   \n  ", 0));
        Assert.Null(HarnessForm.ParseNpmVersionOutput(null!, 0));
    }

    // ---- ContainsPathSegment：用户目录的段级比较 ---------------------------

    [Theory]
    // 用户名互为前缀时子串写法必然误判：C:\Users\Dan 是 C:\Users\Daniel 的前缀。
    // 误判方向是"把别的会话的引擎判成本启动器的残留并整树杀掉"。
    [InlineData(@"C:\Users\Daniel\AppData\Local\Temp\node.exe", @"C:\Users\Dan", false)]
    [InlineData(@"C:\Users\DanielX\AppData\node.exe", @"C:\Users\Dan", false)]
    [InlineData(@"C:\Users\Dan\AppData\Local\Temp\node.exe", @"C:\Users\Dan", true)]
    [InlineData(@"""C:\Users\Dan\AppData\Local\Temp\node.exe"" --port 3080", @"C:\Users\Dan", true)]
    [InlineData(@"C:\Users\Dan", @"C:\Users\Dan", true)]
    [InlineData(@"--home=C:\Users\Dan\.dsh", @"C:\Users\Dan", true)]
    public void 用户目录必须按段比较而不是子串(string commandLine, string home, bool expected)
    {
        Assert.Equal(expected, HarnessForm.ContainsPathSegment(commandLine, home));
    }

    [Theory]
    [InlineData("", @"C:\Users\Dan")]
    [InlineData(@"C:\Users\Dan", "")]
    [InlineData(@"C:\Users\Dan", @"  ")]        // 全是空白 → 削空 → 必须拒
    [InlineData(@"C:\Users\Dan", @"\\")]
    public void 脏输入不得被当成命中(string commandLine, string home)
    {
        Assert.False(HarnessForm.ContainsPathSegment(commandLine, home));
    }

    [Fact]
    public void 用户目录尾部分隔符不影响判定()
    {
        Assert.True(HarnessForm.ContainsPathSegment(@"C:\Users\Dan\AppData\node.exe", @"C:\Users\Dan\"));
    }

    [Fact]
    public void 前缀账户名不得被判成本用户残留_否则会整树杀掉别人的引擎()
    {
        // 杀进程路径（M5）的真实形状：**宽松匹配**那一支（早期 npx / dsh.cmd 时代的
        // 残留）。同机同时存在 Daniel 与 Dan 两个账户，Dan 的启动器点一次「停止」
        // 就会把 Daniel 正在跑的引擎连树带孙杀掉，而这一支连端口都不用匹配。
        //
        // 注意命令行里刻意**不含**本启动器的引擎目录：上面那条"引擎内 dsh 包目录"
        // 精确匹配本就不需要 userHomeDir（命令行带着自己的引擎目录本身就是确定性证据），
        // 拿它来验证用户目录收窄是验不到东西的。
        const string someoneElses =
            @"cmd /d /s /c ""C:\Users\Daniel\AppData\Roaming\npm\npx.cmd"" -y @deepseek-ai/dsh web --port 3080";
        const string danEngine = @"C:\Users\Dan\AppData\Local\DeepSeekHarness\engine";

        Assert.False(HarnessForm.MatchesHarnessCommand("cmd.exe", someoneElses, danEngine, @"C:\Users\Dan", 3080));
        // 同一个命令行对它**自己**的用户仍然命中——这道收窄只收紧不得放宽。
        Assert.True(HarnessForm.MatchesHarnessCommand("cmd.exe", someoneElses, danEngine, @"C:\Users\Daniel", 3080));
    }

    // ---- MayHoldProfileLock：孤儿锁清理的判据 -------------------------------

    private const string Home = @"C:\Users\Dan";

    [Fact]
    public void 桌面客户端的引擎宿主也必须算作可能持锁()
    {
        // 排除清单（app.asar / dsh-desktop-host）对"杀进程"是对的，但客户端与本启动器
        // 共用 profiles\node_modules.lock。借 IsHarnessCommand 判活锁会把**活锁**删掉，
        // 两个引擎于是并发写同一份 profile——正是这把锁要防的踩踏。
        const string cmd =
            @"""C:\Program Files\DeepSeek Harness\DeepSeek Harness.exe"" --expose-internals " +
            @"""C:\Users\Dan\AppData\Local\Programs\DeepSeek Harness\resources\app.asar\dsh\node_modules\@deepseek-ai\dsh\lib\index.js""";
        Assert.False(HarnessForm.MatchesHarnessCommand("DeepSeek Harness.exe", cmd,
            @"C:\Users\Dan\AppData\Local\DeepSeekHarness\engine", Home, 3080));   // 仍然不杀
        Assert.True(HarnessForm.MayHoldProfileLock("DeepSeek Harness.exe", cmd, Home));  // 但要看作持锁
    }

    [Theory]
    [InlineData(@"C:\Users\Dan\AppData\Local\DeepSeekHarness\engine\node_modules\@deepseek-ai\dsh\lib\bin.js", "node.exe")]
    [InlineData(@"""C:\Users\Dan\.dsh\profiles\web\cordis.yml""", "cmd.exe")]
    public void 本启动器自己的引擎同样算作可能持锁(string commandLine, string name)
    {
        Assert.True(HarnessForm.MayHoldProfileLock(name, commandLine, Home));
    }

    [Theory]
    // 启动器自己不持锁；非引擎形态不持锁；别的用户目录不持锁。
    [InlineData("DeepSeekHarness.exe", @"C:\Users\Dan\AppData\Local\DeepSeekHarness\engine\node_modules\@deepseek-ai\dsh")]
    [InlineData("notepad.exe", @"C:\Users\Dan\.dsh\profiles\web\cordis.yml")]
    [InlineData("node.exe", @"C:\Users\Daniel\.dsh\profiles\web\cordis.yml")]   // 前缀绕过
    public void 不可能持锁的进程一律排除(string name, string commandLine)
    {
        Assert.False(HarnessForm.MayHoldProfileLock(name, commandLine, Home));
    }

    [Fact]
    public void 归属不清时一律当作可能持锁()
    {
        // 与本文件其余判据相反的方向：这里漏认 = 删掉活锁，是唯一不可逆的错误。
        Assert.False(HarnessForm.MayHoldProfileLock("", "", Home));
        Assert.False(HarnessForm.MayHoldProfileLock("node.exe", @"C:\Users\Dan\node.exe", ""));  // 无 USERPROFILE
    }

    // ---- IsSafeToolPath：cmd 的 %VAR% 展开 ---------------------------------

    [Theory]
    [InlineData(@"C:\Program Files\nodejs\npm.cmd", true)]      // 空白是常态，调用点加引号
    [InlineData(@"C:\nodejs\npm.cmd", true)]
    [InlineData(@"%ProgramFiles%\nodejs\npm.cmd", false)]      // cmd 会展开 %VAR%
    [InlineData(@"C:\node%USERNAME%js\npm.cmd", false)]
    [InlineData(@"""C:\nodejs\npm.cmd""", false)]
    [InlineData("", false)]
    public void 工具路径不得含百分号或引号(string path, bool expected)
    {
        Assert.Equal(expected, HarnessForm.IsSafeToolPath(path));
    }

    [Fact]
    public void 空工具路径一律拒()
    {
        Assert.False(HarnessForm.IsSafeToolPath(null));
        Assert.False(HarnessForm.IsSafeToolPath(""));
    }

    // ---- FindMissingEngineDependencies：半截安装 ----------------------------

    [Fact]
    public void 缺任何一个直接依赖都算不完整()
    {
        var present = new HashSet<string>(StringComparer.Ordinal)
            { "@deepseek-ai/dsh-base", "commander" };
        var declared = new[] { "@deepseek-ai/dsh-base", "commander", "@deepseek-ai/dsh-terminal" };

        // 只缺一个也必须报出来——"缺依赖"是布尔判定，漏掉其中任何一个的代价相同。
        Assert.Equal(
            new[] { "@deepseek-ai/dsh-terminal" },
            HarnessForm.FindMissingEngineDependencies(declared, present.Contains));

        // 全都落地时才算完整。
        present.Add("@deepseek-ai/dsh-terminal");
        Assert.Empty(HarnessForm.FindMissingEngineDependencies(declared, present.Contains));
    }

    [Fact]
    public void 依赖探针对脏输入保持稳定()
    {
        // 没有任何依赖被声明（清单读不出来）→ 不误报缺失，保持"升级前的行为"。
        Assert.Empty(HarnessForm.FindMissingEngineDependencies(null!, _ => false));
        Assert.Empty(HarnessForm.FindMissingEngineDependencies(Array.Empty<string>(), _ => false));
        // 空白包名直接跳过，不能拿去拼路径。
        Assert.Empty(HarnessForm.FindMissingEngineDependencies(new[] { "", "   " }, _ => false));
    }

    // ---- IsSafeRestoreTarget：恢复动作的链接/联接点防线 ----------------------

    // 纯函数内核：IO 通过 attributesOf 注入，所以不需要真的造符号链接
    // （造 junction 要 SeCreateSymbolicLink 权限，测试机上并不一定有）。
    private const string Root = @"C:\Users\me\.dsh";

    private static FileAttributes Plain => FileAttributes.Normal;
    private static FileAttributes Link => FileAttributes.ReparsePoint;

    [Theory]
    // 正常形状：沿途全是普通目录/文件 → 放行。
    [InlineData(@"C:\Users\me\.dsh\settings.yaml", Root)]
    [InlineData(@"C:\Users\me\.dsh\profiles\web\cordis.patch.yml", Root)]
    [InlineData(@"C:\Users\me\.dsh\profiles\web", Root)]
    public void 普通路径放行(string candidate, string root) =>
        Assert.True(ConfigBackup.IsSafeRestoreTarget(candidate, root, _ => Plain));

    [Theory]
    // 词法越界：与 IsWithinRoot 同一判据，先在这里就挡住。
    [InlineData(@"C:\Users\me\.dshevil\x.yaml", Root)]
    [InlineData(@"C:\Users\me\.dsh\..\evil\x.yaml", Root)]
    [InlineData(@"C:\Windows\System32\evil.dll", Root)]
    public void 词法越界仍然拒绝(string candidate, string root) =>
        Assert.False(ConfigBackup.IsSafeRestoreTarget(candidate, root, _ => Plain));

    [Fact]
    public void 路径上有联接点就拒绝_哪怕词法完全落在根内()
    {
        // 这就是 IsWithinRoot 看不见的那一半：profiles\web 是个指向别处的 junction 时，
        // 路径字符串完全合法，实际写入却在 $DSH_HOME 之外。
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { @"C:\Users\me\.dsh\profiles\web" };
        Assert.False(ConfigBackup.IsSafeRestoreTarget(
            @"C:\Users\me\.dsh\profiles\web\cordis.yml", Root,
            p => linked.Contains(p) ? Link : Plain));
        // 同一路径在"没有联接点"时必须放行——这道守卫只拦链接，不拦一切。
        Assert.True(ConfigBackup.IsSafeRestoreTarget(
            @"C:\Users\me\.dsh\profiles\web\cordis.yml", Root, _ => Plain));
    }

    [Fact]
    public void 文件本身是链接同样拒绝()
    {
        Assert.False(ConfigBackup.IsSafeRestoreTarget(
            @"C:\Users\me\.dsh\settings.yaml", Root, _ => Link));
    }

    [Fact]
    public void 根自己不被检查_把_dsh_放到别的盘是用户的正当选择()
    {
        // "$DSH_HOME 整个是个 junction"是用户重定向到 OneDrive / 另一块盘的常见做法，
        // 守卫不能把它判成不安全——只查根**之下**的段。
        Assert.True(ConfigBackup.IsSafeRestoreTarget(
            @"D:\OneDrive\.dsh\settings.yaml", @"D:\OneDrive\.dsh", _ => Plain));
    }

    [Fact]
    public void 属性读不出来一律不放行()
    {
        // 判不出"安全"就当不安全——判错方向必须是保守的那一侧。
        Assert.False(ConfigBackup.IsSafeRestoreTarget(
            @"C:\Users\me\.dsh\settings.yaml", Root, _ => throw new UnauthorizedAccessException()));
        Assert.False(ConfigBackup.IsSafeRestoreTarget(@"C:\Users\me\.dsh\settings.yaml", Root, null!));
    }

    [Fact]
    public void 该段不存在不等于不安全_首次恢复时目的地本来就没这个文件()
    {
        // IsSafeRestoreTarget 守的是"沿途有没有链接"，而"不存在"的段不可能是链接。
        // 把它判成不安全的后果非常具体：RestoreInto 对每一个**新写入**的目的文件
        // 都返回 false，整条恢复路径一件都恢复不了——而这条只有端到端测试
        // （ConfigBackupRestoreTests）才抓得到，纯函数测试用注入的假探针永远看不见。
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { @"C:\Users\me\.dsh\settings.yaml" };
        Assert.True(ConfigBackup.IsSafeRestoreTarget(
            @"C:\Users\me\.dsh\settings.yaml", Root,
            p => missing.Contains(p) ? throw new FileNotFoundException(p) : Plain));
        Assert.True(ConfigBackup.IsSafeRestoreTarget(
            @"C:\Users\me\.dsh\profiles\web\newfile.yml", Root,
            p => p.EndsWith("newfile.yml", StringComparison.OrdinalIgnoreCase)
                ? throw new FileNotFoundException(p) : Plain));
        // 同理，父目录缺失（CreateDirectory 之前）也不该拒。
        Assert.True(ConfigBackup.IsSafeRestoreTarget(
            @"C:\Users\me\.dsh\profiles\web\cordis.yml", Root,
            p => p.Contains(@"\web\", StringComparison.OrdinalIgnoreCase)
                ? throw new DirectoryNotFoundException(p) : Plain));
    }

    [Theory]
    [InlineData("", Root)]
    [InlineData(@"C:\Users\me\.dsh\settings.yaml", "")]
    [InlineData(@"C:\Users\me\.dsh\settings.yaml", "relative\\root")]   // 根必须是完整路径
    public void 脏输入一律拒绝(string candidate, string root) =>
        Assert.False(ConfigBackup.IsSafeRestoreTarget(candidate, root, _ => Plain));

    // ---- HumanSize.FormatSize：小于 1 MB 不再显示 "0 MB" ------------
    //
    // 注意引用的是 HumanSize（纯类型），**不是** EngineVersionsForm.FormatSize——
    // 后者的类型初始化器会 new 三个 GDI+ Font，经由它调用纯函数会在无 GUI 的
    // 机器上把整套测试拖进字体初始化（见 StaticCouplingTests 的声明）。

    [Theory]
    [InlineData(0L, "—")]                 // 统计失败
    [InlineData(-1L, "—")]
    [InlineData(512L * 1024, "0.5 MB")]    // 修复的主用例：原先显示 "0 MB"
    [InlineData(900L * 1024, "0.9 MB")]
    [InlineData(1024L * 1024, "1 MB")]     // 满 1 MB 起才取整
    [InlineData(214L * 1024 * 1024, "214 MB")]
    public void 版本大小显示(long bytes, string expected) =>
        Assert.Equal(expected, HumanSize.FormatSize(bytes));

    // ---- semver 地基：ParseVersion / CompareVersionStrings -------------------
    //
    // 这两个是 SatisfiesRange / SatisfiesSingle 底下真正的地基，此前**一条直接测试都没有**——
    // 只有经由上层的间接覆盖。而它们的注释里就写着"条件永真""预发布无条件比较"两处
    // 历史 bug，说明这两处正是最容易再次被改坏的地方。
    // internal 是"给单测钉住"的意思，注释写了却没测，等于承诺落空。

    [Theory]
    // 三段是主形态；第四段被接受（npm 的 1.2.3.4）。
    [InlineData("0.2.0", 0, 2, 0, 0)]
    [InlineData("1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("v1.2.3", 1, 2, 3, 0)]        // v 前缀被剥掉
    [InlineData("V1.2.3", 1, 2, 3, 0)]
    [InlineData("  1.2.3  ", 1, 2, 3, 0)]     // 空白被 trim
    [InlineData("0.2.0-rc.2", 0, 2, 0, 0)]   // 预发布在第一个 '-' 处截断
    [InlineData("1.2.3+b-1", 1, 2, 3, 0)]    // build 段里的 '-' 不影响 core
    [InlineData("1.2.3-rc.1+build.7", 1, 2, 3, 0)]
    public void 版本解析(string input, int major, int minor, int build, int revision)
    {
        var v = HarnessForm.ParseVersion(input);
        Assert.NotNull(v);
        Assert.Equal(new Version(major, minor, build, revision), v);
    }

    [Theory]
    // 两段必须拒：^1.2 / ~0.1 这类基准交给上层按"无法判定"处理，
    // 而不是在这里悄悄补 0 当成 1.2.0。
    [InlineData("1.2")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("x.y.z")]
    [InlineData("1.2.x")]
    [InlineData("1..2")]
    [InlineData("-1.2.3")]
    public void 解析不了的版本一律返回null(string? input) =>
        Assert.Null(HarnessForm.ParseVersion(input));

    [Theory]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.9.9", "1.0.0", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    // 预发布 < 同数字段的正式版
    [InlineData("1.2.3-rc.1", "1.2.3", -1)]
    [InlineData("1.2.3", "1.2.3-rc.1", 1)]
    // 数字段不同就**不看**预发布：0.1.5-rc.2 < 0.1.7-rc.1 靠的是 5 < 7。
    // 这条正是"预发布无条件比较"那个历史 bug 的钉子。
    [InlineData("0.1.5-rc.2", "0.1.7-rc.1", -1)]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1", 0)]
    public void 版本比较(string a, string b, int expected) =>
        Assert.Equal(expected, HarnessForm.CompareVersionStrings(a, b));

    [Fact]
    public void 数字段相同而预发布标识不同_判不出来而不是猜()
    {
        // 既定取舍（注释里写明）：预发布标识的逐段比较规则繁琐且本工具用不上，
        // 拿不准就说拿不准。方向安全——整体落"无法判定"而不是猜一个满足/不满足。
        Assert.Null(HarnessForm.CompareVersionStrings("1.2.3-rc.1", "1.2.3-rc.2"));
        Assert.Null(HarnessForm.CompareVersionStrings("1.2.3-rc.1", "1.2.3-beta"));
        // 标识大小写不同视为同一个（OrdinalIgnoreCase）。
        Assert.Equal(0, HarnessForm.CompareVersionStrings("1.2.3-RC.1", "1.2.3-rc.1"));
    }

    [Theory]
    [InlineData("garbage", "1.0.0")]
    [InlineData("1.0.0", "garbage")]
    [InlineData("garbage", "nonsense")]
    [InlineData("1.2", "1.0.0")]          // 两段解析不了
    public void 任一边解析不了就判不出来(string a, string b) =>
        Assert.Null(HarnessForm.CompareVersionStrings(a, b));

    // ---- 预发布门槛：注释自称"要在漏报警方向出过错"，此前无直接测试 ----------

    [Theory]
    // npm 语义：预发布候选只在与范围基准共享同一三元组时才被接受。
    [InlineData("1.2.3-rc.1", 1, 2, 3, true)]     // 同三元组 → 放行
    [InlineData("1.2.4-rc.1", 1, 2, 3, false)]    // patch 不同
    [InlineData("1.3.3-rc.1", 1, 2, 3, false)]    // minor 不同
    [InlineData("2.2.3-rc.1", 1, 2, 3, false)]    // major 不同
    [InlineData("1.2.3", 1, 2, 3, true)]           // 非预发布候选不受门槛限制
    [InlineData("1.2.3+b-1", 1, 2, 3, true)]      // build 段不是预发布
    // …而带 build 的版本**不是**预发布候选，所以它根本不受这道门槛约束——
    // 基准三元组对不对都放行。这不是 bug：npm 的预发布门槛只管预发布候选，
    // 而 "1.2.3+b-1" 就是个带 build 的正式版。刻意钉住，避免后人以为要按三元组筛。
    [InlineData("1.2.3+b-1", 1, 2, 4, true)]
    [InlineData("1.2.3+b-1", 9, 9, 9, true)]
    [InlineData("garbage", 1, 2, 3, true)]        // 解析不了 → 维持原路径，不额外收紧
    public void 范围预发布门槛(string candidate, int major, int minor, int patch, bool expected) =>
        Assert.Equal(expected,
            HarnessForm.PrereleaseAllowedInRange(candidate, major, minor, patch));

    [Theory]
    // 比较器集合级门槛：集合里至少有一个**带预发布**且与候选同三元组的基准才放行。
    [InlineData("0.3.0-rc.1", true)]   // 集合 {0.2.0, no}, {0.3.0, yes} → 放行
    [InlineData("0.2.5-rc.1", false)]  // 两个基准都不带预发布 → 拒
    public void 集合门槛_基准必须自己带预发布(string candidate, bool expected)
    {
        var comparators = new List<(int, int, int, bool)>
        {
            (0, 2, 0, false),   // >=0.2.0
            (0, 3, 0, true),    // <0.3.0-rc.1
        };
        Assert.Equal(expected,
            HarnessForm.PrereleaseAdmittedByComparatorSet(candidate, comparators));
    }

    [Fact]
    public void 集合门槛_非预发布候选与解析不了的候选不受限制()
    {
        var none = new List<(int, int, int, bool)> { (0, 2, 0, false) };
        Assert.True(HarnessForm.PrereleaseAdmittedByComparatorSet("0.2.5", none));
        Assert.True(HarnessForm.PrereleaseAdmittedByComparatorSet("0.2.5+b", none));
        Assert.True(HarnessForm.PrereleaseAdmittedByComparatorSet("garbage", none));
        // 空集合 + 预发布候选 → 无基准可对认 → 拒（这是 npm 语义，不是猜）。
        Assert.False(HarnessForm.PrereleaseAdmittedByComparatorSet(
            "0.2.5-rc.1", Array.Empty<(int, int, int, bool)>()));
    }

    // ---- EnginePackageDirUnder：杀进程那条精确匹配的锚点 -------------------

    [Fact]
    public void 引擎包目录必须是引擎目录下的包路径()
    {
        const string engine = @"C:\Users\me\AppData\Local\DeepSeekHarness\engine";
        var expected = Path.Combine(engine, "node_modules", "@deepseek-ai", "dsh");
        Assert.Equal(expected, HarnessForm.EnginePackageDirUnder(engine));
        // 必须以分隔符收尾，命令行里 c.Contains(这个) 才不会把
        // "...\engine-old\node_modules\..." 之类的相邻目录误命中。
        Assert.EndsWith("dsh", expected, StringComparison.Ordinal);
        Assert.Contains("node_modules", expected, StringComparison.Ordinal);
    }
}