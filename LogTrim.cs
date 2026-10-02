using System.Text;

namespace DeepSeekHarness;

/// <summary>
/// 日志滚动截断。**纯函数、可单测**——它曾把整份日志清成 0 字节且零报错，
/// 必须被单测钉住；独立成纯类型是"纯函数住在纯类型里"的同一纪律
/// （见 <see cref="Semver"/> 头部说明）。
/// </summary>
internal static class LogTrim
{
    /// <summary>
    /// 超过 256 KB 时只保留末尾约 128 KB，且必须从一条记录的开头切起。
    /// 单条记录本身就超过 128 KB 时，保留该记录的最后 128 KB——
    /// 此时确实无法保证记录完整，但总比留下一个无头片段更有用。
    /// startup-log / update-log / crash-log 共用（截断策略必须一致，别只改一处）。
    /// </summary>
    internal static string Tail(string text)
    {
        const int MaxBytes = 256 * 1024;
        const int KeepBytes = 128 * 1024;
        if (Encoding.UTF8.GetByteCount(text) <= MaxBytes) return text;

        var lines = text.Split('\n');
        var kept = new List<string>();
        var bytes = 0;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + 1;
            if (bytes + lineBytes > KeepBytes) break;
            kept.Add(lines[i]);
            bytes += lineBytes;
        }
        kept.Reverse();
        var result = string.Join("\n", kept);
        if (result.Length > 0) return result;

        // 到这里说明 kept 里只有空行：最后一条记录本身就是超过保留预算的单行
        // （pnpm 的裸 \r 进度串合成一行很常见），按整条滚动一行都放不下——保留尾部。
        // 切点必须落在"换行之后还剩内容"的位置上（切点等于尾段末尾会把整份日志
        // 清成 0 字节，见 DESIGN-NOTES.md §10）；尾段内没有换行时原样保留。
        var tail = text[^Math.Min(text.Length, KeepBytes / 3)..];
        var nl = tail.IndexOf('\n');
        return nl >= 0 && nl + 1 < tail.Length ? tail[(nl + 1)..] : tail;
    }
}
