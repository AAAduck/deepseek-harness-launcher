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
    private readonly ListView list = new();
    private readonly Label hint = new();
    private readonly Button openButton = new();
    private readonly Button copyButton = new();

    /// <summary>一个相关位置。File 类条目在资源管理器里用"选中"而不是"打开"。</summary>
    private sealed record Entry(string Name, string Path, string Note, bool IsFile);

    internal FoldersForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "DeepSeek Harness 相关目录";
        ClientSize = new Size(660, 396);
        MinimumSize = new Size(600, 340);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f);

        var title = new Label
        {
            Text = "双击任意一行即可在资源管理器里打开",
            Location = new Point(16, 12),
            Size = new Size(500, 20),
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
        };
        Controls.Add(title);

        list.Location = new Point(16, 38);
        list.Size = new Size(628, 292);
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
        list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(list);

        hint.Location = new Point(16, 336);
        hint.Size = new Size(628, 18);
        hint.ForeColor = Color.FromArgb(108, 114, 126);
        hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(hint);

        openButton = NewButton("在资源管理器中打开", 16, Color.FromArgb(34, 170, 85), 150);
        openButton.Click += (_, _) => OpenSelected();
        Controls.Add(openButton);

        copyButton = NewButton("复制路径", 174, Color.FromArgb(58, 124, 240), 94);
        copyButton.Click += (_, _) => CopySelectedPath();
        Controls.Add(copyButton);

        var closeButton = NewButton("关闭", 400, Color.FromArgb(114, 122, 143), 94);
        closeButton.Click += (_, _) => Close();
        Controls.Add(closeButton);

        var refreshButton = NewButton("刷新", 502, Color.FromArgb(114, 122, 143), 94);
        refreshButton.Click += (_, _) => Reload();
        Controls.Add(refreshButton);

        CancelButton = closeButton;
        Reload();
    }

    private Button NewButton(string text, int x, Color backColor, int width)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, 358),
            Size = new Size(width, 28),
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
