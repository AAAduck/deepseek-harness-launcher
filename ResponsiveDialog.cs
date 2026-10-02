namespace DeepSeekHarness;

/// <summary>
/// 「相关目录」（FoldersForm）与「版本管理」（EngineVersionsForm）两个可缩放
/// 对话框的共享骨架：底边反推的自适应布局、DPI 换算、忙碌态与关闭收口。
///
/// 为什么抽基类：ApplyResponsiveLayout / LayoutButtons / ConfirmClose / SetBusy
/// 在两个窗体里曾是逐行相同的两份拷贝（约 150 行 ×2），而"两个窗体的公式就此
/// 对齐"此前只能靠人肉维护——"列表下限不得盖过上限""按钮宽度缓存必须 Clone"
/// 这类对称 bug 已经各自修过一次，公式再分叉一次就得多修一次。现在公式只存在
/// 这一份。
/// </summary>
internal abstract class ResponsiveDialog : Form
{
    /// <summary>
    /// 正在跑一次异步操作。**只是 UI 层防重入**，不是并发互斥：真正保护 engine 目录
    /// 等共享数据的互斥在数据层（migrateGate / 单实例互斥体）。两个对话框实例可以
    /// 同时活着，它们各自的 busy 互不相识——所以这里禁用的控件挡不住
    /// "关窗后重开一个对话框再操作"，也别把它当成并发安全的依据。
    /// </summary>
    protected bool busy;

    /// <summary>窗体正在关闭。用来区分"已经没了"与"还没显示"——这两件事完全不同。</summary>
    protected bool closing;

    /// <summary>
    /// 布局自检（Program.RunLayoutSelfTest）的静默关闭开关。编程式 Close() 的
    /// CloseReason 是 UserClosing（WinForms 文档原文："either programmatically
    /// or through a user action"），busy 没复位时会命中 ConfirmClose 的确认框——
    /// 自检进程没有人应答，就挂死在那里。自检关闭必须绕过询问。
    /// </summary>
    internal bool QuietClose;

    /// <summary>
    /// AutoScaleMode.Dpi 在**没有** AutoScaleDimensions、也没有 PerformAutoScale 的
    /// 情况下是空操作：WinForms 按默认的 96/96 算缩放因子，在 120/144 DPI 上得到的
    /// 仍是 1.0，于是布局常量其实全是 96-DPI 设计像素。这里明确关掉自动缩放，
    /// 由 <see cref="Px"/> 按实际 DeviceDpi 换算——一套机制，不与 WinForms 的
    /// 自动缩放打架（主窗体 SetFixedClientSize 的注释记的就是那个坑）。
    /// </summary>
    protected ResponsiveDialog()
    {
        AutoScaleMode = AutoScaleMode.None;
    }

    // ---- 子类提供的形状 ----------------------------------------------------

    /// <summary>中间吃剩余高度的列表控件。</summary>
    protected abstract Control ResizableList { get; }
    /// <summary>贴底的提示标签。</summary>
    protected abstract Control HintLabel { get; }
    /// <summary>底部一行按钮（从左到右，含「关闭」）。</summary>
    protected abstract Control[] ButtonRow { get; }
    /// <summary>「关闭」按钮：它是 CancelButton，忙碌期间保持可用以保住 Esc（见 ConfirmClose）。</summary>
    protected abstract Control CloseButton { get; }
    /// <summary>布局自检日志里的窗口名（"目录" / "版本管理"）。</summary>
    protected abstract string DumpCaption { get; }

    // 以下三个是 96-DPI 的设计值：布局常量在代码里保持可读的设计像素，
    // 运行时经 <see cref="Px"/> 换算成实际像素。
    protected abstract int ListTopDesign { get; }
    protected abstract int HintHeightDesign { get; }
    protected abstract int ButtonHeightDesign { get; }

    /// <summary>忙碌解除后恢复按钮可用性（各窗体的可点击条件不同）。</summary>
    protected abstract void UpdateButtons();

    /// <summary>布局自检遍历的控件清单。</summary>
    internal abstract Control[] DumpControls();

    /// <summary>
    /// 窗体已经不可用了（已释放或正在关闭）。
    /// **刻意不用 <c>IsHandleCreated</c>**：它表达的是"还没显示"，不是"已经没了"。
    /// </summary>
    protected bool Gone => IsDisposed || Disposing || closing;

    /// <summary>
    /// 在子类构造完成（底部按钮已创建）**之后**调用一次：挂上 Resize 自适应与
    /// 布局自检。必须挂在按钮之后——处理链路会枚举 ButtonRow，提前挂会在构造期
    /// 的 ClientSize 赋值上撞到 null。
    /// </summary>
    protected void HookResizeLayout()
    {
        Resize += (_, _) =>
        {
            ApplyResponsiveLayout();
            // LayoutDump.Enabled 必须在**调用点**先判一次：参数里的插值字符串与
            // Control[] 在进入 Capture 之前就已构造好，而 Resize 是拖边框时每像素
            // 都触发的热路径，没开自检的普通用户不该为它付费。
            if (LayoutDump.Enabled)
                LayoutDump.Capture($"{DumpCaption} {ClientSize.Width}x{ClientSize.Height}", this, DumpControls());
        };
    }

    // ---- DPI 换算 ----------------------------------------------------------

    /// <summary>
    /// 96-DPI 设计值 → 本机实际像素。<see cref="DeviceDpi"/> 在句柄创建前可能还是
    /// 设计值，句柄建好之后（Load / 首次 Resize）才是真值——所以布局常量**不能**
    /// 在字段初始化器里算死，必须每次布局时现算（ApplyResponsiveLayout 已经如此）。
    /// </summary>
    protected double DpiScale => Math.Max(1.0, DeviceDpi / 96.0);

