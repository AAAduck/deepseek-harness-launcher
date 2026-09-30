using System.Text;

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

    internal EngineVersionsForm(
        Func<Task<IReadOnlyList<EngineVersionEntry>>> listVersions,
        Func<string, Task<string?>> activateVersion,
        Func<string, Task<string?>> deleteVersion)
    {
        this.listVersions = listVersions;
        this.activateVersion = activateVersion;
        this.deleteVersion = deleteVersion;

        AutoScaleMode = AutoScaleMode.Dpi;
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
            LayoutDump.Capture($"版本管理 {ClientSize.Width}x{ClientSize.Height}", this,
                title, list, hint, activateButton, deleteButton, closeButton, refreshButton);
        };
        ApplyResponsiveLayout();
        _ = ReloadAsync();
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
        var rowY = ClientSize.Height - ButtonHeight - BottomPad;
        var hintY = rowY - Gap - HintHeight;

        // 列表吃掉中间的剩余高度；上限同样由底边决定，保证不盖住提示。
        var maxList = Math.Max(40, hintY - Gap - ListTop);
        var listHeight = Math.Clamp(
            ClientSize.Height - ListTop - (HintHeight + Gap + ButtonHeight + BottomPad), 40, maxList);

        list.Location = new Point(SidePad, ListTop);
        list.Size = new Size(Math.Max(80, ClientSize.Width - SidePad * 2), listHeight);

        hint.Location = new Point(SidePad, hintY);
        hint.Size = new Size(Math.Max(80, ClientSize.Width - SidePad * 2), HintHeight);

        LayoutButtons(rowY);
    }

    /// <summary>
    /// 四个按钮在底部均分并居中。宽度按各按钮文字实测，超宽时压缩间隙。
    /// 窗口比"刚好放下"还窄时退化为左对齐贴边，不重叠。
    /// </summary>
    private void LayoutButtons(int rowY)
    {
        var buttons = new Button[] { activateButton, deleteButton, closeButton, refreshButton };

        var widths = buttons.Select(b => Math.Max(84, TextRenderer.MeasureText(b.Text, b.Font).Width + 24)).ToArray();
        var total = widths.Sum() + Gap * (buttons.Length - 1);
        var available = ClientSize.Width - SidePad * 2;
        if (total > available && buttons.Length > 1)
        {
            var shrink = (int)Math.Ceiling((total - available) / (double)(buttons.Length - 1));
            for (var i = 0; i < widths.Length; i++) widths[i] = Math.Max(72, widths[i] - shrink);
            total = widths.Sum() + Gap * (buttons.Length - 1);
        }

        var x = Math.Max(SidePad, (ClientSize.Width - total) / 2);
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Size = new Size(widths[i], ButtonHeight);
            buttons[i].Location = new Point(x, rowY);
            x += widths[i] + Gap;
        }
    }

    /// <summary>供布局自检遍历的控件清单（与 Resize 里记录的是同一组）。</summary>
    internal Control[] DumpControls() =>
        new Control[] { title, list, hint, activateButton, deleteButton, closeButton, refreshButton };

    private EngineVersionEntry? Selected =>
        !IsDisposed && list.SelectedItems.Count > 0
            ? list.SelectedItems[0].Tag as EngineVersionEntry
            : null;

    private void UpdateButtons()
    {
        if (busy || IsDisposed) return;
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
        if (IsDisposed) return;
        busy = value;
        // 列表也要禁用：按钮禁用挡不住双击，而并发两次目录交换会抢同一个 engine 目录。
        list.Enabled = !value;
        refreshButton.Enabled = !value;
        activateButton.Enabled = !value;
        deleteButton.Enabled = !value;
        if (!value) UpdateButtons();
    }

    /// <summary>
    /// 重新读取列表。列举版本要递归遍历每个引擎目录的 node_modules（本机实测 2.5 万个文件），
    /// 所以委托是异步的、整体放在后台线程——这里原本是同步调用，窗口一打开就冻结数秒
    /// 并被 Windows 判为未响应，而那时对话框连沙漏都来不及画出来。
    /// </summary>
    private async Task ReloadAsync()
    {
        if (busy || IsDisposed) return;

        SetBusy(true);
        hint.Text = "正在读取已安装的引擎版本…";
        try
        {
            // 先取数据再 BeginUpdate：委托要遍历磁盘，不能让它夹在 Begin/EndUpdate 中间，
            // 一旦抛异常 ListView 就永远停在更新态。
            var entries = await listVersions();

            // await 期间用户可能关了窗：主窗体那边是 using var dialog + ShowDialog，
            // 窗口一返回就 Dispose，此后任何控件访问都是 ObjectDisposedException。
            if (IsDisposed || !IsHandleCreated) return;

            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in entries)
                {
                    var item = new ListViewItem(entry.Version);
                    item.SubItems.Add(entry.IsActive ? "● 使用中" : "可切换");
                    item.SubItems.Add(entry.SizeBytes > 0 ? $"{entry.SizeBytes / 1024.0 / 1024.0:N0} MB" : "—");
                    item.SubItems.Add(entry.InstalledAt.ToString("yyyy-MM-dd HH:mm"));
                    item.Tag = entry;
                    if (entry.IsActive) item.ForeColor = Color.FromArgb(34, 120, 60);
                    list.Items.Add(item);
                }
            }
            finally { list.EndUpdate(); }

            // 默认选中活动版本，方便一眼看到"现在用的是哪个"
            if (list.Items.Count > 0)
            {
                var active = list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((EngineVersionEntry)i.Tag!).IsActive);
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
            if (!IsDisposed) hint.Text = "读取版本列表失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ActivateSelected()
    {
        var sel = Selected;
        if (sel is null || sel.IsActive || busy) return;

        var confirm = MessageBox.Show(
            $"把活动引擎切换到 {sel.Version}？\n\n" +
            "若引擎正在运行会先停止它（含整个进程树），然后交换目录。\n" +
            "完成后请回主界面点「启动」以新版本启动。当前版本不会被删除，随时可以切回来。",
            "切换引擎版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;
        if (IsDisposed) return;

        // 切换是本对话框最重的操作：停引擎（最多等端口关闭 8 秒）+ 两次目录改名。
        // 之前用 .GetAwaiter().GetResult() 在 UI 线程上同步等，期间两个窗口全部冻结，
        // 看起来像死机——改成真正的 async，等待期间给出提示、按钮禁用。
        SetBusy(true);
        hint.Text = $"正在切换到 {sel.Version}…（停引擎 + 换目录，可能需要几秒）";

        string? error;
        try { error = await activateVersion(sel.Version); }
        catch (Exception ex) { error = ex.Message; }

        // 等待期间用户可以点「关闭」或标题栏 X。主窗体那边是 using var dialog + ShowDialog，
        // 窗口一返回就 Dispose；此后再碰控件就是 ObjectDisposedException，从 async void
        // 抛出无人接管会直接崩掉进程——而切换其实早就成功了。
        if (IsDisposed || !IsHandleCreated) return;

        SetBusy(false);
        if (error is not null)
        {
            hint.Text = "切换失败：" + error;
            MessageBox.Show(error, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        await ReloadAsync();
        if (IsDisposed || !IsHandleCreated) return;
        MessageBox.Show(
            $"已切换到 {sel.Version}。\n\n回到主界面点「启动」以新版本启动。",
            "切换完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async void DeleteSelected()
    {
        var sel = Selected;
        if (sel is null || sel.IsActive || busy) return;

        var confirm = MessageBox.Show(
            $"删除引擎版本 {sel.Version}？\n\n" +
            $"目录：{sel.Path}\n" +
            (sel.SizeBytes > 0 ? $"大小：约 {sel.SizeBytes / 1024.0 / 1024.0:N0} MB\n" : string.Empty) +
            "\n此操作不可恢复（该版本需要时可重新安装）。",
            "删除引擎版本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;
        if (IsDisposed) return;

        // 删除整棵 node_modules（2.5 万个文件）是秒级操作，所以委托是异步的。
        SetBusy(true);
        hint.Text = $"正在删除 {sel.Version}…（清理上万个文件，可能需要几秒）";

        string? error;
        try { error = await deleteVersion(sel.Version); }
        catch (Exception ex) { error = ex.Message; }

        if (IsDisposed || !IsHandleCreated) return;

        SetBusy(false);
        if (error is not null)
        {
            hint.Text = "删除失败：" + error;
            MessageBox.Show(error, "删除引擎版本", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        await ReloadAsync();
    }
}

/// <summary>一个已安装的引擎版本。</summary>
internal sealed record EngineVersionEntry(
    string Version,
    string Path,
    bool IsActive,
    long SizeBytes,
    DateTime InstalledAt);
