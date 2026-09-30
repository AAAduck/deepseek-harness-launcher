using System.Text;

namespace DeepSeekHarness;

/// <summary>
/// 布局自检的公共部分。
///
/// 加这个是因为靠截图/自动化去验证 Windows 布局既慢又不可靠（UIAutomation 读不到这些窗口，
/// PrintWindow 也拿不到稳定结果），而"按钮有没有溢出、有没有重叠、尺寸是否一致"是
/// 可以直接测量的事实。DSH_LAYOUT_DUMP=1 时把几何写进 layout-dump.txt。
///
/// 注意写文件而不是 Console：本程序是 WinExe、没有控制台，Console.WriteLine 会静默消失
/// （第一版就是这么白忙一场的）。
/// </summary>
internal static class LayoutDump
{
    private static readonly object Gate = new();
    private static int captureCount;

    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("DSH_LAYOUT_DUMP"), "1", StringComparison.Ordinal);

    /// <summary>已记录的次数。自检时用来判断"跑了几个尺寸、可以收工了"。</summary>
    internal static int CaptureCount => captureCount;

    private static string DumpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness", "layout-dump.txt");

    /// <summary>把一次布局快照追加到 dump 文件，供自动化验证。</summary>
    internal static void Capture(string label, Form form, params Control[] controls)
        => Capture(label, form, (IEnumerable<Control>)controls);

    internal static void Capture(string label, Form form, IEnumerable<Control> controls)
    {
        if (!Enabled) return;
        try
        {
            var list = controls.ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"[{label}] ClientSize={form.ClientSize.Width}x{form.ClientSize.Height}");
            foreach (var c in list)
            {
                var right = c.Left + c.Width;
                var bottom = c.Top + c.Height;
                var flags = new List<string>();
                if (right > form.ClientSize.Width) flags.Add("右溢出");
                if (bottom > form.ClientSize.Height) flags.Add("下溢出");
                if (c.Left < 0 || c.Top < 0) flags.Add("负坐标");
                sb.AppendLine($"  {c.GetType().Name,-10} \"{Trim(c.Text)}\" " +
                              $"x={c.Left,4} y={c.Top,4} w={c.Width,4} h={c.Height,3} " +
                              $"右={right,4} 下={bottom,4} " +
                              $"{(flags.Count > 0 ? "✗ " + string.Join(",", flags) : "✓")}");
            }
            for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
            {
                if (list[i].Bounds.IntersectsWith(list[j].Bounds))
                    sb.AppendLine($"  ✗ 重叠: \"{Trim(list[i].Text)}\" 与 \"{Trim(list[j].Text)}\"");
            }

            lock (Gate)
            {
                captureCount++;
                Directory.CreateDirectory(Path.GetDirectoryName(DumpPath)!);
                File.AppendAllText(DumpPath, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= 12 ? text : text[..12] + "…";
    }
}
