using System.Diagnostics;

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
    private readonly Label title = new();
    private readonly ListView list = new();
    private readonly Label hint = new();
    private readonly Button openButton = new();
    private readonly Button copyButton = new();
    private readonly Button closeButton = new();
    private readonly Button refreshButton = new();

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
        Font = new Font("Microsoft YaHei UI", 9f);

        title.Text = "双击任意一行即可在资源管理器里打开";
        title.Location = new Point(16, 12);
        // 用 AutoSize 而不是硬编码 500px 宽：窗口拉到最窄时 500px 的标签会溢出客户区
        // （布局自检在 470px 宽度下抓到的）。
        title.AutoSize = true;
        title.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
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
        hint.Size = new Size(628, 18);
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
        refreshButton.Click += (_, _) => Reload();
        Controls.Add(refreshButton);

        CancelButton = closeButton;

        // 与"版本管理"同一套自适应：列表吃掉中间的剩余高度，提示与按钮锚在底部。
        // 只设 Anchor 不够——列表锚 Top 时拉高窗口它不动，按钮更是纵向写死。
        Resize += (_, _) =>
        {
            ApplyResponsiveLayout();
            LayoutDump.Capture($"目录 {ClientSize.Width}x{ClientSize.Height}", this,
                title, list, hint, openButton, copyButton, closeButton, refreshButton);
        };
        ApplyResponsiveLayout();
        DumpLayoutIfRequested();
        Reload();
    }

    /// <summary>DSH_LAYOUT_DUMP=1 时在多个尺寸下各排一次并记录几何，验证拉伸是否真的自适应。</summary>
    private void DumpLayoutIfRequested()
    {
        if (!LayoutDump.Enabled) return;
        Shown += (_, _) =>
        {
            foreach (var size in new[] { new Size(660, 286), new Size(660, 400), new Size(900, 320), new Size(470, 226) })
            {
                ClientSize = size;
                ApplyResponsiveLayout();
                LayoutDump.Capture($"目录 {size.Width}x{size.Height}", this,
                    title, list, hint, openButton, copyButton, closeButton, refreshButton);
            }
            Close();
        };
    }

    private Button NewButton(string text, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(120, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
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
        const int listTop = 36;
        const int hintHeight = 18;
        const int buttonHeight = 28;
        const int bottomPad = 14;
        const int margin = 16;
        const int gap = 8;

        var bottomArea = hintHeight + 8 + buttonHeight + bottomPad;
        var listHeight = Math.Max(52, ClientSize.Height - listTop - bottomArea);

        list.Location = new Point(margin, listTop);
        list.Size = new Size(Math.Max(80, ClientSize.Width - margin * 2), listHeight);

        hint.Location = new Point(margin, list.Bottom + 8);
        hint.Size = new Size(Math.Max(80, ClientSize.Width - margin * 2), hintHeight);

        LayoutButtons(ClientSize.Height - buttonHeight - bottomPad, margin, gap, buttonHeight);
    }

    /// <summary>
    /// 四个按钮在底部均分并居中；窗口比"刚好放下"还窄时压缩宽度并退化为贴边，不重叠。
    /// </summary>
    private void LayoutButtons(int rowY, int margin, int gap, int buttonHeight)
    {
        var buttons = new Button[] { openButton, copyButton, closeButton, refreshButton };

        var widths = buttons.Select(b => Math.Max(84, TextRenderer.MeasureText(b.Text, b.Font).Width + 24)).ToArray();
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
    private static List<Entry> Collect()
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarness");
        var dshHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

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
            new("更新日志", Path.Combine(appDir, "update-log.txt"), "插件更新的输出", true),
            new("会话记录", Path.Combine(dshHome, "sessions"), "历史对话", false),
            new("附件", Path.Combine(dshHome, "attachments"), "上传的图片等", false),
            new("存储 / 缓存", Path.Combine(dshHome, "storages"), "DSH 内部状态", false),
            new("凭据", Path.Combine(dshHome, ".credentials.yaml"), "账号密钥（请勿外传）", true),
            new("设置（遗留）", Path.Combine(dshHome, "settings.yaml.imported"), "迁移前的设置备份", true),
            new("设置", Path.Combine(dshHome, "settings.yaml"), "迁移后一般不再存在", true),
        };

        // 会话目录按项目分子目录，逐个也列出来，方便直接跳过去
        try
        {
            var sessions = Path.Combine(dshHome, "sessions");
            if (Directory.Exists(sessions))
            {
                foreach (var dir in Directory.GetDirectories(sessions))
                {
                    var count = Directory.GetDirectories(dir).Length;
                    candidates.Add(new Entry(
                        "  └ " + Path.GetFileName(dir),
                        dir,
                        $"{count} 个会话",
                        false));
                }
            }
        }
        catch { }

        return candidates.Where(e => File.Exists(e.Path) || Directory.Exists(e.Path)).ToList();
    }

    private void Reload()
    {
        var entries = Collect();
        list.BeginUpdate();
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
        list.EndUpdate();

        if (list.Items.Count > 0) list.Items[0].Selected = true;
        hint.Text = $"共 {entries.Count} 项（只显示真实存在的位置）" +
                    "　·　「凭据」含密钥，分享截图前请留意";
        UpdateButtons();
    }

    private Entry? Selected =>
        list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Entry : null;

    private void UpdateButtons()
    {
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
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{sel.Path}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(sel.Path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开：\n{sel.Path}\n\n{ex.Message}",
                "打开目录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopySelectedPath()
    {
        var sel = Selected;
        if (sel is null) return;
        try
        {
            Clipboard.SetText(sel.Path);
            hint.Text = "已复制：" + sel.Path;
        }
        catch
        {
            MessageBox.Show("复制失败，剪贴板被其他程序占用。", "复制路径",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
