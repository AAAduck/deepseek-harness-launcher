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
    private readonly ListView list = new();
    private readonly Label hint = new();
    private readonly Button activateButton = new();
    private readonly Button deleteButton = new();
    private readonly Func<string, string?> activateVersion;
    private readonly Action<string> deleteVersion;
    private readonly Func<IReadOnlyList<EngineVersionEntry>> listVersions;

    internal EngineVersionsForm(
        Func<IReadOnlyList<EngineVersionEntry>> listVersions,
        Func<string, string?> activateVersion,
        Action<string> deleteVersion)
    {
        this.listVersions = listVersions;
        this.activateVersion = activateVersion;
        this.deleteVersion = deleteVersion;

        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "引擎版本管理";
        ClientSize = new Size(560, 330);
        MinimumSize = new Size(520, 300);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f);

        var title = new Label
        {
            Text = "本机已安装的 DSH 引擎版本",
            Location = new Point(16, 12),
            Size = new Size(400, 20),
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
        };
        Controls.Add(title);

        list.Location = new Point(16, 38);
        list.Size = new Size(528, 226);
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
        list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(list);

        hint.Location = new Point(16, 272);
        hint.Size = new Size(528, 34);
        hint.ForeColor = Color.FromArgb(108, 114, 126);
        hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(hint);

        activateButton = NewButton("切换到此版本", 16, Color.FromArgb(34, 170, 85));
        activateButton.Click += (_, _) => ActivateSelected();
        Controls.Add(activateButton);

        deleteButton = NewButton("删除", 132, Color.FromArgb(224, 69, 62));
        deleteButton.Click += (_, _) => DeleteSelected();
        Controls.Add(deleteButton);

        var closeButton = NewButton("关闭", 248, Color.FromArgb(114, 122, 143));
        closeButton.Click += (_, _) => Close();
        Controls.Add(closeButton);

        var refreshButton = NewButton("刷新", 364, Color.FromArgb(58, 124, 240));
        refreshButton.Click += (_, _) => Reload();
        Controls.Add(refreshButton);

        CancelButton = closeButton;

        Reload();
    }

    private Button NewButton(string text, int x, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, 312),
            Size = new Size(108, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void Reload()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var entry in listVersions())
        {
            var item = new ListViewItem(entry.Version);
            item.SubItems.Add(entry.IsActive ? "● 使用中" : "可切换");
            item.SubItems.Add(entry.SizeBytes > 0 ? $"{entry.SizeBytes / 1024.0 / 1024.0:N0} MB" : "—");
            item.SubItems.Add(entry.InstalledAt.ToString("yyyy-MM-dd HH:mm"));
            item.Tag = entry;
            if (entry.IsActive) item.ForeColor = Color.FromArgb(34, 120, 60);
            list.Items.Add(item);
        }
        list.EndUpdate();

        // 默认选中活动版本，方便一眼看到"现在用的是哪个"
        if (list.Items.Count > 0)
        {
            var active = list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((EngineVersionEntry)i.Tag!).IsActive);
            (active ?? list.Items[0]).Selected = true;
        }

        hint.Text = list.Items.Count switch
        {
            0 => "没有检测到已安装的引擎。点「开始」会自动安装。",
            1 => "只装了一个版本。升级后旧版本会保留在这里，出问题可以切回去。",
            _ => "切换版本不会删除任何东西；删除操作只对未被使用的版本可用。"
        };
        UpdateButtons();
    }

    private EngineVersionEntry? Selected =>
        list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as EngineVersionEntry : null;

    private void UpdateButtons()
    {
        var sel = Selected;
        activateButton.Enabled = sel is not null && !sel.IsActive;
        deleteButton.Enabled = sel is not null && !sel.IsActive;
    }

    private void ActivateSelected()
    {
        var sel = Selected;
        if (sel is null || sel.IsActive) return;

        var confirm = MessageBox.Show(
            $"把活动引擎切换到 {sel.Version}？\n\n" +
            "切换需要引擎处于停止状态。若正在运行，请切换后点主界面的「开始」重新启动。\n" +
            "当前版本不会被删除，随时可以切回来。",
            "切换引擎版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        var error = activateVersion(sel.Version);
        if (error is not null)
        {
            MessageBox.Show(error, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Reload();
        MessageBox.Show(
            $"已切换到 {sel.Version}。\n\n请回到主界面点「开始」重启引擎使其生效。",
            "切换完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DeleteSelected()
    {
        var sel = Selected;
        if (sel is null || sel.IsActive) return;

        var confirm = MessageBox.Show(
            $"删除引擎版本 {sel.Version}？\n\n" +
            $"目录：{sel.Path}\n" +
            (sel.SizeBytes > 0 ? $"大小：约 {sel.SizeBytes / 1024.0 / 1024.0:N0} MB\n" : string.Empty) +
            "\n此操作不可恢复（该版本需要时可重新安装）。",
            "删除引擎版本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        deleteVersion(sel.Version);
        Reload();
    }
}

/// <summary>一个已安装的引擎版本。</summary>
internal sealed record EngineVersionEntry(
    string Version,
    string Path,
    bool IsActive,
    long SizeBytes,
    DateTime InstalledAt);
