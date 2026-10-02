using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing.Drawing2D;
using System.Windows.Forms.Automation;

namespace DeepSeekHarness;

internal sealed partial class HarnessForm : Form
{
    private const int DefaultPort = 3080;
    private const int ProxyPort = 7897;
    // 引擎安装耗时随网络波动很大：本机实测 537 个包约 67 秒，走 npx 的那次（要联网
    // 重新解析整棵树）到过 91 秒。所以安装超时给到 900 秒——早期版本的 120 秒会把
    // 慢网络下的正常安装直接误判成失败。
    private const int StartTimeoutSeconds = 600;
    private const int UpdateTimeoutSeconds = 600;
    private const int EngineInstallTimeoutSeconds = 900;
    private const int EngineQueryTimeoutSeconds = 60;
    private const int RecentOutputLines = 10;
    /// <summary>
    /// LOCALAPPDATA 解析为空（受限账户/组策略）时得到的是**相对路径**——后续所有
    /// Directory.CreateDirectory / File 操作会按当前工作目录解析，把引擎装到不明位置、
    /// 快照也写到不明位置。宁可直接报出"无法定位数据目录"，也不要静默落到 CWD。
    ///
    /// ⚠ 刻意做成**惰性**的（原为 static readonly 字段，在类型初始化器里求值）。
    /// 静态字段初始化器会在**任何**静态成员首次被访问时执行——哪怕那个成员只是
    /// <c>HarnessForm.ParseVersion</c>这样一个纯字符串函数。于是整套单测都经这条路径
    /// 连带初始化了数据目录解析与三个 GDI+ 字体（见下方三个 Font）：
    /// 无 GUI 的 Windows Server Core / 容器 CI 上没有这些字体，也常常没有
    /// LOCALAPPDATA，于是**与被测逻辑毫无关系**的一条 <c>TypeInitializationException</c>
    /// 让整套测试全红。惰性化之后，只有真正碰文件系统/字体的代码路径才会付这个代价。
    /// 抛异常的语义一字未变：只是从"类型首次加载时"推迟到"这条路径首次使用时"。
    /// </summary>
    private static string? localAppDirResolved;
    private static string LocalAppDir =>
        localAppDirResolved ??= ResolveLocalAppDir();

