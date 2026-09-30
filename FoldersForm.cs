using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeepSeekHarness;

/// <summary>
/// DeepSeek Harness 相关目录一览：双击或用按钮在资源管理器里打开。
///
/// 做这个的原因很实际：这台机器上的相关位置散落在三处（%LOCALAPPDATA%\DeepSeekHarness、
/// %USERPROFILE%\.dsh、以及各 profile），排查问题时要来回翻。
/// 更麻烦的是有些目录是"按需出现"的——engine.old 只在升级过之后才有，
/// 所以这里**只列出真实存在的项**，不凭空造路径，避免看见一堆打不开的条目。
/// </summary>
internal sealed class FoldersForm : Form
{
    // 与另两个窗体同一个道理：外部 new 的 Font 不随控件释放，
    // 四个按钮各 new 一份就是每次开窗漏四个 GDI 句柄（见 HarnessForm.UiFont 的注释）。
    // 注意：这份静态 Font **刻意不在任何 Dispose 里释放**——窗体只回收自己 new 的字体，
    // 而共享单例要随进程存活（后续销毁流程中的控件仍可能引用它）。改代码时别"好心"回收。
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 9f);
    private static readonly Font BoldFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 10f, FontStyle.Bold);

    private readonly Label title = new();
    private readonly ListView list = new();
    private readonly Label hint = new();
    // 按钮不给初始化器：构造函数里一律用 NewButton 赋值。留个 `= new()` 只会让每次
    // 构造都白造四个永不加入 Controls 的孤立 Button（连带四个 Font）。
    private readonly Button openButton;
    private readonly Button copyButton;
    private readonly Button closeButton;
    private readonly Button refreshButton;
    /// <summary>正在跑一次刷新。防重入 + 统一管按钮可用性。</summary>
    private bool busy;
    /// <summary>窗体正在关闭。用来区分"已经没了"与"还没显示"（见 Gone）。</summary>
    private bool closing;
    /// <summary>四个按钮的实测文字宽度，构造后第一次布局时算一次即可。</summary>
    private int[]? buttonWidths;
    /// <summary>正在跑剪贴板重试（最长约 600ms），用来挡住连点。</summary>
    private bool copying;

    /// <summary>一个相关位置。File 类条目在资源管理器里用"选中"而不是"打开"。</summary>
    private sealed record Entry(string Name, string Path, string Note, bool IsFile);

    internal FoldersForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "DeepSeek Harness 相关目录";
        // 目录列表初始给 168px（表头 + 约 5–6 行）：本机有 17 个真实位置，多露几行实用。
        // 窗口可缩放，尺寸变化由 ApplyResponsiveLayout 接管（列表吃剩余高度，底部锚定）。
        ClientSize = new Size(660, 286);
        MinimumSize = new Size(460, 220);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = BodyFont;

        title.Text = "双击任意一行即可在资源管理器里打开";
        title.Location = new Point(16, 12);
        // 用 AutoSize 而不是硬编码 500px 宽：窗口拉到最窄时 500px 的标签会溢出客户区
        // （布局自检在 470px 宽度下抓到的）。
        title.AutoSize = true;
        title.Font = TitleFont;
        title.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        Controls.Add(title);

        list.Location = new Point(16, 36);
        list.Size = new Size(628, 168);
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.HideSelection = false;
        list.Columns.Add("名称", 150);
        // 位置列必须给足：这是本窗口最该看清的信息。
        // 首版给 290，实测连 "C:\Users\Administrator.DESKTOP-QA5LIHH\.dsh\profiles\web"
        // 都显示不下（被截成 ...\DESKTOP-Q...），等于把最有用的部分藏起来了。
        list.Columns.Add("位置", 300);
        list.Columns.Add("说明", 170);
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.DoubleClick += (_, _) => OpenSelected();
        Controls.Add(list);

        hint.Location = new Point(16, 210);
        // 两行高度，且文本里用硬换行拆开。此前是一行 18px 的 Label 装一句约 35 个全角
        // 字符的提示：Label 既不换行也无 AutoEllipsis，而本窗 MinimumSize.Width 只有 460
        // （可用宽约 412px），120% DPI 下这段文字要 525px——**必然被截断**，
        // 且被截掉的正是后半句「「凭据」含密钥，分享截图前请留意」这条唯一的安全提醒。
        // 布局自检只看控件 Bounds，看不见文字截断，所以它永远不会报这里。
        hint.Size = new Size(628, 36);
        hint.ForeColor = Color.FromArgb(108, 114, 126);
        Controls.Add(hint);

        openButton = NewButton("在资源管理器中打开", Color.FromArgb(34, 170, 85));
        openButton.Click += (_, _) => OpenSelected();
        Controls.Add(openButton);

        copyButton = NewButton("复制路径", Color.FromArgb(58, 124, 240));
        copyButton.Click += (_, _) => CopySelectedPath();
        Controls.Add(copyButton);

        closeButton = NewButton("关闭", Color.FromArgb(114, 122, 143));
        closeButton.Click += (_, _) => Close();
        Controls.Add(closeButton);

        refreshButton = NewButton("刷新", Color.FromArgb(114, 122, 143));
        refreshButton.Click += (_, _) => _ = ReloadAsync();
        Controls.Add(refreshButton);

        CancelButton = closeButton;

        // 与"版本管理"同一套自适应：列表吃掉中间的剩余高度，提示与按钮锚在底部。
        // 只设 Anchor 不够——列表锚 Top 时拉高窗口它不动，按钮更是纵向写死。
        Resize += (_, _) =>
        {
            ApplyResponsiveLayout();
            // 与「版本管理」同一个理由：LayoutDump.Enabled 必须在调用点先判，
            // 否则每次 Resize 都要白付一次插值字符串 + Control[] 的分配。
            if (LayoutDump.Enabled)
                LayoutDump.Capture($"目录 {ClientSize.Width}x{ClientSize.Height}", this,
                    title, list, hint, openButton, copyButton, closeButton, refreshButton);
        };
        ApplyResponsiveLayout();

        // 首屏加载挂在 Load 而不是构造函数：构造阶段句柄必然还没创建，
        // 而下面 ReloadAsync 的存活判据不能用 IsHandleCreated（它表达"还没显示"）。
        // 详见 Gone 的注释。
        Load += async (_, _) => await ReloadAsync();
        FormClosing += (_, e) => ConfirmClose(e);
    }

    /// <summary>
    /// 窗体已经不可用了（已释放或正在关闭）。
    /// **刻意不用 <c>IsHandleCreated</c>**：它表达的是"还没显示"，不是"已经没了"。
    /// </summary>
    private bool Gone => IsDisposed || Disposing || closing;

    /// <summary>
    /// 关闭时收口。被取消的关闭**不能**把 closing 置起来——FormClosing 在
    /// <c>e.Cancel</c> 时照样触发，而 closing 粘性，误置一次 <see cref="Gone"/>
    /// 就永远为真（列表永远空、提示永远停在"正在读取…"，且不报错）。
    ///
    /// 「关闭」按钮不禁用：它是 CancelButton，禁用会连带吞掉 Esc
    /// （Button 在 Enabled==false 时 CanSelect 为 false，ProcessDialogKey 的
    /// Escape 分支直接跳过），键盘用户就变成完全无反应。禁它也拦不住标题栏 X。
    /// </summary>
    private void ConfirmClose(FormClosingEventArgs e)
    {
        if (busy)
        {
            var go = MessageBox.Show(this,
                "正在读取目录列表。\n\n现在关闭窗口，本次读取仍会在后台跑完，但结果不会再显示。\n\n确定要关闭吗？",
                "读取进行中", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (go != DialogResult.Yes) { e.Cancel = true; return; }
        }
        closing = true;
    }

    /// <summary>
    /// 布局自检不再挂在本窗体的 Shown 上（DSH_LAYOUT_DUMP=1 时那个处理器会直接
    /// Close() 掉自己，导致误设环境变量时"相关目录"窗口一闪即消）。
    /// 独立入口在 Program.RunLayoutSelfTest：它在窗体外部遍历各尺寸后自行关闭。
    /// </summary>

    private Button NewButton(string text, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(120, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = BoldFont,
            Cursor = Cursors.Hand,
            AccessibleName = text
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    /// <summary>
    /// 让内容跟着窗口尺寸走：列表吃掉中间的剩余高度，提示与按钮永远贴底。
    /// 之前按钮只按宽度重算 x、纵向写死，所以上下拉伸毫无反应。
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        // 与「版本管理」同一套公式：提示与按钮行都从客户区**底边反推**，
        // 列表高度上下都夹住。原来 hint 的 y 取自 list.Bottom，而 list 有下限保护，
        // 窗口压到最矮时提示会与按钮行零间隙相接（两个窗体的公式就此对齐）。
        const int listTop = 36;
        const int hintHeight = 36;   // 两行：计数 + 凭据提醒（见构造里 hint 的注释）
        const int buttonHeight = 28;
        const int bottomPad = 14;
        const int margin = 16;
        const int gap = 8;

        var rowY = ClientSize.Height - buttonHeight - bottomPad;
        var hintY = rowY - gap - hintHeight;

        var maxList = Math.Max(40, hintY - gap - listTop);
        var listHeight = Math.Clamp(
            ClientSize.Height - listTop - (hintHeight + gap + buttonHeight + bottomPad), 40, maxList);

        list.Location = new Point(margin, listTop);
        list.Size = new Size(Math.Max(80, ClientSize.Width - margin * 2), listHeight);

        hint.Location = new Point(margin, hintY);
        hint.Size = new Size(Math.Max(80, ClientSize.Width - margin * 2), hintHeight);

        LayoutButtons(rowY, margin, gap, buttonHeight);
    }

    /// <summary>
    /// 四个按钮在底部均分并居中；窗口比"刚好放下"还窄时压缩宽度并退化为贴边，不重叠。
    /// </summary>
    private void LayoutButtons(int rowY, int margin, int gap, int buttonHeight)
    {
        // 文字与字体构造期就定死，宽度永远不变；而 Resize 是拖边框时每像素触发一次的热路径。
        // TextRenderer.MeasureText 走 GDI，比普通标量计算贵得多，不该在这里重复付。
        buttonWidths ??= new[] { openButton, copyButton, closeButton, refreshButton }
            .Select(b => Math.Max(84, TextRenderer.MeasureText(b.Text, b.Font).Width + 24)).ToArray();

        var buttons = new[] { openButton, copyButton, closeButton, refreshButton };
        var widths = buttonWidths;
        var total = widths.Sum() + gap * (buttons.Length - 1);
        var available = ClientSize.Width - margin * 2;
        if (total > available && buttons.Length > 1)
        {
            var shrink = (int)Math.Ceiling((total - available) / (double)(buttons.Length - 1));
            for (var i = 0; i < widths.Length; i++) widths[i] = Math.Max(72, widths[i] - shrink);
            total = widths.Sum() + gap * (buttons.Length - 1);
        }

        var x = Math.Max(margin, (ClientSize.Width - total) / 2);
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Size = new Size(widths[i], buttonHeight);
            buttons[i].Location = new Point(x, rowY);
            x += widths[i] + gap;
        }
    }

    /// <summary>供布局自检遍历的控件清单。</summary>
    internal Control[] DumpControls() =>
        new Control[] { title, list, hint, openButton, copyButton, closeButton, refreshButton };

    /// <summary>
    /// 收集所有相关位置。只返回真实存在的，按"常用的排前面"排序。
    /// 路径全部从环境变量推导，不写死任何机器专属位置。
    /// </summary>
    /// <summary>「相关目录」里最多列出的会话项目目录数，超出部分只报个数。</summary>
    private const int MaxSessionProjects = 200;

    /// <summary>收集结果：条目 + 因超过上限而未列出的会话项目数。</summary>
    private readonly record struct CollectResult(List<Entry> Entries, int SessionOverflow);

    private static CollectResult Collect()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // 某些受限账户下这两个可能返回空串，而 Path.Combine("", ".dsh") == ".dsh"
        // 是个**相对路径**——后面的 Exists 会按当前工作目录解析，可能误命中，
        // 并把一个相对路径显示甚至打开给用户。宁可少列几项。
        if (string.IsNullOrWhiteSpace(appData) || string.IsNullOrWhiteSpace(userProfile))
            return new CollectResult(new List<Entry>(), 0);

        var appDir = Path.Combine(appData, "DeepSeekHarness");
        var dshHome = Path.Combine(userProfile, ".dsh");

        var candidates = new List<Entry>
        {
            new("启动器数据", appDir, "本程序自己的工作目录", false),
            new("DSH 主目录", dshHome, "所有 DSH 数据的根", false),
            new("web profile", Path.Combine(dshHome, "profiles", "web"), "启动器启动的就是它", false),
            new("desktop profile", Path.Combine(dshHome, "profiles", "desktop"), "桌面客户端用的 profile", false),
            new("引擎（活动）", Path.Combine(appDir, "engine"), "当前正在运行的引擎", false),
            new("引擎（上一版本）", Path.Combine(appDir, "engine.old"), "升级前的备份，可回退", false),
            new("配置备份", Path.Combine(appDir, "config-backups"), "启动器自动拍的配置快照", false),
            new("认证链接", Path.Combine(appDir, "web-url.txt"), "含 token 的本地链接", true),
            new("引擎输出日志", Path.Combine(appDir, "engine-stdio.log"), "引擎 stdout/stderr（每次重新拉起时重开）", true),
            new("更新日志", Path.Combine(appDir, "update-log.txt"), "插件更新的输出", true),
            new("会话记录", Path.Combine(dshHome, "sessions"), "历史对话", false),
            new("附件", Path.Combine(dshHome, "attachments"), "上传的图片等", false),
            new("存储 / 缓存", Path.Combine(dshHome, "storages"), "DSH 内部状态", false),
            new("凭据", Path.Combine(dshHome, ".credentials.yaml"), "账号密钥（请勿外传）", true),
            new("设置（遗留）", Path.Combine(dshHome, "settings.yaml.imported"), "迁移前的设置备份", true),
            new("设置", Path.Combine(dshHome, "settings.yaml"), "迁移后一般不再存在", true),
        };

        // 会话目录按项目分子目录，逐个也列出来，方便直接跳过去。
        // 上限 200：会话目录随使用线性增长，而这里对每个项目还要再发一次
        // GetDirectories 统计会话数（N+1 次系统调用）。不设上限的话，一个用了
        // 很久的账号能把这个"相关目录"窗口撑成上千行——而它的用途是挑几个位置跳过去，
        // 没人需要翻到第 800 个项目。超限时在提示行里说明总数。
        var overflow = 0;
        try
        {
            var sessions = Path.Combine(dshHome, "sessions");
            if (Directory.Exists(sessions))
            {
                var projects = Directory.GetDirectories(sessions)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var shown = Math.Min(projects.Count, MaxSessionProjects);
                for (var i = 0; i < shown; i++)
                {
                    var dir = projects[i];
                    var count = Directory.GetDirectories(dir).Length;
                    candidates.Add(new Entry(
                        "  └ " + Path.GetFileName(dir),
                        dir,
                        $"{count} 个会话",
                        false));
                }
                overflow = projects.Count - shown;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只是部分会话子目录读不到（权限/被占用），主列表照常显示。
            // 收窄异常类型：不该把 OutOfMemory 之类一并吞掉。
        }

        var visible = candidates.Where(e => File.Exists(e.Path) || Directory.Exists(e.Path)).ToList();
        return new CollectResult(visible, overflow);
    }

    private async Task ReloadAsync()
    {
        if (busy || Gone) return;

        SetBusy(true);
        hint.Text = "正在读取…";
        // 记住当前选中的路径：刷新后一律跳回第一行会让"复制路径"的上下文跑掉。
        var previous = Selected?.Path;
        try
        {
            // Collect 是纯只读的元数据查询（Exists + GetDirectories，不读文件内容），
            // 放后台线程就够；但 profile 落在漫游配置/网络盘时这些查询并不便宜，
            // 同步做在 UI 线程上会明显卡一下。
            var collected = await Task.Run(Collect);
            var entries = collected.Entries;
            var sessionOverflow = collected.SessionOverflow;

            // await 期间用户可能关了窗：主窗体那边是 using var dialog + ShowDialog，
            // 窗口一返回就 Dispose，此后任何控件访问都是 ObjectDisposedException。
            if (Gone) return;

            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in entries)
                {
                    var item = new ListViewItem(entry.IsFile ? "📄 " + entry.Name : "📁 " + entry.Name);
                    item.SubItems.Add(entry.Path);
                    item.SubItems.Add(entry.Note);
                    item.Tag = entry;
                    item.ForeColor = entry.IsFile ? Color.FromArgb(88, 94, 120) : Color.FromArgb(30, 34, 44);
                    list.Items.Add(item);
                }
            }
            finally { list.EndUpdate(); }

            if (list.Items.Count > 0)
            {
                var restore = list.Items.Cast<ListViewItem>()
                    .FirstOrDefault(i => ((Entry)i.Tag!).Path == previous);
                (restore ?? list.Items[0]).Selected = true;
            }

            hint.Text = $"共 {entries.Count} 项（只显示真实存在的位置）" +
                        (sessionOverflow > 0 ? $"，另有 {sessionOverflow} 个会话项目未列出" : string.Empty) +
                        "\r\n「凭据」含密钥，分享截图前请留意";
        }
        catch (Exception ex)
        {
            if (!Gone) hint.Text = "读取目录失败：" + ex.Message;
        }
        finally
        {
            // busy 是状态、控件写入是副作用：两者必须分开无条件复位。
            // 原先写在 `if (!IsDisposed)` 里，窗体一旦在 await 期间被关掉，
            // busy 就被永远留在 true——目前无害，但只要有人放宽这个判断
            // （比如改成"句柄没了但对象还在，以便把结果交回父窗体弹提示"），
            // 就会留下一条再也不会被清掉的 busy。
            busy = false;
            if (!Gone) SetBusy(false);
        }
    }

    /// <summary>
    /// 统一管理忙碌态。与「版本管理」的 SetBusy 同一套纪律：列表也要禁用——
    /// 只禁按钮挡不住双击，而 Collect() 在网络盘/漫游配置上可能几百毫秒，
    /// 这段时间里双击命中的是**上一轮**的 Tag（可能已被删掉）。
    /// </summary>
    private void SetBusy(bool value)
    {
        if (Gone) return;
        busy = value;
        list.Enabled = !value;
        refreshButton.Enabled = !value;
        openButton.Enabled = !value;
        copyButton.Enabled = !value;
        // 「关闭」不用：它是 CancelButton，禁用会连带吞掉 Esc；由 ConfirmClose 收口。
        if (!value) UpdateButtons();
    }

    private Entry? Selected =>
        !Gone && list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Entry : null;

    private void UpdateButtons()
    {
        if (busy || Gone) return;
        var sel = Selected;
        openButton.Enabled = sel is not null;
        copyButton.Enabled = sel is not null;
    }

    private void OpenSelected()
    {
        var sel = Selected;
        if (sel is null) return;
        try
        {
            if (sel.IsFile)
            {
                // 文件用"在资源管理器中选中"而不是"用默认程序打开"：
                // 拿 .credentials.yaml 来说，用记事本打开可不是用户想要的效果。
                // explorer 只认 "/select,<path>" **连写成一个参数**——拆成两个参数
                // （ArgumentList 各加一项）在部分 Windows 版本上只会打开父目录、
                // 不选中文件。路径来自我们枚举的真实文件系统条目，且 Windows
                // 文件名不允许出现引号字符，这里内联引号转义是安全的。
                var psi = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = "/select,\"" + sel.Path + "\""
                };
                using (Process.Start(psi)) { }
            }
            else
            {
                using (Process.Start(new ProcessStartInfo(sel.Path) { UseShellExecute = true })) { }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开：\n{sel.Path}\n\n{ex.Message}",
                "打开目录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopySelectedPath() => _ = CopySelectedPathAsync();

    /// <summary>
    /// 复制选中的路径。
    ///
    /// 剪贴板被别的进程短暂占着时，<see cref="Clipboard.SetText"/> 抛的是
    /// ExternalException（ERROR_CLIPBOARD_NOT_OPEN）——这在真实使用里非常常见
    /// （剪贴板管理器、远程桌面、截图工具）。原先第一次失败就弹窗，于是用户
    /// 点「复制路径」常常要试两三次。这里做有限次退避重试：等待必须离开 UI 线程，
    /// 所以整体做成 async。
    /// </summary>
    private async Task CopySelectedPathAsync()
    {
        var sel = Selected;
        if (sel is null || copying) return;   // 重试期间可能长达 600ms，得挡住连点
        copying = true;
        copyButton.Enabled = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetText(sel.Path);
                    if (!Gone) hint.Text = "已复制：" + sel.Path;
                    return;
                }
                catch (ExternalException) when (attempt < 4)
                {
                    await Task.Delay(60 * (attempt + 1));
                    // 刻意不在这里判 Gone：最后一次尝试仍要照做，复制成功就是成功。
                    // 中途 return 只会得到"既没复制成功、也没有任何提示"的静默失败——
                    // 比旧实现"第一次失败必定弹窗"还要糟。
                }
                catch (Exception ex)
                {
                    if (!Gone)
                        MessageBox.Show($"复制失败：{ex.Message}\n\n剪贴板可能被其他程序占用，或内容超出限制。",
                            "复制路径", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
        }
        finally
        {
            copying = false;
            if (!Gone) copyButton.Enabled = true;
        }
    }
}
