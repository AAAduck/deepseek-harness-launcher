// ── HarnessForm 的「引擎安装与升级（含「切换到此版本」）」部分 ──────────────────────────────────────────
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

    // ---- 引擎安装与升级 -----------------------------------------------------

    private string EngineEntryScript => Path.Combine(
        engineDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

    private static string? ReadEngineVersion(string dir)
    {
        // 1.5 秒的状态刷新每一轮都要读引擎版本，而这里是一次 File.ReadAllText +
        // JsonDocument.Parse，且整段挂在 UI 线程上。engine 目录下的 package.json
        // 平时只有几百字节，单次确实便宜——可它在**每一次轮询**里都发生。
        // 用 (最后写入时间, 长度) 做一道缓存：内容没动就复用，动了就重读。
        // 判据是够的——那份文件由本程序写、只在我们自己替换引擎目录时变，
        // 而那次替换必然同时改 mtime 与长度。拿不到任一项就退回实读（正确性优先）。
        try
        {
            var manifest = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            var info = new FileInfo(manifest);
            var stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
            var cached = Volatile.Read(ref cachedEngineVersion);
            if (cached is not null && cached.Stamp == stamp) return cached.Version;

            string? version;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            }
            catch { version = null; }
            Volatile.Write(ref cachedEngineVersion, new VersionCache(stamp, version));
            return version;
        }
        catch { return null; }
    }

    /// <summary>
    /// 引擎版本的 mtime 缓存。跨线程读写（UI 线程的刷新 + 后台线程的安装/切换路径
    /// 都会读它），所以经 <see cref="Volatile"/> 发布。
    /// <b>刻意做成引用类型而不是可空元组</b>：<c>Volatile.Read/Write&lt;T&gt;</c> 只接受
    /// 引用类型，可空元组是值类型——编译不过。写成 class 之后整个对象原子替换，
    /// 两个字段（stamp 与 version）也不会被读到"新的 stamp 配旧的 version"。
    /// </summary>
    private sealed class VersionCache
    {
        internal VersionCache((long Ticks, long Length) stamp, string? version)
        {
            Stamp = stamp;
            Version = version;
        }

        internal (long Ticks, long Length) Stamp { get; }
        internal string? Version { get; }
    }

    private static VersionCache? cachedEngineVersion;

    /// <summary>
    /// 任何动了引擎目录的动作之后都必须调用，否则状态栏会继续显示旧版本号。
    /// 保守做法：在替换/切换/删除的收尾处都清一次——缓存本身是 mtime 驱动的，
    /// 这里只是把"换了目录但 mtime 恰好相同"的极端情形也覆盖掉。
    /// </summary>
    internal static void InvalidateEngineVersionCache() => Volatile.Write(ref cachedEngineVersion, null);

    /// <summary>
    /// dsh 包自己声明的<b>直接依赖</b>里，哪些在引擎目录里没落地（纯函数内核、可单测）。
    /// <paramref name="packageExists"/> 接收包名（如 <c>@deepseek-ai/dsh-base</c>），
    /// 由调用方决定去哪儿找——把 IO 从判定里摘出去，这条逻辑才能被测试钉住。
    /// </summary>
    internal static IReadOnlyList<string> FindMissingEngineDependencies(
        IEnumerable<string> declaredDependencies, Func<string, bool> packageExists)
    {
        var missing = new List<string>();
        if (declaredDependencies is null) return missing;
        foreach (var name in declaredDependencies)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!packageExists(name)) missing.Add(name);
        }
        return missing;
    }

    /// <summary>
    /// 目录里是不是一份<b>能用</b>的引擎：入口脚本在、版本读得出、
    /// 且 dsh 声明的直接依赖都已落地。npm 逐包解包没有事务性，强杀/断电会留下
    /// "本体在、依赖缺一半"的半截目录——只查"版本 + bin.js"时它会一路晋升成
    /// 活动引擎，造成 UI 内无解的死局（时间线见 DESIGN-NOTES.md §4）。
    /// 纯本地检查（不联网/不执行代码）；误判"不完整"只是重装一次（自愈），
    /// 误判"完整"是死局——判定方向必须偏向前者。
    /// </summary>
    internal static bool IsCompleteEngineInstall(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        if (!File.Exists(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"))) return false;
        if (ReadEngineVersion(dir) is null) return false;
        return FindMissingEngineDependencies(ReadEngineDependencyNames(dir), MakeEngineDependencyProbe(dir)).Count == 0;
    }

    /// <summary>
    /// 读出引擎目录里 dsh 声明的直接依赖名。读不到就当作"没有依赖"——
    /// 此时 <see cref="FindMissingEngineDependencies"/> 返回空、判定通过，
    /// 与升级前的行为一致（读不到清单本身已由"版本读得出"这道闸拦过一次）。
    /// </summary>
    private static IEnumerable<string> ReadEngineDependencyNames(string dir)
    {
        var names = new List<string>();
        try
        {
            var manifest = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(manifest)) return names;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps) ||
                deps.ValueKind != JsonValueKind.Object) return names;
            foreach (var p in deps.EnumerateObject())
                if (!string.IsNullOrWhiteSpace(p.Name)) names.Add(p.Name);
        }
        catch { }
        return names;
    }

    /// <summary>
    /// 依赖是否"已落地"的探针。npm 默认把依赖<b>提升</b>到顶层 node_modules，
    /// 只有版本冲突时才会嵌套到 <c>@deepseek-ai/dsh/node_modules</c> 下——
    /// 两个位置都认，否则一次无害的嵌套就足以把整份好引擎误判成半截、每次启动重装 214 MB。
    /// </summary>
    private static Func<string, bool> MakeEngineDependencyProbe(string dir)
    {
        var root = Path.Combine(dir, "node_modules");
        var nested = Path.Combine(root, "@deepseek-ai", "dsh", "node_modules");
        return name =>
        {
            try
            {
                return File.Exists(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar), "package.json")) ||
                       File.Exists(Path.Combine(nested, name.Replace('/', Path.DirectorySeparatorChar), "package.json"));
            }
            catch { return false; }
        };
    }

    /// <summary>
    /// 保证固定目录里有一份可用的引擎。只做本地检查：不联网、不查最新版、不重装。
    /// 引擎由「升级引擎」按钮显式升级，因此这里在正常启动路径上几乎零成本。
    /// </summary>
    private async Task EnsureEngineAsync(string node, CancellationToken ct)
    {
        RecoverEngineSwap();
        if (IsCompleteEngineInstall(engineDir)) return;

        // 走到这里 = 活动引擎不可用。两种成因要走不同的说法，因为用户该做的事不同：
        //   • 目录根本不存在 → 首装，说"只此一次"。
        //   • 目录在但不完整（中断安装的半截引擎被提升上来了）→ 说清楚这是**修复**，
        //     否则用户看到"正在安装"会以为程序记错了、清缓存重装也没用。
        if (Directory.Exists(engineDir))
            SetInfo("检测到引擎目录不完整（上次安装被中断过），正在自动重新安装修复…");
        else
            SetInfo("首次运行：正在把 DSH 引擎装到固定目录（只此一次，之后启动不再联网）");

        // 版本锁优先：写了 engine-version.txt 就只装那个版本，永不跟随最新版。
        // 这是"以插件为主"的开关——插件只在某个引擎版本上验证过时，把它钉住。
        var pinned = ReadPinnedEngineVersion();
        if (pinned is not null)
        {
            SetInfo($"检测到引擎版本锁，安装指定版本 {pinned}");
            await InstallEngineAsync(node, pinned, await ResolveNpmRegistryAsync(node, ct), ct, ReadEngineVersion(engineDir));
            return;
        }

        // 否则先查一次精确版本号再装。原因：写 "latest" 的话重装会忽略锁文件、抓当天最新版，
        // 同一份 manifest 在不同日子装出不同版本，出了问题无法复现。
        // 查询用独立的短超时（20 秒）——实测安装本身要 67 秒，不能在查询上再赔 60 秒；
        // 查不到只是退回 latest，不影响安装成功。
        string? latest = null;
        try
        {
            using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            queryCts.CancelAfter(TimeSpan.FromSeconds(20));
            latest = await GetLatestEngineVersionAsync(node, queryCts.Token);
        }
        catch (OperationCanceledException) { }
        if (ct.IsCancellationRequested) return;
        if (latest is null) SetInfo("查不到版本号，按 latest 安装（网络受限时的降级路径）");
        // 活动版本一并传给 InstallEngineAsync：安装期间可能长达几分钟，替换前要在
        // migrateGate 内核对它没被别的启动器换掉（见 InstallEngineAsync 的参数说明）。
        // 这里传的是"不完整目录也读得到的版本号"——不一致正是应该中止的信号。
        await InstallEngineAsync(
            node, latest ?? "latest", await ResolveNpmRegistryAsync(node, ct), ct, ReadEngineVersion(engineDir));
    }

    /// <summary>
    /// 底部那 7 个按钮：几何由 LayoutBottomRow 统一排，这里只定外观。
    /// 全部同高、同字体、同内边距——主按钮不再特殊（它曾经 324px 宽、10pt，
    /// 比旁边按钮大出 6 倍）。宽度先给占位值，布局时按实测文字改写。
    /// </summary>
    private Button NewActionButton(string text, string accessibleName, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(52, BottomRowHeight),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = BoldFont,
            Cursor = Cursors.Hand,
            AccessibleName = accessibleName,
            AutoEllipsis = true   // 万一某处字体比预期宽，宁可显示省略号也不要裁掉半个字
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private const int BottomRowHeight = 34;
    /// <summary>
    /// 期望的客户区高度。窗体固定尺寸，这个值决定整体高度。
    /// 注意 MinimumSize/MaximumSize 用的是**窗口**尺寸（含标题栏与边框），
    /// 直接拿客户区高度去设会让客户区少掉标题栏那几十像素——展开行/按钮行因此溢出过。
    /// 所以下面用 SetFixedClientSize 一次性把三者算自洽。
    /// </summary>
    private const int TargetClientHeight = 232;
    /// <summary>窗体宽度固定不变。</summary>
    private const int ClientWidth = 560;

    /// <summary>
    /// 把 7 个按钮排成一行：宽度按实测文字 + 相同内边距，整体居中，垂直方向贴近底边。
    ///
    /// 为什么不写死 94px：中文两字按钮在 9pt 粗体下实测 32px，三字 48px，
    /// 写死宽度要么浪费空间要么把长文案挤到省略号。按文字量算，改文案时布局自己会跟着走。
    ///
    /// 宽度与行位置都用**实际 ClientSize** 而不是常量：本机 DeviceDpi 报 120（1.25 倍），
    /// 逻辑 560 会被换算成设备 700 并因此被屏幕截断，写死就会算歪（自检抓出过"按钮排到 202
    /// 而客户区只有 188"）。所以行位置由实际客户区反推，并在窗口显示后重排一次。
    /// </summary>
    private void LayoutBottomRow(int rowHeight)
    {
        var buttons = new List<Button>
        {
            startButton, restartButton,
            refreshButton, envButton, foldersButton, versionsButton, upgradeButton
        };
        var gap = 8;

        var widths = buttons.Select(b => TextWidth(b.Text, b.Font) + 20).ToList();
        var total = widths.Sum() + gap * (buttons.Count - 1);

        // 超出可用宽度时按比例压缩间隙，保证一定放得下。下界 2px：
        // 压到 0 会让按钮粘在一起，不如交给 AutoEllipsis 处理文字。
        var available = ClientSize.Width - 8;
        if (total > available)
            gap = Math.Max(2, gap - (int)Math.Ceiling((total - available) / (double)(buttons.Count - 1)));
        total = widths.Sum() + gap * (buttons.Count - 1);

        var x = Math.Max(4, (ClientSize.Width - total) / 2);
        // 贴底留 12px；Math.Max(120, …) 是无害的冗余保险（MinimumSize 保证客户区
        // 不会低到触发它），留着防呆。
        var rowY = Math.Max(120, ClientSize.Height - rowHeight - 12);

        for (var i = 0; i < buttons.Count; i++)
        {
            buttons[i].Size = new Size(widths[i], rowHeight);
            buttons[i].Location = new Point(x, rowY);
            x += widths[i] + gap;
        }
    }

    private static int TextWidth(string text, Font font) =>
        TextRenderer.MeasureText(text, font).Width;

    /// <summary>
    /// 把客户区固定为指定尺寸，并让 MinimumSize/MaximumSize 与之一致。
    ///
    /// 这里必须用"差值"而不是直接赋值：Size/ClientSize 之间的关系是
    /// ClientSize = Size - 边框 - 标题栏，而边框宽度随主题与 DPI 变化。
    /// 早先写成 MinimumSize = new Size(宽, 客户区高)，等于要求窗口高度小于客户区高度，
    /// 结果客户区被压到 188px，底部按钮行整个溢出（自检抓到的）。
    /// 先设 ClientSize，再按 Size 与 ClientSize 的实际差值补齐，就不会算歪。
    /// </summary>
    private void SetFixedClientSize(int width, int height)
    {
        // 顺序是踩出来的，注释留着免得下次又踩：
        //  ① 先清空 Min/Max。它们在 AutoScaleMode.Dpi + 高 DPI 下按逻辑单位换算，
        //     残留旧值会把窗口挤得远小于目标。
        //  ② 设 ClientSize——**必须设两次**：第一次发生在窗口句柄创建之前，
        //     WinForms 那时用默认边框算 Size-ClientSize 并把窗口钳小
        //     （实测 560 的目标被压成客户区 197）；第一次设完句柄就建好了，
        //     第二次才是按真实边框生效的那次，也才能算出正确的边框差值。
        //  ③ 用真实差值锁死宽高。
        //
        // 宽度也锁：这个窗体的版式（面板 424px + 两侧留白）本来就按固定宽度设计，
        // 放开宽度只会让按钮行在窄窗口下挤出边界。锁定比"允许拉伸但可能破版"可靠。
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;

        ClientSize = new Size(width, height);   // 第一次：建立窗口句柄
        ClientSize = new Size(width, height);   // 第二次：按真实边框生效

        var chromeW = Math.Max(0, Size.Width - ClientSize.Width);
        var chromeH = Math.Max(0, Size.Height - ClientSize.Height);
        var window = new Size(ClientSize.Width + chromeW, ClientSize.Height + chromeH);
        MinimumSize = window;
        MaximumSize = window;
    }

    /// <summary>
    /// 主窗体的布局自检（复用 LayoutDump 的公共实现）。
    /// 除了按钮几何，这里还额外记录 DPI 与 AutoScale 信息——本机 DeviceDpi 报 120 而系统是 96，
    /// 正是靠这几个字段才定位到"逻辑尺寸被 1.25 倍换算后溢出客户区"。
    /// </summary>
    private void DumpLayout(string label)
    {
        if (!LayoutDump.Enabled) return;
        LayoutDump.Capture(
            $"{label} Dpi={DeviceDpi} Scale={AutoScaleFactor} Min={MinimumSize.Width}x{MinimumSize.Height}",
            this,
            startButton, restartButton,
            refreshButton, envButton, foldersButton, versionsButton, upgradeButton);
    }

    /// <summary>
    /// 主按钮的文字随状态变，永远是"当前该点的那个"：未运行→启动，运行中→停止。
    /// 独立的「停止」按钮已并入主按钮（两套设计叠加时，运行中会出现两个红色「停止」），
    /// 「重启」保持独立——它在两种状态下都有意义（清掉残留后重新拉起）。
    /// </summary>
    private void ApplyPrimaryActionLabel()
    {
        startButton.Text = isOn ? "停止" : "启动";
        startButton.BackColor = isOn ? Color.FromArgb(224, 69, 62) : Color.FromArgb(34, 170, 85);
        startButton.AccessibleName = isOn
            ? "停止引擎并释放端口"
            : "启动 Harness 引擎并打开控制台。回车键等效";
        // 悬停提示同随状态刷新：按钮文字在"启动/停止"间切换，鼠标用户没有提示
        // 就只能靠颜色与文字猜。tooltip 之类是低频写入，每 1.5 秒刷新一次可忽略。
        try { tooltip.SetToolTip(startButton, isOn ? "停止引擎并释放端口（Esc 等效）" : "启动 Harness 引擎并打开控制台（回车等效）"); }
        catch { }
    }

    /// <summary>
    /// 打开"相关目录"窗口。相关位置散落在 %LOCALAPPDATA%\DeepSeekHarness、
    /// %USERPROFILE%\.dsh 与各 profile 三处，排查问题时来回翻很费事。
    /// </summary>
    private void OpenFoldersWindow()
    {
        if (closing || IsDisposed) return;
        try
        {
            using var dialog = new FoldersForm();
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowError("打开目录列表失败", ex.Message);
        }
    }

    /// <summary>
    /// 打开引擎版本管理。版本留在本地、随时能切回去，是"以插件为主"这个取舍的最后一道保险：
    /// 插件的 peer 要求是针对特定引擎版本写的，出问题时能一键退回上一个版本，
    /// 比重新下载安装可靠得多。
    /// </summary>
    private void OpenEngineVersions()
    {
        if (closing || IsDisposed) return;
        try
        {
            using var dialog = new EngineVersionsForm(
                GetInstalledEngineVersionsAsync,
                ActivateEngineVersionAsync,
                DeleteEngineVersionAsync);
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowError("打开版本管理失败", ex.Message);
        }
        finally
        {
            // 切换版本会停掉引擎，回来刷新一下状态与按钮可用性。
            _ = RefreshStatusAsync();
        }
    }

    /// <summary>
    /// ActivateEngineVersionAsync 返回值的**警告标记**：以此开头的返回串表示切换本体
    /// 已成功、但有需要用户知道的警告（旧版本归档失败、副本保留在 engine.tmp）。
    /// 版本管理对话框据此区分标题与图标——不能把成功误报成"切换失败"。
    /// </summary>
    internal const string ActivateSwitchedWithWarningPrefix = "新版本已启用";

    private async Task<string?> ActivateEngineVersionAsync(string version)
    {
        try
        {
            if (!IsSafeVersionToken(version))
                return $"版本号 {version} 不是合法的目录名，拒绝切换。";

            var slot = EngineSlotDirFor(version);
            if (!Directory.Exists(slot))
                return $"找不到版本 {version} 的目录：\n{slot}";
            if (!File.Exists(Path.Combine(slot, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")))
                return $"版本 {version} 的目录不完整（缺少 bin.js），无法切换。\n{slot}";

            var active = ReadEngineVersion(engineDir);
            if (string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
                return null;

            // 运行中的实例占着引擎文件，先停干净再改名。
            await StopHarnessProcessesAsync();
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(8), CancellationToken.None);

            // 交换段全程持 migrateGate：与每小时的归档轮/删槽改同一批目录，
            // 交错轻则 Move 失败、重则陈旧副本被建回槽里（闸管本进程内的竞态，
            // "改名认领"管跨进程的，两者粒度不同）。
            await migrateGate.WaitAsync();
            string? archiveWarning;
            try
            {
                archiveWarning = await Task.Run<string?>(() =>
                {
                    // 持闸后复查活动版本：active 是停引擎+等端口之前读的，另一会话的
                    // 启动器可能已换过引擎——拿陈旧名字归档会归进错误的槽。
                    // 早退判断（active == version）留在停引擎之前：只省一次重启，
                    // 不产生写动作。
                    var confirmed = ReadEngineVersion(engineDir);
                    if (!string.Equals(confirmed, active, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "等待期间活动引擎已被其他启动器改动，请重新打开「版本管理」再试。");

                    // **持闸后再复核目标槽是否仍完整**：上面的 bin.js 检查在停引擎+
                    // 等端口之前做的，另一会话这几秒里可以删掉该槽；被掏空目录上的
                    // Move 仍可能成功（同卷改名只要求源存在），残缺目录就成了活动引擎。
                    // 闸内复核把最坏结果从"静默装上残缺引擎"变成"切换中止 + 明确报错"。
                    if (!IsCompleteEngineInstall(slot))
                        throw new InvalidOperationException(
                            $"等待期间版本 {version} 的目录已被其他启动器改动或删除（不再完整），" +
                            "已中止切换。请重新打开「版本管理」再试。");

                    PreserveOrDiscardStagedEngine();
                    if (Directory.Exists(engineDir)) Directory.Move(engineDir, engineStageDir);
                    try
                    {
                        Directory.Move(slot, engineDir);
                    }
                    catch
                    {
                        // 新版本顶上失败：把旧引擎搬回去再上抛。
                        // 必须这么写——engineStageDir 虽已列入 RecoverEngineSwap 的
                        // 最后恢复源，但那要等下次启动才生效；当场搬回才是最直接的
                        // 恢复路径。不搬回去就会留下"没有引擎"的状态，下次启动只能
                        // 重新下载整份引擎（实测 214 MB）。
                        try { if (Directory.Exists(engineStageDir)) Directory.Move(engineStageDir, engineDir); } catch { }
                        throw;
                    }
                    // 原活动版本搬到它的版本槽。归档失败也不删副本：挪进 broken-<时间戳>
                    // 槽，或原样保留在 engine.tmp 并把位置报给用户——那是切换前唯一回退
                    // 副本（"归档失败就删 engine.tmp"的旧语义丢过唯一回退，见
                    // DESIGN-NOTES.md §4）。active 来自 package.json（可能被篡改），
                    // 拼进槽目录名前必须过 IsSafeVersionToken；不合法走 broken- 槽
                    // （槽名由本程序生成，仍可见、可管理）。
                    try
                    {
                        if (active is not null && IsSafeVersionToken(active))
                        {
                            // 与 ArchiveClaimedDir 同一口径：同名槽先删再放。
                            // 少了这一步，槽已存在时 Directory.Move 抛 IOException，
                            // 一路掉进下面的 catch → 归档成 broken-<时间戳> —— 用户在
                            // 「版本管理」里看到的是两个同版本条目，其中一个叫 broken-，
                            // 而本该被替换掉的那个旧副本还占着位置。
                            ForceDeleteDirectory(EngineSlotDirFor(active));
                            Directory.Move(engineStageDir, EngineSlotDirFor(active));
                        }
                        else if (Directory.Exists(engineStageDir))
                            Directory.Move(engineStageDir, BrokenSlotDirFor());
                    }
                    catch (Exception ex)
                    {
                        AppendStartupLog($"切换后归档旧版本失败（{active}）：{ex.Message}");
                        // 归档失败**也不能把这份旧版本直接删掉**——engine.tmp 里装着的正是
                        // 切换前的活动引擎，删掉等于丢掉唯一的回退副本（新版本顶上后，
                        // 用户想退回就只能重下 214 MB）。至少挪到 broken- 槽，位置可见、可管理。
                        var saved = false;
                        var brokenDir = (string?)null;
                        try
                        {
                            if (Directory.Exists(engineStageDir))
                            {
                                brokenDir = BrokenSlotDirFor();   // 取一次，落点与提示共用
                                Directory.Move(engineStageDir, brokenDir);
                                saved = true;
                            }
                        }
                        catch { }
                        // 已在后台线程（锁内不允许 await）。saved 时用带警告前缀的返回值
                        // 而非谎称完全成功：旧版本没进自己的槽、只在 broken- 槽里可见，
                        // 只报成功会让用户以为可管理的条目凭空消失。
                        // broken 槽名必须在 Move 之前取一次并复用：BrokenSlotDirFor
                        // 带时间戳且同秒加序号，消息里再调一次得到的是不存在的另一个目录。
                        if (saved) return ActivateSwitchedWithWarningPrefix +
                            $"，但旧版本（{active}）没能归档进它自己的版本槽，" +
                            $"已挪到「版本管理」里可管理的备用槽：\n{brokenDir}\n\n" +
                            "旧版本没有丢，可以用它回退；如果你不打算回退，下次安装引擎时会一并清理。";
                        // 两个归档落点都失败：**绝不能删**。此前这里的兜底是
                        // ForceDeleteDirectory(engineStageDir)——删掉的恰恰是切换前的
                        // 完整引擎（新版本此刻已顶上成功），等于把唯一回退副本丢掉。
                        // 原样保留：「环境」检测会提示 engine.tmp 残留，下次安装引擎
                        // 才清理；位置如实报给用户，由他决定手动改名或等待。
                        return ActivateSwitchedWithWarningPrefix +
                            $"，但旧版本未能归档（版本槽与 broken- 槽两处落点都失败），" +
                            $"已原样保留在：\n{engineStageDir}\n\n" +
                            "在下次安装引擎之前它是旧版本唯一的副本，请不要手动删除；" +
                            "可稍后在「版本管理」重试，或手动把该目录改名为 engine.<版本号>。\n\n" +
                            $"原始错误：{ex.Message}";
                    }
                    return null;
                });
            }
            finally { migrateGate.Release(); }
            InvalidateEngineVersionCache();   // 活动引擎目录已换，mtime 缓存必须失效
            if (archiveWarning is not null)
            {
                AppendStartupLog($"引擎版本已切换到 {version}，但旧版本归档失败：副本保留在 engine.tmp");
                return archiveWarning;
            }
            AppendStartupLog($"引擎版本已切换到 {version}");
            return null;
        }
        catch (Exception ex)
        {
            AppendStartupLog($"切换引擎版本失败：{ex.Message}");
            return "切换失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 删除一个版本槽目录。返回错误串，null 表示成功。
    /// 错误提示交回对话框自己弹——主窗体不该隔着模态对话框代弹 MessageBox。
    /// 删除整棵 node_modules 同样是秒级操作，所以放后台线程。
    /// </summary>
    private async Task<string?> DeleteEngineVersionAsync(string version)
    {
        if (!IsSafeVersionToken(version))
            return $"版本号 {version} 不是合法的目录名，拒绝删除。";
        var slot = EngineSlotDirFor(version);
        if (!Directory.Exists(slot)) return $"找不到版本 {version} 的目录。";

        try
        {
            SetInfo($"正在删除引擎 {version}…");
            // 读活动版本与删除收进**同一个后台闭包**，不留 UI 线程往返的窗口：
            // 互斥体是 Local\（每个登录会话各一个实例、共享同一份 %LOCALAPPDATA%），
            // 另一会话的启动器可能正在切换/启动引擎，两次 Task.Run 之间就是它的机会窗。
            var error = await Task.Run(() =>
            {
                // 与版本切换、每小时归档共用 migrateGate：三者改的是同一批
                // engine.<版本> 槽目录，删到一半撞上别人改名会两败俱伤。
                // 已在后台线程，同步 Wait 即可，不占 UI 线程。
                migrateGate.Wait();
                try
                {
                    var active = ReadEngineVersion(engineDir);
                    // fail-safe：活动目录**存在**却读不出版本（正被另一实例改名交换、
                    // 或 package.json 已损坏——后者恰是用户最可能来动版本的时机）时
                    // 不能放行。原实现拿 null 与版本号比较，null ≠ version，守卫空放行；
                    // 引擎目录若真不存在（已卸载），删除任意槽才是安全的。
                    if (Directory.Exists(engineDir) && active is null)
                        return "活动引擎目录存在但读不出版本（可能正被另一启动器实例交换目录，或清单已损坏），已拒绝删除。请稍后重试，或先修复/重装活动引擎。";
                    if (string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
                        return "不能删除正在使用的版本。请先切换到其他版本。";
                    ForceDeleteDirectory(slot);
                    return null;
                }
                finally { migrateGate.Release(); }
            });
            if (error is not null) return error;
            InvalidateEngineVersionCache();   // 槽目录动过，mtime 缓存可能已过期
            AppendStartupLog($"已删除引擎版本 {version}");
            SetInfo($"已删除引擎 {version}");
            return null;
        }
        catch (Exception ex)
        {
            return "删除失败：" + ex.Message;
        }
    }
}