    private static string ResolveLocalAppDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness");
        if (!Path.IsPathFullyQualified(dir))
            throw new InvalidOperationException(
                "无法定位 %LOCALAPPDATA% 目录（系统返回了相对路径）。\n" +
                "请检查当前用户的环境变量配置后重试。");
        return dir;
    }
    private static readonly string EnginePackageName = "@deepseek-ai/dsh";
    /// <summary>npm 官方源。用户改过 npm 配置时以用户配置为准（见 ResolveNpmRegistry）。</summary>
    private const string DefaultRegistry = "https://registry.npmjs.org/";
    private static readonly Color OkColor = Color.FromArgb(34, 170, 85);
    private static readonly Color WarnColor = Color.FromArgb(238, 148, 32);
    private static readonly Color IdleColor = Color.FromArgb(88, 94, 104);

    // ---- 按钮无障碍名称的**单一真相源** ---------------------------------------
    // 这几个字面量此前散在三处：构造期的 NewActionButton 实参、ApplyAccessibility、
    // EndBusy 的恢复赋值。三处各写一份，漂移后没有任何编译期提示，而后果是
    // 读屏对外自称一个已经不存在的老名字（EndBusy 漏掉某个按钮 → 点一次「重启」
    // 之后它永久自称"重启中"；ApplyAccessibility 又单独覆盖 upgradeButton，
    // 于是 EndBusy 恢复的值与构造值不一致时，界面看着正常、读屏却在撒谎）。
    // 提成常量后，三处引用同一份，改文案只需改一处。
    internal const string RestartAccessibleName = "结束当前引擎并重新启动";
    internal const string RefreshAccessibleName = "重新检测引擎状态";
    internal const string EnvAccessibleName = "检测 Node、npm、pnpm、引擎与端口";
    internal const string FoldersAccessibleName = "打开 DeepSeek Harness 相关目录";
    internal const string VersionsAccessibleName = "管理已安装的引擎版本：切换、删除";
    internal const string UpgradeAccessibleName = "升级 DSH 引擎到 npm 上的最新版本";
    internal const string AutoUpdateAccessibleName = "启动后在后台更新 DSH 插件，改动下次启动生效";

    // 字体与颜色一样是"常量"，用静态字段共享。此前在构造函数和 NewButton 里
    // 各 new 了一份，同一个窗体里出现 7 个内容完全相同的 Font 对象；
    // 它们是实例字段，只能等窗体被 GC 才回收（本程序窗体活到进程结束，等于不回收）。
    // 静态共享把 7 份合成 3 份，且只分配一次。
    //
    // ⚠ 同样**惰性化**（原为 static readonly）：Font 构造要过 GDI+ 并按名解析字体，
    // 无 GUI 的 Server Core / 容器 CI 上这一步会失败，而它由类型初始化器触发——
    // 也就是任何一次 HarnessForm.X 的纯函数调用都会撞上。理由同 LocalAppDir。
    // ??= 不是线程安全的，但 Font 构造幂等且句柄分配失败会抛而不是产生坏对象，
    // 最坏情况是并发构造出两份——与原先"多 new 几份"的代价同量级。
    private static Font? uiFontResolved;
    private static Font UiFont => uiFontResolved ??= new("Microsoft YaHei UI", 9f);

    private static Font? boldFontResolved;
    private static Font BoldFont => boldFontResolved ??= new("Microsoft YaHei UI", 9f, FontStyle.Bold);

    private static Font? statusFontResolved;
    private static Font StatusFont => statusFontResolved ??= new("Microsoft YaHei UI", 14f, FontStyle.Bold);
    private static readonly Regex AuthUrlRegex = new(
        "https?://127\\.0\\.0\\.1:\\d+/\\?token=[^\\s\\\"'<>\\x1b]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnsiRegex = new(
        "\u001b\\[[0-9;?]*[ -/]*[@-~]",
        RegexOptions.Compiled);
    /// <summary>
    /// 引擎日志里的认证 token 脱敏（TruncateEngineLog 落盘前用）。
    /// 字符集与 AuthUrlRegex 的 token 段一致：URL 查询参数形式 (?|&)token=...，
    /// 吃到空白/引号/ANSI 转义为止——与"每行一个 URL"的日志形态对齐。
    ///
    /// <b>IgnoreCase 不能省</b>：AuthUrlRegex 带 IgnoreCase，它会把 <c>?Token=</c>、
    /// <c>?TOKEN=</c> 一并识别成认证 URL；而这里只认小写的 <c>token=</c>，
    /// 于是同一个 token 会被抓进"已识别为认证 URL"的分支、却躲过脱敏——
    /// 两条正则对"什么算 token 参数"的认定不一致，而漏掉的那一半正好是明文落盘。
    /// 捕获组写法与 AuthUrlRegex 的 (?|&) 对齐，两条保持同形。
    /// </summary>
    private static readonly Regex AuthTokenRedactRegex = new(
        "([?&])token=[^\\s\\\"'<>\\x1b]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>
    /// 命令行里"独立的数字 token"。端口收窄专用：必须**整体**等于本启动器的端口才算数。
    /// 前后都不允许紧邻数字、小数点或任何单词字符，于是 "30801" 被当成一个整体而不是
    /// "3080"，"3080x" 也不会被截成 "3080"。
    ///
    /// <b>ECMAScript 标志不能去掉</b>：.NET 默认的 <c>\w</c>/<c>\d</c> 含 Unicode——
    /// 实测 <c>\w</c> 匹配「的」、<c>\d</c> 匹配全角「０」。那会让"端口紧邻一个汉字"
    /// 被判成"没提端口"，也就是**漏杀真残留**：而这条收窄的失手方向不该是这个。
    /// ECMAScript 下两者退化为 ASCII，端口紧邻汉字照样命中（这才是想要的），
    /// 真正的分界仍然落在数字与字母上——30801、3080x 都会被放过。
    /// </summary>
    private static readonly Regex PortTokenRegex = new(
        @"(?<![\w.])[0-9]{1,5}(?![\w.])",
        RegexOptions.Compiled | RegexOptions.ECMAScript);
    /// <summary>
    /// 残留进程匹配的两条宽松正则。与 AuthUrlRegex/PortTokenRegex 同一纪律：
    /// 提为 static readonly + Compiled（每个进程都要过一遍它们，内联字面量
    /// 每次调用都要重新解析模式）。IgnoreCase 用选项表达，不再用内联 (?i)。
    /// </summary>
    private static readonly Regex DshCommandRegex = new(
        @"(^|[\\/\s])dsh(?:\.cmd)?(?:[\\/](?:lib|bin))?\s+(?:web|--profile\s+web)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NpxDshCommandRegex = new(
        @"\bnpx(?:\.cmd)?\b.*\b(?:@deepseek-ai[\\/]dsh|dsh)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>
    /// `npm view &lt;pkg&gt; version` 的合法输出形态。npm 的 version 字段恒为 semver，
    /// 必以数字开头（1.2.3 / 1.2.3-rc.2 / 1.2.3+build 都过），
    /// 而 npm 自己的 warn / notice / ERR! 文本、以及任何错误摘要都过不了这道闸。
    /// ECMAScript：<c>\d</c> 只认 ASCII，避免全角数字混进来。
    /// </summary>
    private static readonly Regex NpmVersionLineRegex = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.\-+]*)?$",
        RegexOptions.Compiled | RegexOptions.ECMAScript);
    /// <summary>
    /// 探针专用 HttpClient。<b>AutomaticDecompression 不可省</b>：DSH 的 web server 带
    /// gzip 中间件（dsh-host-webserver 的 compression 配置，默认 none 但可开），
    /// 一旦对方开了压缩，不解压就只能看到二进制，body 里的身份标记永远匹配不上——
    /// 而"匹配不上"在两条探针上都意味着**误判成不是 DSH**，是必须避免的方向。
    /// </summary>
    private static readonly HttpClient LocalHttp = new(new HttpClientHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly Panel lamp = new();
    private readonly Label status = new();
    private readonly Label info = new();
    private readonly LinkLabel link = new();
    private readonly Button startButton;
    private readonly Button restartButton;
    private readonly Button upgradeButton;
    private readonly Button versionsButton;
    private readonly Button foldersButton;
    // 折叠机制已移除：主按钮随状态切换语义后，7 个按钮能排成一行，不再需要展开/收起状态。
    private readonly Button refreshButton;
    private readonly Button envButton;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly string urlFile = Path.Combine(LocalAppDir, "web-url.txt");
    private readonly string settingsFile = Path.Combine(LocalAppDir, "settings.txt");
    /// <summary>
    /// 引擎固定安装目录。此前引擎装在 npx 的哈希缓存
    /// （%LOCALAPPDATA%\npm-cache\_npx\&lt;hash&gt;）里，三个后果都很难受：
    /// ① 每次启动 npx 都要先向 registry 查一次 @latest，有新版本就整棵静默重装
    ///   （实测 91 秒、零输出，界面像死住）；
    /// ② 被中断的半成品无法原地修复（npm 只信 .package-lock.json，不会补半截包），
    ///   只能整个删掉重下；
    /// ③ 目录名是哈希，位置不可预测。
    /// 现在改为：装到固定目录 → 启动直接跑 bin.js（零网络、零重装、不查版本）；
    /// 升级只在你点「升级引擎」时发生，且先装到 engine.tmp，成功才替换正式目录。
    /// </summary>
    /// ⚠ 下面这些路径字段一律惰性求值（原为 static readonly）。它们全都链在
    /// <see cref="LocalAppDir"/> 上，所以哪怕只把 LocalAppDir 惰性化、这几个仍是
    /// static readonly，类型初始化器照样会顺带把它们算出来——惰性化就等于没做。
    private static string? engineDirResolved;
    private static string engineDir => engineDirResolved ??= Path.Combine(LocalAppDir, "engine");
    private static string? engineStageDirResolved;
    private static string engineStageDir => engineStageDirResolved ??= Path.Combine(LocalAppDir, "engine.tmp");
    private static string? engineOldDirResolved;
    private static string engineOldDir => engineOldDirResolved ??= Path.Combine(LocalAppDir, "engine.old");
    /// <summary>
    /// 归档认领的中转目录：engine.old 被某个进程原子改名到这里之后、归档成
    /// engine.&lt;版本&gt; 之前，它一直待在这里。存在即表示"这份数据已被认领"。
    /// 它不是新造的一种垃圾，而是 engine.old 本身——中途被杀留下的这一份，
    /// 下一轮启动会先把它收尾（见 MigrateEngineOldToSlot）。
    /// </summary>
    private static string? engineMigratingDirResolved;
    private static string engineMigratingDir => engineMigratingDirResolved ??= Path.Combine(LocalAppDir, "engine.migrating");
    /// <summary>认领目录名的前缀：engine.migrating.&lt;8 位十六进制&gt;。见 ClaimMigratingDir。</summary>
    private const string EngineClaimPrefix = "engine.migrating.";
    /// <summary>
    /// 认领目录判"陈旧"的时间。认领只在归档期间存在（秒级），半小时足够区分
    /// "另一个执行者正拿着它"与"上次死在认领之后"。
    /// </summary>
    private static readonly TimeSpan ClaimStaleAfter = TimeSpan.FromMinutes(30);
    private static string? webProfileDirResolved;
    /// <summary>web profile 目录。此前这个路径在多处各写了一遍，容易写歪，统一到这里。</summary>
    private static string webProfileDir => webProfileDirResolved ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "web");
    /// <summary>
    /// 当前用户主目录。宽松进程匹配（IsHarnessCommand）只认它之下的路径——
    /// 互斥体是 Local\（每登录会话一个），管不到别的会话，不加这道限定就会把
    /// 另一个会话里的引擎整树杀掉。
    /// </summary>
    private static string? userHomeDirResolved;
    private static string userHomeDir =>
        userHomeDirResolved ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // UI 线程写（启动/停止路径）、tail 循环与 Exited 回调（线程池）读。
    // 引用读写在所有平台上都是原子的，但可见性没有语言规范保证——与本文件对
    // engineTailToken 的同一纪律：读侧一律走 Volatile.Read。
    private Process? dshProcess;
    private CancellationTokenSource? startCts;
    /// <summary>
    /// busy 态的**所有权标记**，与 <see cref="startCts"/> 刻意分开。
    ///
    /// 为什么不能拿 startCts 判所有权：CancelPendingStart 会把 startCts 置 null，
    /// 而"清空 startCts"恰恰是取消的语义。于是 busy 期间按一次 Esc（正是
    /// StopClickedAsync 忙碌分支干的第一件事），操作自己的 finally 里
    /// ReferenceEquals(startCts, cts) 就恒为 false → 跳过 EndBusy →
    /// busy 全文件再无第二处复位 → 全部按钮永久禁用，只能重启启动器。
    /// 启动/升级最长 15 分钟，按一次 Esc 就锁死整窗。
    ///
    /// 这里用 EnterBusy 时记下的 owner 引用做判定：取消只动 startCts，
    /// owner 不受影响，"我还是这次忙碌态的主人"因此始终可判。
    /// 后继操作 EnterBusy 会覆盖它，于是旧操作的 finally 也不会误清后来者的状态。
    /// </summary>
    private object? busyOwner;
    private readonly List<string> recentOutput = new();
    // ── 引擎 stdio 文件化（与启动器生死解耦）──────────────────────────────
    // 原来的管道模式里，读端挂在启动器进程上：启动器一死（强杀/崩溃/更新），
    // 引擎下一次写日志就随断管退出（本机实测 ~1 秒 EXIT code=1）。
    // 现在引擎经 cmd 把 stdout/stderr 重定向进 engine-stdio.log，本进程按增量
    // tail 读文件复现代替管道事件。效果：引擎不再随启动器陪葬——更新/崩溃后
    // 新实例探到 web-url.txt 仍可用就直接复用还在跑的引擎，Web 会话零打断。
    private static string? engineStdioLogResolved;
    private static string engineStdioLog => engineStdioLogResolved ??= Path.Combine(LocalAppDir, "engine-stdio.log");
    /// <summary>
    /// tail 游标（字节偏移 + UTF8 解码器 + 半行尾巴）。刻意是**引用类型并整体替换**：
    /// 新引擎起跑时 UI 线程直接 new 一个新的换上（volatile 写），旧循环在入口捕获的
    /// 旧游标继续自洽地读完退场——两边各持各的状态，不存在共享可变字段。
    /// 此前 pos/decoder/remainder 是三个散装字段，旧循环在 FeedEngineLogChunk
    /// 内部一边读一边写，与新启动的复位交错时，旧循环会拿被清零的 pos 续读、
    /// 把新引擎的日志当旧文件重新分派一遍（窄竞态。整替游标封掉跨代交错；
    /// 1.4.1 再把代际核对下沉到**逐行分发**——换代瞬间旧循环可能正卡在
    /// 一次 Feed 中途，刚读进的整段里混着上一会话的 token 行，只在循环顶
    /// 核对的话整段照发不误）。
    /// </summary>
    private volatile LogCursor engineLogCursor = new(0);
    /// <summary>代际标记：新引擎起跑后旧循环自行退场。volatile——UI 线程 `++`、
    /// tail 后台循环每行读它做分发守卫；要的是语言规范的可见性保证，
    /// 不是"x86 上恰好没出事"（与上面的 engineLogCursor 同一纪律）。</summary>
    private volatile int engineTailToken;

    /// <summary>
    /// 这个 tail 循环是否还属于当前代。**纯函数、可单测**——它守着的是
    /// "旧会话的 token 行一行都不许发出去"这条不变量，而它错了不报错：
    /// 上一会话的 <c>?token=</c> 行会回填 <see cref="authenticatedUrl"/>、
    /// 弹死链接标签页、写脏 web-url.txt，启动等待循环还会在新引擎就绪之前
    /// 提前判成"启动成功"。
    ///
    /// 三处守卫（循环顶 ×2、逐行分发 ×1）必须走这一个判定，不能各写各的
    /// <c>token != engineTailToken</c>：多写一次就多一处能被"顺手改坏"的地方。
    /// </summary>
    internal static bool TailGenerationAlive(int loopToken, int currentToken) => loopToken == currentToken;

    /// <summary>
    /// **杀引擎后必须退役当前代际令牌**（两条杀进程路径都要调，见
    /// <see cref="StopHarnessProcessesAsync"/> 与 <see cref="StopEngineForExit"/>）。
    ///
    /// 此前只有"新引擎起跑"会换令牌，于是杀掉引擎到新引擎起跑之间的那段窗口里，
    /// 旧 tail 循环的退场排空（最多 4×150 ms）仍然拿着当前令牌通过逐行守卫：
    /// 刚被删掉的 web-url.txt 被重写成**过期 token**、authenticatedUrl 复活，
    /// 紧接着的等待循环（<c>authenticatedUrl is not null → return</c>）会在
    /// 新引擎还没输出任何日志时就提前判"启动成功"。
    ///
    /// 令牌只增不减、不复用，所以"退役"就是加一：旧循环下一次读就发现自己过期。
    /// </summary>
    private void RetireEngineTail() => Interlocked.Increment(ref engineTailToken);
    // volatile：tail 后台线程写（HandleProcessLine）、UI 线程在 250ms 轮询里读它判
    // "启动成功"。实际内存模型 + Task.Delay 的隐式栅栏让陈旧读几乎不可能被观察到，
    // 但本文件对 engineTailToken 的可见性用的是 Interlocked（语言规范保证，不靠
    // "x86 上恰好没出事"），这两个字段是同一并发形状，执行同一纪律。
    private volatile string? authenticatedUrl;
    private volatile bool isOn;
    private volatile bool busy;
    private volatile bool refreshing;
    private volatile bool closing;
    // 与 authenticatedUrl 同点写入（tail 线程）、跨线程读；只用于界面展示
    // （状态文案与提示），不参与任何判定分支，int 原子读写即可。
    private int lastPort = DefaultPort;
    // 安装进度的 200ms 节流戳记：npm 的 stdout/stderr 各是一个线程池线程并发调用
    // TrackInstallLine，"读-比较-写"无锁时两个线程会同时通过节流检查（后果只是多刷
    // 一次界面，无害），但 x86 上 8 字节 DateTime 读不保证原子。存 UTC ticks 用
    // Interlocked 读，与 engineTailToken 同一纪律（语言规范保证，不靠平台恰好没出事）。
    private long lastInstallInfoAtTicks;
    private readonly CheckBox autoUpdateCheckbox = new();
    private readonly ToolTip tooltip = new();
    internal HarnessForm()
    {
        // 所有控件的坐标都是按 96 DPI 硬编码的像素值。没有这一行时，在 125%/150%
        // 缩放的机器上（同学的笔记本很常见）窗体会被整体拉伸而不是按比例重排，
        // 文字与控件会错位/被裁。Dpi 模式让 WinForms 按当前 DPI 重算尺寸。
        // 注意：本机是 96 DPI / 100% 缩放，这条改动在这里无法验证，只能保证方向正确。
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "DeepSeek Harness 控制台";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.White;
        Font = UiFont;
        ApplyWindowIcon();

        var panel = new Panel
        {
            Location = new Point(22, 20),
            Size = new Size(424, 138),
            BackColor = Color.FromArgb(246, 248, 252),
            TabIndex = 0
        };
        Controls.Add(panel);

        lamp.Location = new Point(28, 39);
        lamp.Size = new Size(36, 36);
        lamp.Paint += DrawLamp;
        panel.Controls.Add(lamp);

        status.Location = new Point(82, 21);
        status.Size = new Size(320, 30);
        status.Font = StatusFont;
        status.Text = "检测中";
        status.TabIndex = 0;
        panel.Controls.Add(status);

        info.Location = new Point(84, 55);
        info.Size = new Size(320, 22);
        info.ForeColor = Color.FromArgb(108, 114, 126);
        info.TabIndex = 1;
        panel.Controls.Add(info);

        link.Location = new Point(83, 80);
        link.Size = new Size(320, 25);
        link.AutoEllipsis = true;
        link.TabIndex = 2;
        link.AccessibleName = "打开 DeepSeek Harness 控制台";
        link.LinkClicked += (_, _) => OpenKnownUrl();
        // 键盘用户：Tab 到链接后回车/空格也能打开，不能只依赖鼠标点。
        link.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Space)) return;
            e.Handled = true;
            OpenKnownUrl();
        };
        panel.Controls.Add(link);

        // 文案必须与真实行为一致：插件更新现在发生在引擎启动「之后」（并行/后台），
        // 且改的是 profile 的 node_modules，所以生效时机是下一次启动。
        autoUpdateCheckbox.Text = "启动后更新插件";
        autoUpdateCheckbox.Checked = LoadAutoUpdateSetting();
        autoUpdateCheckbox.AutoSize = true;
        autoUpdateCheckbox.Location = new Point(28, 110);
        autoUpdateCheckbox.ForeColor = Color.FromArgb(88, 94, 104);
        autoUpdateCheckbox.TabIndex = 3;
        autoUpdateCheckbox.CheckedChanged += (_, _) => SaveAutoUpdateSetting();
        panel.Controls.Add(autoUpdateCheckbox);

        // ── 底部一行：7 个按钮全部同尺寸排成一行 ─────────────────────────────────
        // 演化过程（每一步都由实测数据推动，不是拍脑袋）：
        //  ① 最初 6 个主按钮各 94px 挤一行（右边界 524/560），调用频率天差地别的操作占同样宽度；
        //  ② 加目录/版本/升级后彻底排不下，做过"折叠 + 向下展开"；
        //  ③ 但那样主按钮只有 2 个字却占了 324px 宽（还用了 10pt），比旁边按钮大 6 倍——
        //     把主按钮缩到与其余按钮完全一致后，7 个按钮反而能一行放下。
        // 实测（9pt 粗体）：所有两字按钮文字都是 32px，加 20px 内边距 = 52px；
        // 8 × 52 + 7 × 8 = 472px，可用 496px。所以折叠机制被整个删掉了——
        // 能一行放下就不需要它，少一个交互状态也少一处出错的地方。
        //
        // 尺寸统一是刻意的：主按钮不再特殊，同高（BottomRowHeight）、同字体、同宽。

        // 主按钮随状态切换语义（未运行→启动，运行中→停止，见 ApplyPrimaryActionLabel），
        // 所以不再有独立的「停止」按钮——之前两套设计叠加，运行中一行出现两个红色「停止」。
        // 「重启」保持独立：它在两种状态下都有意义（清残留后拉起）。
        startButton = NewActionButton("启动", "启动或停止 Harness 引擎。回车键等效",
            Color.FromArgb(34, 170, 85));
        restartButton = NewActionButton("重启", RestartAccessibleName,
            Color.FromArgb(238, 148, 32));
        refreshButton = NewActionButton("刷新", RefreshAccessibleName, Color.FromArgb(58, 124, 240));
        envButton = NewActionButton("环境", EnvAccessibleName, Color.FromArgb(114, 122, 143));
        foldersButton = NewActionButton("目录", FoldersAccessibleName, Color.FromArgb(58, 124, 240));
        versionsButton = NewActionButton("版本", VersionsAccessibleName, Color.FromArgb(88, 94, 104));
        upgradeButton = NewActionButton("升级", UpgradeAccessibleName, Color.FromArgb(114, 122, 143));

        Controls.AddRange(new Control[]
        {
            startButton, restartButton,
            refreshButton, envButton, foldersButton, versionsButton, upgradeButton
        });

        // 尺寸约束必须在 LayoutBottomRow 之前：布局按实际 ClientSize 反推行位置，
        // 而构造完成前 ClientSize 还是默认值。之前顺序反了，靠 Shown 里重排一次才救回来。
        SetFixedClientSize(ClientWidth, TargetClientHeight);
        LayoutBottomRow(rowHeight: BottomRowHeight);

        // 尺寸自检：记录设置前后来定位宽度被谁改小（曾经的坑：句柄未创建时设尺寸会被钳小）。
        if (LayoutDump.Enabled)
        {
            LayoutDump.Capture($"构造完成 Size={Size.Width}x{Size.Height}", this, startButton, upgradeButton);
        }

        // 布局自检：DSH_LAYOUT_DUMP=1 时把窗口与按钮的实际几何写进 layout-dump.txt 并退出。
        // 加这个是因为靠截图/自动化去验证 Windows 布局既慢又不可靠（试过 UIAutomation 与
        // PrintWindow，都拿不到稳定结果），而布局对不对是可以直接测量的事实——
        // 它已经抓出过"按钮溢出客户区""居中把行推出边界""MinimumSize 与 ClientSize 自相矛盾"
        // 以及"边框差值算错导致客户区被压缩"四个真问题。
        Shown += (_, _) =>
        {
            // 客户区尺寸要等窗口真正显示后才稳定（之前 Set 的值可能被 DPI 换算与屏幕边界改写），
            // 所以这里按最终尺寸重排一次。
            LayoutBottomRow(rowHeight: BottomRowHeight);

            // 自检入口：DSH_LAYOUT_DUMP=1 时记录几何后退出。
            // 曾试图在此改宽度测自适应，但窗体尺寸已锁（Min=Max），赋值只会被钳回原值——
            // 改了也是同一个几何，测了等于没测；宽度自适应由按钮重排逻辑保证。
            if (Environment.GetEnvironmentVariable("DSH_LAYOUT_DUMP") == "1")
            {
                LayoutBottomRow(rowHeight: BottomRowHeight);
                DumpLayout("显示后");
                Close();
                return;
            }
        };

        // 两个对话框平时要点按钮才出现，靠 DSH_LAYOUT_TEST=1 的独立入口验证
        // （见 Program.RunLayoutSelfTest），不走主窗体的 Shown。

        startButton.Click += async (_, _) =>
        {
            // 主按钮随状态切换语义：运行中点它是「停止」，否则是「启动」。
            if (isOn) await StopClickedAsync();
            else await RunStartAsync(startButton, "启动中", reuseExisting: true);
        };
        restartButton.Click += async (_, _) => await RunStartAsync(restartButton, "重启中", reuseExisting: false);
        refreshButton.Click += async (_, _) => await RefreshStatusAsync();
        envButton.Click += async (_, _) => await RunEnvCheckAsync();
        foldersButton.Click += (_, _) => OpenFoldersWindow();
        versionsButton.Click += (_, _) => OpenEngineVersions();
        upgradeButton.Click += async (_, _) => await RunEngineUpgradeAsync();
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();

        // 键盘可达性：回车 = 启动/停止（随主按钮），Esc = 停止引擎。
        // Esc 之前靠 CancelButton 绑在「停止」按钮上；独立停止按钮删除后改用 KeyPreview，
        // 语义不变（破坏性最小的那个动作），也不再依赖某个可见控件。
        AcceptButton = startButton;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            e.Handled = true;
            _ = StopClickedAsync();
        };
        ApplyAccessibility();
        Shown += async (_, _) =>
        {
            Activate();
            await RefreshStatusAsync();
            // await 之后窗体可能已经被关掉并释放（refreshTimer 是本窗体创建的，
            // 已在 Dispose 里 Dispose）。async void 里对已释放对象调 Start() 会抛
            // ObjectDisposedException，且**无人接管**——整个启动器当场崩掉。
            if (closing || IsDisposed) return;
            _ = RunStartAsync(startButton, "启动中", reuseExisting: true);
            refreshTimer.Start();
        };
        FormClosing += (_, _) =>
        {
            closing = true;
            refreshTimer.Stop();
            CancelPendingStart();
            // 之前这里只有 dshProcess?.Dispose()——Dispose 不结束进程，于是关窗后
            // 引擎继续在后台活着、占着端口，下次启动还能被"复用"到，泄漏被完全掩盖。
            // 关窗即退出：把引擎一并结束，不留占端口的后台进程；下次启动会重新拉起。
            StopEngineForExit();
        };
        UpdateButtons();
    }
}
