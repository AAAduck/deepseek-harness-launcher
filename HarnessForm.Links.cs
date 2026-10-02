// ── HarnessForm 的「认证链接读写与引擎日志脱敏」部分 ──────────────────────────────────────────
// 由 HarnessForm.cs 按本文件原有的「// ---- 分段 ----」分隔线机械切分而来。
// 切分只搬位置、不改任何一行成员代码；partial class 之间共享全部字段与成员。
// 各段清单见 README「文件说明」。改动请落在语义所属的那一段里。

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

    // ---- 链接与界面 ---------------------------------------------------------

    /// <summary>
    /// 无障碍与键盘可达性。屏幕阅读器读不出"开始/重启/停止"这种两字按钮是干什么的，
    /// 而状态与提示文字的变化（"正在启动…""启动失败"）此前也不会被主动播报。
    /// </summary>
    private void ApplyAccessibility()
    {
        AccessibleName = "DeepSeek Harness 控制台";
        AccessibleDescription = "启动与管理本机的 DeepSeek Harness Web 服务";

        // 状态与提示是纯展示控件，设置为礼貌播报区域，变化时会被读屏主动念出来。
        status.AccessibleName = "运行状态";
        status.LiveSetting = AutomationLiveSetting.Polite;
        info.AccessibleName = "状态详情";
        info.LiveSetting = AutomationLiveSetting.Polite;

        // 全部取自本类的无障碍名称常量（唯一真相源，见常量声明处的注释）——
        // 此前只有 upgradeButton 在这里被写死，其余三个只写在构造与 EndBusy 里。
        restartButton.AccessibleName = RestartAccessibleName;
        restartButton.TabIndex = 1;
        refreshButton.AccessibleName = RefreshAccessibleName;
        refreshButton.TabIndex = 4;
        envButton.AccessibleName = EnvAccessibleName;
        envButton.TabIndex = 5;
        foldersButton.TabIndex = 7;
        versionsButton.AccessibleName = VersionsAccessibleName;
        versionsButton.TabIndex = 6;
        upgradeButton.AccessibleName = UpgradeAccessibleName;
        upgradeButton.TabIndex = 2;
        autoUpdateCheckbox.AccessibleName = AutoUpdateAccessibleName;
        // 主按钮的 Tab 顺序按视觉从左到右。
        startButton.TabIndex = 0;

        // 悬停提示（读屏走 AccessibleName，普通用户走 tooltip）。之前 ToolTip
        // 实例化后从没 SetToolTip 过任何控件，纯占资源——现在真的用起来。
        // 主按钮的提示在 ApplyPrimaryActionLabel 里随状态刷新（那里才读 isOn）。
        tooltip.SetToolTip(restartButton, "结束当前引擎并重新启动（会先清理残留进程与端口）");
        tooltip.SetToolTip(refreshButton, "重新检测引擎状态");
        tooltip.SetToolTip(envButton, "检测 Node、npm、pnpm、引擎、插件兼容性与端口");
        tooltip.SetToolTip(foldersButton, "打开 DeepSeek Harness 相关目录一览");
        tooltip.SetToolTip(versionsButton, "查看/切换/删除本机已安装的引擎版本");
        tooltip.SetToolTip(upgradeButton, "升级引擎到 npm 上的最新版本（升级前会做插件兼容性检查）");
        tooltip.SetToolTip(autoUpdateCheckbox,
            "引擎启动后在后台跑一次 pnpm update；改动下次启动生效，距上次成功更新满 20 小时才再跑一次");
    }

    private void ApplyWindowIcon()
    {
        // 让窗口图标与 DeepSeekHarness.exe 的图标一致（含标题栏和任务栏）。
        // ExtractAssociatedIcon 返回的 Icon **刻意不释放**：与静态字体同一策略——
        // 它被 Form.Icon 持有、窗体整个存活期都要用，销毁流程里提前释放会抛
        // ObjectDisposedException。只在构造时取一次、进程级一份，泄漏量可忽略。
        try
        {
            var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (exeIcon is not null) Icon = exeIcon;
        }
        catch { }
    }

    private void OpenKnownUrl()
    {
        var url = authenticatedUrl ?? TryReadUrlFile();
        if (url is null)
        {
            MessageBox.Show(this, "当前没有可用的认证链接，请点击“启动”获取。", "DeepSeek Harness");
            return;
        }
        OpenBrowser(url);
    }

    private void OpenBrowser(string url)
    {
        // Process 对象持有内核句柄，拿到即收、不等 GC；
        // 弹窗挂到主窗体上，避免跑到别的窗口后面看不见。
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, $"无法打开浏览器：{ex.Message}", "DeepSeek Harness");
        }
    }

    /// <summary>
    /// 启动新引擎前清理日志：保留文件末尾 8 MB，其余丢弃；同时把历史日志里的
    /// 认证 token 脱敏（见 AuthTokenRedactRegex）。
    /// 引擎 stdio 文件化后，日志由 cmd 以追加句柄持有、引擎运行期间持续增长。
    /// 不做轮转的话，长期复用（数周不重启）会让它膨胀到 GB 级。
    /// 这里在**每次真启动**（到得了 StartHarnessAsync 的路径）时清理，保留尾部供排查。
    /// 调用方保证清理之后才为新游标取起点（见 StartHarnessAsync 的换代顺序），
    /// 截掉的头部连同其余历史内容都不会被回放。
    /// </summary>
    private static void TruncateEngineLog()
    {
        const long MaxKeepBytes = 8L * 1024 * 1024;
        if (!File.Exists(engineStdioLog)) return;
        var info = new FileInfo(engineStdioLog);
        if (info.Length == 0) return;

        // 最多保留末尾 8 MB。注意不能按字节硬切——可能截断多字节 UTF-8 序列的中间。
        // 所以先按字节读，再从第一个完整换行符之后开始保留。
        var keepBytes = (int)Math.Min(info.Length, MaxKeepBytes);
        var buffer = new byte[keepBytes];
        using (var fs = new FileStream(engineStdioLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(-keepBytes, SeekOrigin.End);
            // **必须循环读满**。FileStream.Read 的契约允许短读（网络盘、被抢占的页、
            // 甚至某些过滤驱动），单次调用返回的字节数可以小于请求值。此处按单次
            // 结果算 keepFrom 与解码长度，就会在短读时**静默把尾部截短**——
            // 而"尾部"恰恰是最新、最该留住的那几行。两个失败出口都各自记录，
            // 读不满就退化成"已读到的部分"，而不是把未读的那截当成不存在。
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;          // EOF 或读取失败：就按已读的这些处理
                read += n;
            }
            // 找第一个完整行：跳过可能被截断的半个 UTF-8 字符（行首的不完整字节）
            var firstNewLine = Array.IndexOf(buffer, (byte)'\n', 0, read);
            // 没找到换行（极端情况）时**整段都保留**——此前这里把 firstNewLine 置 0 之后
            // 仍然 +1，于是"从头保留"实际变成了"丢掉第 1 个字节"，与本行注释相反。
            var keepFrom = firstNewLine < 0 ? 0 : firstNewLine + 1;

            // 历史日志里的认证 token 落盘前一律脱敏：web-url.txt 已经 DPAPI 加密，
            // 但同一个 token 还明文躺在引擎日志里，而且这份文件跨启动长期保留——
            // 能读磁盘的人不用碰 DPAPI 就能拿到旧 token，"token 加密落盘"的目标
            // 被同目录的这个文件抵消。脱敏只发生在换代清理这一刻：当前会话新写入的
            // 行不经过这里（tail 读取路径必须看到明文才能捕获认证链接），所以盘上
            // 只会留有"当前会话正在用"的那一个 token，下次重启即被清掉。
            // 此前小文件（≤8 MB）直接早退，旧 token 就一直躺在盘上——清理必须
            // 无条件执行，而不是只在文件超大时顺带做。
            var text = Encoding.UTF8.GetString(buffer, keepFrom, read - keepFrom);
            text = AuthTokenRedactRegex.Replace(text, "$1token=<redacted>");
            // 回写走"临时文件 + 同卷 Move"，与 DpapiFile.WriteAtomic / ConfigBackup.AtomicWrite
            // 同一条纪律：File.WriteAllBytes 是**原地覆写**，写到一半被杀/断电就留下半截
            // 引擎日志——而这份日志正是崩溃后唯一的现场。
            var tmp = engineStdioLog + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes(text));
                File.Move(tmp, engineStdioLog, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
    }

    private string? TryReadUrlFile()
    {
        // DPAPI 解密 + 旧版明文自动升级，全部封装在 DpapiFile.ReadAllText 里。
        var value = DpapiFile.ReadAllText(urlFile);
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = AuthUrlRegex.Match(value);
        if (!match.Success) return null;
        // **端口必须是本启动器那个**。AuthUrlRegex 接受任意端口的 127.0.0.1 链接，
        // 而 HandleProcessLine 捕获引擎日志里的 URL 时强制要求 urlPort == DefaultPort
        // ——同一纪律，这条读回路径此前漏了。不补的后果很具体：文件里躺着一个指向
        // 别的端口的认证链接时，ResolveUsableUrlAsync 会把它当候选，探测通过后
        // **把 token 作为 query 发给那台不相干的服务**（见「端口探针验身份」一条）。
        var port = ExtractPort(match.Value);
        return port is not null && port == DefaultPort ? match.Value : null;
    }

    private void TryDeleteUrlFile()
    {
        try { if (File.Exists(urlFile)) File.Delete(urlFile); } catch { }
    }

    private void EnterBusy(Button active, string text, object owner)
    {
        busy = true;
        busyOwner = owner;
        startButton.Enabled = restartButton.Enabled = false;
        upgradeButton.Enabled = false;
        // 引擎运行期间不允许切换版本：切换必须先停引擎，让用户在这里点会与启动流程打架。
        versionsButton.Enabled = false;
        // 「环境」在忙碌期间也禁用：它会改写状态文案并弹模态报告，
        // 与进行中的启动/升级互相踩（此前只有方法开头一道守卫，按钮还亮着）。
        envButton.Enabled = false;
        // 复选框同样要禁：RunStartAsync 在建插件更新任务时读一次、启动完成后再读一次，
        // 两次读之间隔着整个引擎启动（实测十几秒）。不锁住就可能两次读到不同的值，
        // 第二次读到 false 时会把一个还在跑 pnpm 的任务丢在身后（无人 await 的
        // 未观察异常 + 一个提前释放的 CTS）。复选框在 panel 里，禁按钮并不会连带禁它。
        autoUpdateCheckbox.Enabled = false;
        active.Text = text;
        // 忙碌时按钮文字变成"启动中/重启中"，读屏就再也读不出它是哪个按钮了；
        // busyText 本身自描述，直接作为 AccessibleName。
        active.AccessibleName = text;
        status.Text = text;
        status.ForeColor = WarnColor;
        lamp.Invalidate();
    }

    private void EndBusy()
    {
        busy = false;
        busyOwner = null;
        // 恢复所有在 EnterBusy 里被改成"xx中"的文字。此前只恢复主按钮与升级，
        // 点过「重启」后 restartButton 会永远停在"重启中"——回归过一次的坑。
        restartButton.Text = "重启";
        upgradeButton.Text = "升级";
        envButton.Text = "环境";
        // AccessibleName 同样要恢复：EnterBusy 把它改成了 busyText，而读屏只念这个名字。
        // 漏掉的后果是点过一次「重启」之后，这个按钮**永久**对外自称"重启中"，
        // 比文字没恢复更难被发现（看得到界面的人看不出来）。
        // 取值引用常量，不再各写一份字面量——三处（构造 / ApplyAccessibility / 这里）
        // 各写一份时，任何一处漂移都只表现为"读屏在撒谎"，不会编译报错。
        restartButton.AccessibleName = RestartAccessibleName;
        upgradeButton.AccessibleName = UpgradeAccessibleName;
        envButton.AccessibleName = EnvAccessibleName;
        // 主按钮的文字/AccessibleName 由 ApplyPrimaryActionLabel 按运行状态恢复。
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (busy) return;
        startButton.Enabled = true;
        restartButton.Enabled = true;
        upgradeButton.Enabled = true;
        versionsButton.Enabled = true;
        envButton.Enabled = true;
        autoUpdateCheckbox.Enabled = true;   // 配 EnterBusy 里的同一条注释
        // 主按钮的文字/颜色随运行状态变，这里是唯一会在状态变化后被调用的地方。
        ApplyPrimaryActionLabel();
    }

    private void DrawLamp(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(isOn ? Color.FromArgb(34, 170, 85) : Color.FromArgb(178, 182, 190));
        e.Graphics.FillEllipse(brush, 4, 4, 28, 28);
    }

    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>
    /// 窗体级资源的收尾。Timer 与 ToolTip 是本窗体创建的，随窗体释放；
    /// LocalHttp（静态 HttpClient）与静态字体是**进程级**对象，刻意不在此释放——
    /// 后台任务与后续销毁流程仍可能引用它们。这里先置 closing，
    /// 让 1.5 秒刷新与后台循环不再发起新请求、不再碰界面。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            try { refreshTimer.Stop(); } catch { }
            try { refreshTimer.Dispose(); } catch { }
            try { tooltip.Dispose(); } catch { }
            // 静态字体属于进程生命周期，不在这里释放：WinForms 控件仍可能在
            // 后续的销毁流程里引用它们，提前释放会抛 ObjectDisposedException。
        }
        base.Dispose(disposing);
    }

    private static int? ExtractPort(string url)
    {
        try { return new Uri(url).Port; } catch { return null; }
    }

    // internal：CollectProcessTreeIds（杀进程树的纯函数内核）吃它、单测要构造它
    //（InternalsVisibleTo）。此前 private，那个内核就没法被钉住。
    internal sealed record ProcessRecord(int Id, int ParentId, string Name, string CommandLine, DateTime StartTime);
}
