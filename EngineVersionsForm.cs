namespace DeepSeekHarness;

/// <summary>
/// 已安装引擎版本的列表：查看、切换、删除。
///
/// 目录约定（与主窗体的升级逻辑共用）：
///   活动版本永远是 <c>engine\</c>——启动器只从这一个目录跑 bin.js。
///   其他版本放 <c>engine.&lt;版本号&gt;\</c>，例如 <c>engine.0.1.5-rc.2\</c>。
/// 这样"切换版本"就是两次目录改名，原子且可逆，和升级的三步替换是同一套思路。
///
/// 之所以要这个界面：插件的 peer 要求是针对特定引擎版本写的，
/// 所以"哪个引擎版本能用"必须以插件为准。版本留在本地、随时能切回去，
/// 比"升级后出问题再重装"可靠得多。
/// </summary>
internal sealed class EngineVersionsForm : Form
{
    // 字体与主窗体同一个道理：外部 new 的 Font 不随控件释放，四个按钮各 new 一份
    // 就是每次开窗漏四个 GDI 句柄（见 HarnessForm.UiFont 处的注释）。这里共享一份。
    // 注意：这份静态 Font **刻意不在任何 Dispose 里释放**——共享单例要随进程存活
    // （后续销毁流程中的控件仍可能引用它）。改代码时别"好心"回收。
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 9f);
    private static readonly Font BoldFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 10f, FontStyle.Bold);

    private readonly Label title = new();
    private readonly ListView list = new();
    private readonly Label hint = new();
    // 按钮不给初始化器：构造函数里一律用 NewButton 赋值。留个 `= new()` 只会让每次
    // 构造都白造四个永不加入 Controls 的孤立 Button（连带四个 Font），纯浪费。
    private readonly Button activateButton;
    private readonly Button deleteButton;
    private readonly Button closeButton;
    private readonly Button refreshButton;
    private readonly Func<Task<IReadOnlyList<EngineVersionEntry>>> listVersions;
    private readonly Func<string, Task<string?>> activateVersion;
    private readonly Func<string, Task<string?>> deleteVersion;
    /// <summary>正在跑一个异步操作（读取/切换/删除）。防重入，同时统一管按钮可用性。</summary>
    private bool busy;
    /// <summary>
    /// busy **只是 UI 层防重入**，不是并发互斥。
    ///
    /// 真正的互斥在数据层：<c>activateVersion</c> / <c>deleteVersion</c> 两个委托
    /// 最终都进 HarnessForm 的 <c>migrateGate</c>（同一把 SemaphoreSlim，切版本 / 删槽 /
    /// 归档 / 安装替换共用）。必须记住这一点，否则很容易以为"关窗后重开一个对话框"是
    /// 安全的——它<b>不是</b>：本窗体的 <c>busy</c> 只管住这一个实例，新实例照样能发起
    /// 破坏性操作。两个操作在闸上排队，顺序由闸决定，而不是由用户的确认框决定。
    ///
    /// 本窗还把「列举」也纳入了同一把闸，所以读到的列表与磁盘状态自洽。
    /// </summary>
    /// <summary>窗体正在关闭。用来区分"已经没了"与"还没显示"——这两件事完全不同。</summary>
    private bool closing;
    /// <summary>
    /// 布局自检（Program.RunLayoutSelfTest）的静默关闭开关。编程式 Close() 的
    /// CloseReason 是 UserClosing（WinForms 文档原文："either programmatically
    /// or through a user action"），busy 没复位时会命中 ConfirmClose 的确认框——
    /// 自检进程没有人应答，就挂死在那里。自检关闭必须绕过询问。
    /// </summary>
    internal bool QuietClose;

    internal EngineVersionsForm(
        Func<Task<IReadOnlyList<EngineVersionEntry>>> listVersions,
        Func<string, Task<string?>> activateVersion,
        Func<string, Task<string?>> deleteVersion)
    {
        this.listVersions = listVersions;
        this.activateVersion = activateVersion;
        this.deleteVersion = deleteVersion;

        // AutoScaleMode.Dpi 在**没有** AutoScaleDimensions、也没有 PerformAutoScale 的
        // 情况下是空操作：WinForms 按默认的 96/96 算缩放因子，在 120/144 DPI 上得到的
        // 仍是 1.0，于是本文件里所有布局常量（34/30/16/8/14…）其实全是 96-DPI 像素。
        // 这里明确关掉自动缩放、自己按 DeviceDpi 换算——与 FoldersForm、主窗体同一纪律：
        // 一套机制，全部常量从 <see cref="DpiScale"/> 派生，不与 WinForms 的自动缩放打架。
        AutoScaleMode = AutoScaleMode.None;
        Text = "引擎版本管理";
        // 初始尺寸按"通常只有 1–3 个版本"来定：列表留 86px（表头 + 约 3 行），再多滚动。
        // 但窗口是**可缩放**的，所以下面用 ApplyResponsiveLayout 让内容跟着变：
        // 只设 Anchor 不够——那个小箭头按钮当初只按宽度重算 x，纵向仍然是写死的常量，
        // 所以上下拉伸时按钮纹丝不动。真正自适应的做法见 ApplyResponsiveLayout。
        ClientSize = new Size(560, 250);
        MinimumSize = new Size(420, 210);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = BodyFont;

        title.Text = "本机已安装的 DSH 引擎版本";
        title.Location = new Point(16, 10);
        // AutoSize 而非硬编码宽度：窗口变窄时固定宽度会溢出客户区。
        title.AutoSize = true;
        title.Font = TitleFont;
        title.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        Controls.Add(title);

        list.Location = new Point(16, 34);
        list.Size = new Size(528, 86);
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.HideSelection = false;
        list.GridLines = false;
        list.Columns.Add("版本", 150);
        list.Columns.Add("状态", 110);
        list.Columns.Add("大小", 90);
        list.Columns.Add("安装时间", 150);
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.DoubleClick += (_, _) => ActivateSelected();
        Controls.Add(list);

        hint.Location = new Point(16, 126);
        hint.Size = new Size(528, 34);
        hint.ForeColor = Color.FromArgb(108, 114, 126);
        hint.AutoEllipsis = true;
        Controls.Add(hint);

        activateButton = NewButton("切换到此版本", Color.FromArgb(34, 170, 85));
        activateButton.Click += (_, _) => ActivateSelected();
        Controls.Add(activateButton);

        deleteButton = NewButton("删除", Color.FromArgb(224, 69, 62));
        deleteButton.Click += (_, _) => DeleteSelected();
        Controls.Add(deleteButton);

        closeButton = NewButton("关闭", Color.FromArgb(114, 122, 143));
        closeButton.Click += (_, _) => Close();
        Controls.Add(closeButton);

        refreshButton = NewButton("刷新", Color.FromArgb(58, 124, 240));
        refreshButton.Click += (_, _) => _ = ReloadAsync();
        Controls.Add(refreshButton);
        CancelButton = closeButton;
        // "切换到此版本"是本窗的主操作，之前只设了 CancelButton，键盘用户按回车没反应。
        AcceptButton = activateButton;

        // 真正自适应的布局：列表吃掉中间的剩余高度，提示与按钮锚在底部。
        Resize += (_, _) =>
        {
            ApplyResponsiveLayout();
            // LayoutDump.Enabled 要在**调用点**先判一次：参数里的插值字符串与 Control[]
            // 在进入 Capture 之前就已经构造好了，而 Resize 是拖边框时每像素都触发的热路径。
            // 不判的话，没开自检的普通用户也要为每次 Resize 白付一次字符串 + 数组分配。
            if (LayoutDump.Enabled)
                LayoutDump.Capture($"版本管理 {ClientSize.Width}x{ClientSize.Height}", this,
                    title, list, hint, activateButton, deleteButton, closeButton, refreshButton);
        };
        ApplyResponsiveLayout();

        // 首屏加载挂在 Load，**不是构造函数**。
        // 构造函数阶段窗体句柄必然还没创建，而此前 ReloadAsync 里拿 IsHandleCreated
        // 当"窗体已消失"的判据，于是只要 listVersions 的 Task 恰好已完成（await 不产生
        // 让出），代码会原地走到那个 return：列表永远是空的、提示永远停在"正在读取…"，
        // 而且**不报任何错**。生产目前靠 GetInstalledEngineVersionsAsync 内部是 Task.Run
        // （要扫 2.5 万个文件、必然让出）侥幸躲过，那是时序上的运气。Program 的布局自检
        // 给的正是已完成的 Task.FromResult，所以自检每次跑的都是这条"空列表"分支——
        // 也就是说它量到的布局状态在生产里根本不会出现。Load 时句柄已建，没这个问题。
        Load += async (_, _) => await ReloadAsyncCore();
        FormClosing += (_, e) => ConfirmClose(e);
    }

    /// <summary>
    /// 窗体已经不可用了（已释放或正在关闭）。
    /// **刻意不用 <c>IsHandleCreated</c>**：它表达的是"还没显示"，不是"已经没了"。
    /// </summary>
    private bool Gone => IsDisposed || Disposing || closing;

    /// <summary>
    /// 关闭时收口。<paramref name="e.Cancel"/> 被置位时**不能**把 closing 置起来——
    /// FormClosing 在取消关闭时照样触发，而 closing 是粘性的。一旦被一次"取消"
    /// 误置，<see cref="Gone"/> 就永远为真：列表永远空、提示永远停在"正在读取…"，
    /// 而且不报任何错——正是这次改动想消灭的那种静默失败。
    ///
    /// 破坏性操作进行到一半时给出确认，而不是把「关闭」按钮禁掉：
    /// 禁按钮只挡得住鼠标，标题栏 X 与 Alt+F4 照样能关（ControlBox 不受控件
    /// Enabled 影响），可它同时把 CancelButton 也废了——Button 在 Enabled==false
    /// 时 CanSelect 为 false，Form.ProcessDialogKey 的 Escape 分支会直接跳过，
    /// 键盘用户按 Esc 就变成**完全无反应**。与"无声吞掉 Esc"相比，
    /// "问一句要不要关"把三条关闭路径统一成了同一个有意识的决定。
    /// </summary>
    private void ConfirmClose(FormClosingEventArgs e)
    {
        // 只有**用户自己点的关闭**才询问。系统关机/注销/任务管理器结束同样会走
        // FormClosing，而此刻 busy 的概率最高（正在扫 2.5 万文件）：弹模态框 +
        // e.Cancel = true 就是 Windows 意义上的"此应用阻止关机"，用户只能强杀，
        // 连日志都留不下。那种场景下"操作会不会跑完"根本不是用户需要做决定的事。
        if (e.CloseReason != CloseReason.UserClosing) { closing = true; return; }
        if (QuietClose) { closing = true; return; }   // 布局自检：编程式关闭不询问（见字段注释）
        if (busy)
        {
            var go = MessageBox.Show(this,
                "操作仍在进行中。\n\n现在关闭窗口，操作会继续在后台执行完，但结果不会再显示。\n\n确定要关闭吗？",
                "操作进行中", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (go != DialogResult.Yes) { e.Cancel = true; return; }
        }
        closing = true;
    }

    /// <summary>
    /// 布局自检不再挂在本窗体的 Shown 上（DSH_LAYOUT_DUMP=1 时那个处理器会直接
    /// Close() 掉自己，导致误设环境变量时"版本管理"窗口一闪即消）。
    /// 独立入口在 Program.RunLayoutSelfTest：它在窗体外部遍历各尺寸后自行关闭。
    /// </summary>

    private Button NewButton(string text, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(108, 30),
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

    private const int ButtonHeight = 30;
    private const int HintHeight = 34;
    /// <summary>左右留白。刻意不叫 Margin：那是 Form 的继承成员，同名会遮蔽并触发 CS0108。</summary>
    private const int SidePad = 16;
    private const int BottomPad = 14;
    private const int Gap = 8;
    private const int ListTop = 34;
    /// <summary>四个按钮的实测文字宽度，按 <see cref="measuredAtDpi"/> 那个 DPI 量一次即可。</summary>
    private int[]? buttonWidths;
    /// <summary>上面那份宽度是在哪个 DPI 下量的。DPI 变了必须重量（GDI 量的是设备像素）。</summary>
    private int measuredAtDpi = -1;

    // ---- DPI 换算 ----------------------------------------------------------

    /// <summary>
    /// 96-DPI 设计值 → 本机实际像素。<see cref="DeviceDpi"/> 在句柄创建前还报设计值，
    /// 句柄建好之后（Load / 首次 Resize）才是真值——所以布局常量**不能**在字段
    /// 初始化器里算死，必须每次布局时现算（ApplyResponsiveLayout 已经如此）。
    /// </summary>
    private double DpiScale => Math.Max(1.0, DeviceDpi / 96.0);

    /// <summary>按 <see cref="DpiScale"/> 把 96-DPI 的设计像素换算成实际像素（向上取整）。</summary>
    private int Px(double designValue) => (int)Math.Ceiling(designValue * DpiScale);

    /// <summary>由 <see cref="DpiScale"/> 换算出来的几个布局常量（96-DPI 设计值见上面的常量声明）。</summary>
    private int ScaledButtonHeight => Px(ButtonHeight);
    private int ScaledHintHeight => Px(HintHeight);
    private int ScaledSidePad => Px(SidePad);
    private int ScaledBottomPad => Px(BottomPad);
    private int ScaledGap => Px(Gap);
    private int ScaledListTop => Px(ListTop);

    /// <summary>
    /// 让内容跟着窗口尺寸走。
    ///
    /// 只靠 Anchor 不够：ListView 锚 Left|Right 能横向拉伸，但纵向我把它锚成了 Top，
    /// 于是拉高窗口时列表不动、下方留白；按钮更是只按宽度重算 x，纵向写死常量，
    /// 所以"上下拉伸没反应"。这里统一按当前 ClientSize 重算：
    ///   列表高度 = 客户区高 - 列表顶 - 提示与按钮占位（它们锚在底部）
    ///   提示与按钮的 y 由客户区高反推，永远贴着底边。
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        // 提示与按钮行都从客户区**底边反推**，不再依赖 list.Bottom：
        // 列表高度有下限保护，窗口被压到 MinimumSize 时它会被撑大，
        // 若提示的 y 取自 list.Bottom 就会和按钮行叠在一起。
        var buttonHeight = ScaledButtonHeight;
        var hintHeight = ScaledHintHeight;
        var bottomPad = ScaledBottomPad;
        var sidePad = ScaledSidePad;
        var gap = ScaledGap;
        var listTop = ScaledListTop;

        var rowY = ClientSize.Height - buttonHeight - bottomPad;
        var hintY = rowY - gap - hintHeight;

        // 列表吃掉中间的剩余高度。**下限不能盖过上限**：
        // 原实现是 Math.Max(40, hintY - Gap - ListTop) 先给一个 40px 的下限，
        // 再 Math.Clamp(…, 40, maxList) —— 而 max 自己也带了 Math.Max(40, …)，
        // 于是"窗口比提示 + 按钮所需还矮"时 maxList 恒为 40，列表底边
        // （ListTop + 40）就压到 hintY - Gap 之下，与提示零间隙相接甚至重叠，
        // 正是本方法开头承诺"控件不重叠"要避免的形状。
        // 现在上限是**真实可用空间**，下限只在它之上生效；空间不够时列表贴住提示，
        // 宁可压扁也不重叠。MinimumSize 已经保证正常拖拽不会走到这个分支。
        var available = hintY - gap - listTop;
        var listHeight = Math.Max(Px(24), Math.Min(
            available, ClientSize.Height - listTop - (hintHeight + gap + buttonHeight + bottomPad)));
        if (listHeight > available) listHeight = available;

        list.Location = new Point(sidePad, listTop);
        list.Size = new Size(Math.Max(Px(80), ClientSize.Width - sidePad * 2), listHeight);

        hint.Location = new Point(sidePad, hintY);
        hint.Size = new Size(Math.Max(Px(80), ClientSize.Width - sidePad * 2), hintHeight);

        LayoutButtons(rowY, sidePad, gap, buttonHeight);
    }

    /// <summary>
    /// 四个按钮在底部均分并居中。宽度按各按钮文字实测，超宽时压缩间隙。
    /// 窗口比"刚好放下"还窄时退化为左对齐贴边，不重叠。
    /// </summary>
    private void LayoutButtons(int rowY, int sidePad, int gap, int buttonHeight)
    {
        // 文字与字体构造期就定死，宽度本应永远不变——但 **DeviceDpi 不是**：
        // 构造期窗体句柄还没创建，DeviceDpi 还报着设计值；真正布局时（Load /
        // 首次 Resize）句柄建好、它才变成真值，而 GDI 量文字用的正是设备上下文，
        // 125% DPI 下同样一段文字要宽 25%。只按首次结果缓存、之后永不重测，
        // 高 DPI 上按钮文字就会被 AutoEllipsis 截掉。
        // 缓存以 DeviceDpi 为键：DPI 变了就重量（每种 DPI 只量一次，
        // Resize 这条每像素热路径上仍然零 GDI 调用）。
        if (buttonWidths is null || measuredAtDpi != DeviceDpi)
        {
            buttonWidths = new[] { activateButton, deleteButton, closeButton, refreshButton }
                .Select(b => Math.Max(Px(84), TextRenderer.MeasureText(b.Text, b.Font).Width + Px(24)))
                .ToArray();
            measuredAtDpi = DeviceDpi;
        }

        var buttons = new[] { activateButton, deleteButton, closeButton, refreshButton };
        // **必须 Clone**：int[] 是引用类型，直接用 buttonWidths 等于把下面压缩出来的
        // 宽度写回那份缓存。后果是窗口拉宽后按钮不恢复，反复横拖一路压到下限
        // 就再也回不去了。缓存里存的是"文字实测宽度"这个不变量，压缩只是本次布局的
        // 临时结果，两者不能是同一份数据。
        var widths = (int[])buttonWidths!.Clone();
        var total = widths.Sum() + gap * (buttons.Length - 1);
        var available = ClientSize.Width - sidePad * 2;
        if (total > available && buttons.Length > 1)
        {
            var shrink = (int)Math.Ceiling((total - available) / (double)(buttons.Length - 1));
            var floor = Px(72);
            for (var i = 0; i < widths.Length; i++) widths[i] = Math.Max(floor, widths[i] - shrink);
            total = widths.Sum() + gap * (buttons.Length - 1);
        }

        var x = Math.Max(sidePad, (ClientSize.Width - total) / 2);
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Size = new Size(widths[i], buttonHeight);
            buttons[i].Location = new Point(x, rowY);
            x += widths[i] + gap;
        }
    }

    /// <summary>供布局自检遍历的控件清单（与 Resize 里记录的是同一组）。</summary>
    internal Control[] DumpControls() =>
        new Control[] { title, list, hint, activateButton, deleteButton, closeButton, refreshButton };

    // 版本槽大小的人读格式见 HumanSize.FormatSize（**纯函数、可单测**）。它必须
    // 住在非 UI 的类型里：本窗体的类型初始化器会 new 三个 GDI+ Font，单测若经由
    // 这里调用纯函数，就会在无 GUI 的机器上被拖进字体初始化（StaticCouplingTests
    // 声明要消灭的形状）。窗体内不保留转发器——留一条转发路径，测试早晚会走回去。

    private EngineVersionEntry? Selected =>
        !Gone && list.SelectedItems.Count > 0
            ? list.SelectedItems[0].Tag as EngineVersionEntry
            : null;

    private void UpdateButtons()
    {
        if (busy || Gone) return;
        var sel = Selected;
        activateButton.Enabled = sel is not null && !sel.IsActive;
        deleteButton.Enabled = sel is not null && !sel.IsActive;
    }

    /// <summary>
    /// 统一管理忙碌态下的控件可用性。**「刷新」必须在这里显式恢复**：
    /// 之前它只出现在"被禁用"的那一行里，点过一次「切换」之后就永久变灰了，
    /// 而失败分支又不刷新列表，窗口就卡在"切换失败 + 不能刷新"的状态。
    /// </summary>
    private void SetBusy(bool value)
    {
        // 守卫用 Gone 而不是只判 IsDisposed：父窗体那边是 using var dialog + ShowDialog，
        // ShowDialog 返回后进入 Dispose(bool) 时 Disposing 已为真而 IsDisposed 仍为假，
        // 只判后者会放行一串控件写入。ReloadAsync 走的是 async void 事件处理器，
        // 真抛了就是无人接管的进程崩溃。
        if (Gone) return;
        busy = value;
        // 列表也要禁用：按钮禁用挡不住双击，而并发两次目录交换会抢同一个 engine 目录。
        list.Enabled = !value;
        refreshButton.Enabled = !value;
        activateButton.Enabled = !value;
        deleteButton.Enabled = !value;
        // 「关闭」**不**禁用：它是 CancelButton，禁用会连带吞掉 Esc（见 ConfirmClose）。
        // 需要拦的时候由 ConfirmClose 弹一句确认，三条关闭路径统一收口。
        if (!value) UpdateButtons();
    }

    /// <summary>
    /// 重新读取列表。列举版本要递归遍历每个引擎目录的 node_modules（本机实测 2.5 万个文件），
    /// 所以委托是异步的、整体放在后台线程——这里原本是同步调用，窗口一打开就冻结数秒
    /// 并被 Windows 判为未响应，而那时对话框连沙漏都来不及画出来。
    /// </summary>
    private async Task ReloadAsync()
    {
        if (busy || Gone) return;

        SetBusy(true);
        try { await ReloadAsyncCore(); }
        finally
        {
            // 与 FoldersForm 同一套纪律：busy 是状态（无条件复位），
            // 控件写入是副作用（Gone 之后不再做）。
            busy = false;
            if (!Gone) SetBusy(false);
        }
    }

    /// <summary>
    /// 重读列表的本体，**不碰 busy**：busy 的置位与复位归入口（按钮/Load 走
    /// <see cref="ReloadAsync"/>，切换/删除的 Core 自管）。
    ///
    /// 拆出它只有一个理由：切换/删除的四个内部调用点跑在 busy 窗口**之内**——
    /// 走 ReloadAsync 会被顶部的守卫当场退回，一行都不执行。此前正是这个形状：
    /// 列表停在旧状态（旧版本仍标"● 使用中"、已删条目还在）、hint 永远停在
    /// "正在切换/正在删除…"，与"失败也必须回到磁盘真相"的承诺相反，而且后续
    /// 按钮按陈旧 Tag 判定，整个对话框会话都停在错的状态上。
    /// </summary>
    private async Task ReloadAsyncCore()
    {
        if (Gone) return;
        hint.Text = "正在读取已安装的引擎版本…";
        try
        {
            // 先取数据再 BeginUpdate：委托要遍历磁盘，不能让它夹在 Begin/EndUpdate 中间，
            // 一旦抛异常 ListView 就永远停在更新态。
            var entries = await listVersions();

            // await 期间用户可能关了窗：主窗体那边是 using var dialog + ShowDialog，
            // 窗口一返回就 Dispose，此后任何控件访问都是 ObjectDisposedException。
            // 判据必须是 Gone 而不是"句柄还在不在"——见 Gone 的注释。
            if (Gone) return;

            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in entries)
                {
                    var item = new ListViewItem(entry.Version);
                    item.SubItems.Add(entry.IsActive ? "● 使用中" : "可切换");
                    item.SubItems.Add(HumanSize.FormatSize(entry.SizeBytes));
                    item.SubItems.Add(entry.InstalledAt.ToString("yyyy-MM-dd HH:mm"));
                    item.Tag = entry;
                    if (entry.IsActive) item.ForeColor = Color.FromArgb(34, 120, 60);
                    list.Items.Add(item);
                }
            }
            finally { list.EndUpdate(); }

            // 默认选中活动版本，方便一眼看到"现在用的是哪个"。
            // Tag 一律按 EngineVersionEntry 强转回去：Tag 是我们**自己**刚放进去的，
            // 拿不到就说明有别的代码往列表里塞过东西——那一行不能直接崩掉整次刷新。
            if (list.Items.Count > 0)
            {
                var active = list.Items.Cast<ListViewItem>()
                    .FirstOrDefault(i => (i.Tag as EngineVersionEntry)?.IsActive == true);
                (active ?? list.Items[0]).Selected = true;
            }

            hint.Text = list.Items.Count switch
            {
                0 => "没有检测到已安装的引擎。点「启动」会自动安装。",
                1 => "只装了一个版本。升级后旧版本会保留在这里，出问题可以切回去。",
                _ => "切换版本不会删除任何东西；删除操作只对未被使用的版本可用。"
            };
        }
        catch (Exception ex)
        {
            if (!Gone) hint.Text = "读取版本列表失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 所有 async void 事件处理器的统一入口。
    ///
    /// async void 里抛出的异常**绕过所有 try/catch**直达默认处理器，也就是整个启动器
    /// 当场崩掉。而这两个处理器在 await 之后要摸控件、弹 MessageBox，关窗竞态随时
    /// 可能插进来（ObjectDisposedException / InvalidOperationException）。
    /// 顶部注释反复强调"绝不能崩"，那就必须有一层兜底，而不是指望每处判据都写对。
    /// 刻意是**实例**方法：兜底弹窗要挂 owner（this），否则可能被主窗体的模态对话框
    /// 压在后面，表现为"点了没反应"。
    /// </summary>
    private async Task GuardedAsync(Func<Task> body)
    {
        try { await body(); }
        // 关窗竞态的判据必须带 Gone：裸吞 InvalidOperationException 会把真实逻辑 bug
        // （比如"在错误的线程上读控件"）也当成关窗竞态静默吞掉，与项目"坏了要报错"的
        // 纪律相反。窗体没消失时的 InvalidOperationException 是代码错误，该报就得报。
        catch (Exception ex) when (Gone && ex is ObjectDisposedException or InvalidOperationException)
        {
            // 关窗竞态：操作多半已经执行完，只是反馈没地方显示。正常路径，不打扰用户。
        }
        catch (Exception ex)
        {
            try { MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
        }
    }

    private void ActivateSelected() => _ = GuardedAsync(ActivateSelectedCore);

    private async Task ActivateSelectedCore()
    {
        var sel = Selected;
        if (sel is null || sel.IsActive || busy) return;

        // 确认框一律挂 owner（this）：owned 框会禁用属主窗体，确认期间没法再
        // 双击列表叠出第二个操作（两个 activateVersion 先后进 migrateGate 会让
        // 引擎被连停两次、活动版本随确认顺序来回翻转）；无属主的框也不参与
        // ShowDialog(this) 的模态 z 序，可能被压在对话框后面。
        var confirm = MessageBox.Show(this,
            $"把活动引擎切换到 {sel.Version}？\n\n" +
            "若引擎正在运行会先停止它（含整个进程树），然后交换目录。\n" +
            "完成后请回主界面点「启动」以新版本启动。当前版本不会被删除，随时可以切回来。",
            "切换引擎版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;
        if (Gone) return;

        // 切换是本对话框最重的操作：停引擎（最多等端口关闭 8 秒）+ 两次目录改名。
        // 之前用 .GetAwaiter().GetResult() 在 UI 线程上同步等，期间两个窗口全部冻结，
        // 看起来像死机——改成真正的 async，等待期间给出提示、按钮禁用。
        SetBusy(true);
        hint.Text = $"正在切换到 {sel.Version}…（停引擎 + 换目录，可能需要几秒）";

        string? error;
        // busy 是状态，必须**无条件**复位——与 ReloadAsync 的 finally 同一纪律。
        // 此前这里是"if (Gone) return;"在 SetBusy(false) 之前：窗体一旦在 await 期间
        // 被关掉，busy 就被永远留在 true。今天无害（closing/Disposed 都是粘性的，
        // 窗体随即被 Dispose，busy 再没人读），但它与 ReloadAsync 的纪律自相矛盾：
        // 只要有人放宽 Gone 的判据（例如改成"句柄没了但对象还在，好把结果交回父窗体弹提示"），
        // 这条早退就变成一条再也清不掉的 busy。try/finally 让复位与是否还活着无关。
        try
        {
            try { error = await activateVersion(sel.Version); }
            catch (Exception ex)
            {
                // 类型与堆栈要留痕（见 M10）：只把 Message 交给用户，
                // "Cannot find a directory" 与 "拒绝访问" 在这里长得一模一样。
                Swallow.Quiet(ex, $"engine-activate:{sel.Version}");
                error = ex.Message;
            }

            if (Gone) return;

            if (error is not null)
            {
                // ActivateEngineVersionAsync 的返回值约定：以 ActivateSwitchedWithWarningPrefix
                // 开头 = 切换本体已成功、但有警告（旧版本归档失败、副本保留在 engine.tmp）。
                // 标题与图标据此区分，不能把成功误报成"切换失败"。
                var switchedWithWarning = error.StartsWith(
                    HarnessForm.ActivateSwitchedWithWarningPrefix, StringComparison.Ordinal);
                var failureHint = (switchedWithWarning ? "切换完成（有警告）：" : "切换失败：") + error;
                MessageBox.Show(this, error,
                    switchedWithWarning ? "切换完成（有警告）" : "切换失败",
                    MessageBoxButtons.OK,
                    switchedWithWarning ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
                // 失败也必须回到磁盘真相：目录交换走到一半才失败的情况（新的顶上失败、
                // 旧的搬回也失败）会让列表里显示的"使用中"版本与磁盘实际状态不一致，
                // 而界面上没有任何提示。SetBusy 恢复了「刷新」，但数据没人去重读。
                //
                // 提示文案**必须排在 ReloadAsync 之后**再设：ReloadAsync 自己会把 hint
                // 改成"正在读取…"再改成统计文案，先设后刷的话失败原因当场就被覆盖掉了
                // ——用户看到列表刷新了一下，错误却消失得无影无踪。
                await ReloadAsyncCore();
                if (!Gone) hint.Text = failureHint;
                return;
            }
            await ReloadAsyncCore();
            if (Gone) return;
            MessageBox.Show(this,
                $"已切换到 {sel.Version}。\n\n回到主界面点「启动」以新版本启动。",
                "切换完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            busy = false;
            if (!Gone) SetBusy(false);
        }
    }

    private void DeleteSelected() => _ = GuardedAsync(DeleteSelectedCore);

    private async Task DeleteSelectedCore()
    {
        var sel = Selected;
        if (sel is null || sel.IsActive || busy) return;

        var confirm = MessageBox.Show(this,
            $"删除引擎版本 {sel.Version}？\n\n" +
            $"目录：{sel.Path}\n" +
            (sel.SizeBytes > 0 ? $"大小：约 {sel.SizeBytes / 1024.0 / 1024.0:N1} MB\n" : string.Empty) +
            "\n此操作不可恢复（该版本需要时可重新安装）。",
            "删除引擎版本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;
        if (Gone) return;

        // 删除整棵 node_modules（2.5 万个文件）是秒级操作，所以委托是异步的。
        SetBusy(true);
        hint.Text = $"正在删除 {sel.Version}…（清理上万个文件，可能需要几秒）";

        string? error;
        // 与 ActivateSelectedCore 同一纪律：busy 无条件复位（见那里的注释），
        // 失败异常按类型 + 堆栈留痕，不只留一句 Message。
        try
        {
            try { error = await deleteVersion(sel.Version); }
            catch (Exception ex)
            {
                Swallow.Quiet(ex, $"engine-delete:{sel.Version}");
                error = ex.Message;
            }

            if (Gone) return;

            if (error is not null)
            {
                var failureHint = "删除失败：" + error;
                MessageBox.Show(this, error, "删除引擎版本", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // 文案排在 ReloadAsync **之后**：ReloadAsync 自己会改 hint，
                // 先设后刷等于把失败原因当场抹掉。
                await ReloadAsyncCore();
                if (!Gone) hint.Text = failureHint;
                return;
            }
            await ReloadAsyncCore();
        }
        finally
        {
            busy = false;
            if (!Gone) SetBusy(false);
        }
    }
}

/// <summary>一个已安装的引擎版本。</summary>
internal sealed record EngineVersionEntry(
    string Version,
    string Path,
    bool IsActive,
    long SizeBytes,
    DateTime InstalledAt);