    /// <summary>按 <see cref="DpiScale"/> 把 96-DPI 的设计像素换算成实际像素（向上取整）。</summary>
    protected int Px(double designValue) => (int)Math.Ceiling(designValue * DpiScale);

    // 两个窗体在这几个常量上本来就一致（左右留白 16、间隙 8、底垫 14），收进基类。
    private const int SidePadDesign = 16;
    private const int GapDesign = 8;
    private const int BottomPadDesign = 14;

    /// <summary>
    /// 让内容跟着窗口尺寸走：列表吃掉中间的剩余高度，提示与按钮行都从客户区
    /// **底边反推**，永远贴底、互不重叠。
    ///
    /// 列表高度有下限保护，但**下限不得盖过上限**：空间不够时若下限赢了，
    /// 列表会压到提示之上——宁可压扁也不重叠（MinimumSize 已保证正常拖拽
    /// 不会走到这个分支）。
    /// </summary>
    protected void ApplyResponsiveLayout()
    {
        var listTop = Px(ListTopDesign);
        var hintHeight = Px(HintHeightDesign);
        var buttonHeight = Px(ButtonHeightDesign);
        var bottomPad = Px(BottomPadDesign);
        var sidePad = Px(SidePadDesign);
        var gap = Px(GapDesign);

        var rowY = ClientSize.Height - buttonHeight - bottomPad;
        var hintY = rowY - gap - hintHeight;

        var available = hintY - gap - listTop;
        var listHeight = Math.Max(Px(24), Math.Min(
            available, ClientSize.Height - listTop - (hintHeight + gap + buttonHeight + bottomPad)));
        if (listHeight > available) listHeight = available;

        ResizableList.Location = new Point(sidePad, listTop);
        ResizableList.Size = new Size(Math.Max(Px(80), ClientSize.Width - sidePad * 2), listHeight);

        HintLabel.Location = new Point(sidePad, hintY);
        HintLabel.Size = new Size(Math.Max(Px(80), ClientSize.Width - sidePad * 2), hintHeight);

        LayoutButtons(rowY, sidePad, gap, buttonHeight);
    }

    /// <summary>四个按钮的实测文字宽度，按 <see cref="measuredAtDpi"/> 那个 DPI 量一次即可。</summary>
    private int[]? buttonWidths;
    /// <summary>上面那份宽度是在哪个 DPI 下量的。DPI 变了必须重量（GDI 量的是设备像素）。</summary>
    private int measuredAtDpi = -1;

    /// <summary>
    /// 底部一行按钮：宽度按各按钮文字实测（GDI 量的是设备像素，缓存以 DeviceDpi
    /// 为键——构造期句柄未建时 DeviceDpi 还报设计值，只量一次的话高 DPI 上文字
    /// 会被 AutoEllipsis 截掉），超宽时压缩间隙，整体居中；保证不重叠。
    ///
    /// 宽度缓存**必须 Clone** 后再压缩：缓存里存的是"文字实测宽度"这个不变量，
    /// 把压缩结果直接写回去，窗口拉宽后按钮就再也回不来了。
    /// </summary>
    private void LayoutButtons(int rowY, int sidePad, int gap, int buttonHeight)
    {
        if (buttonWidths is null || measuredAtDpi != DeviceDpi)
        {
            buttonWidths = ButtonRow
                .Select(b => Math.Max(Px(84), TextRenderer.MeasureText(b.Text, b.Font).Width + Px(24)))
                .ToArray();
            measuredAtDpi = DeviceDpi;
        }

        var buttons = ButtonRow;
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

    /// <summary>
    /// 统一管理忙碌态：列表与整行按钮全部禁用（只禁按钮挡不住双击，而目录扫描 /
    /// 目录交换期间命中旧 Tag 或并发两次交换都是真实的踩踏），唯独「关闭」不动——
    /// 它是 CancelButton，禁用会连带吞掉 Esc（Button 在 Enabled==false 时
    /// CanSelect 为 false，ProcessDialogKey 的 Escape 分支直接跳过）。
    /// 需要拦的时候由 ConfirmClose 问一句，三条关闭路径统一收口。
    /// </summary>
    protected void SetBusy(bool value)
    {
        if (Gone) return;
        busy = value;
        ResizableList.Enabled = !value;
        foreach (var button in ButtonRow)
            if (!ReferenceEquals(button, CloseButton)) button.Enabled = !value;
        if (!value) UpdateButtons();
    }

    /// <summary>
    /// 关闭时收口。被取消的关闭**不能**把 closing 置起来——FormClosing 在
    /// <c>e.Cancel</c> 时照样触发，而 closing 粘性，误置一次 <see cref="Gone"/>
    /// 就永远为真（列表永远空、提示永远停在"正在读取…"，且不报错）。
    ///
    /// 只有**用户自己点的关闭**才询问。系统关机/注销/任务管理器结束同样会走
    /// FormClosing，而此刻 busy 的概率最高（正在扫大量文件）：弹模态框 +
    /// e.Cancel = true 就是 Windows 意义上的"此应用阻止关机"，用户只能强杀，
    /// 连日志都留不下。那种场景下"操作会不会跑完"根本不是用户需要做决定的事。
    /// </summary>
    protected void ConfirmClose(FormClosingEventArgs e, string busyTitle, string busyMessage)
    {
        if (e.CloseReason != CloseReason.UserClosing) { closing = true; return; }
        if (QuietClose) { closing = true; return; }   // 布局自检：编程式关闭不询问
        if (busy)
        {
            var go = MessageBox.Show(this, busyMessage, busyTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (go != DialogResult.Yes) { e.Cancel = true; return; }
        }
        closing = true;
    }
}
