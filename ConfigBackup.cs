using System.Text;
using System.Text.Json;

namespace DeepSeekHarness;

/// <summary>
/// DSH 配置快照。
///
/// 为什么需要它：实测本机发生过两次真实的数据丢失，都是同一个原因——
/// DSH 在版本升级时把 <c>$DSH_HOME/settings.yaml</c> 改名成 <c>settings.yaml.imported</c>，
/// 再按「section id = profile 条目 id」逐段导入；<b>id 对不上任何 profile 条目的段直接丢失</b>。
/// 受害的是 jet-hub 的账号列表和 llm-pi-ai 的自定义模型供应商（基元 / 商汤）。
/// 而写在 <c>profiles\&lt;名字&gt;\cordis.patch.yml</c> 里的配置不受影响，
/// 所以那次是从 desktop profile 的 patch 里把供应商捞回来的。
///
/// 这个类只做一件最朴素的事：把「体积小、丢了很痛、且不随版本重建」的那些配置
/// 按时间戳各存一份。刻意<b>不</b>包含 node_modules / engine 之类可重建的大目录。
/// </summary>
internal static class ConfigBackup
{
    /// <summary>保留的最近快照数。每个快照只有几十 KB，留几份成本极低。</summary>
    private const int KeepSnapshots = 8;

    internal static string BackupRoot => Path.Combine(LocalAppDir, "config-backups");

    private static string LocalAppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness");

    private static string DshHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    /// <summary>
    /// 要备份的文件（相对 $DSH_HOME）。只挑配置类小文件：
    /// 凭据、设置、以及各 profile 的 patch——
    /// patch 是唯一"不参与 settings 迁移所以最安全"的自定义配置位置。
    /// </summary>
    private static IEnumerable<string> ConfigFiles()
    {
        yield return ".credentials.yaml";
        yield return "settings.yaml";
        yield return "settings.yaml.imported";

        var profiles = Path.Combine(DshHome, "profiles");
        if (!Directory.Exists(profiles)) yield break;
        foreach (var dir in Directory.GetDirectories(profiles))
        {
            var name = Path.GetFileName(dir);
            // node_modules 是 pnpm 的链接树，既大又能重建，不备份。
            if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in new[] { "cordis.patch.yml", "cordis.yml", "package.json" })
            {
                var full = Path.Combine(dir, file);
                if (File.Exists(full)) yield return Path.Combine("profiles", name, file);
            }
        }
    }

    /// <summary>
    /// 生成一份快照。只有内容相对上一份有变化时才写新目录，避免每次启动都堆一份。
    /// 返回新快照路径；未变化或失败返回 null。
    /// </summary>
    internal static string? CreateSnapshot(string reason)
    {
        try
        {
            var files = ConfigFiles().Where(f => File.Exists(Path.Combine(DshHome, f))).ToList();
            if (files.Count == 0) return null;

            // 逐个文件算内容哈希，而不是把全部内容拼起来算一个总哈希。
            // 实测教训：.credentials.yaml 每次启动都会被引擎重写（token 轮换），
            // 字节数常常分毫不差（17051 -> 17051）但内容确实变了。用总哈希判断，
            // 结果就是"每次启动都算配置有变化"，快照名额会被这种例行改写迅速耗尽，
            // 真正需要留住的配置快照反而被挤掉。
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in files)
            {
                try
                {
                    using var stream = File.OpenRead(Path.Combine(DshHome, rel));
                    hashes[rel] = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(stream))[..16];
                }
                catch { hashes[rel] = "<读取失败>"; }
            }

            Directory.CreateDirectory(BackupRoot);
            var manifestPath = Path.Combine(BackupRoot, "last-hashes.txt");

            // 只有"稳定文件"变了才值得新建快照。这类文件是真正的用户配置：
            // patch / settings / profile 清单，改动都出自主观操作。
            // 易变文件（凭据）单独排除在外——它的例行轮换不该消耗快照名额。
            var volatileFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".credentials.yaml" };

            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(manifestPath))
            {
                foreach (var line in File.ReadAllLines(manifestPath))
                {
                    var sep = line.IndexOf('\t');
                    if (sep > 0) previous[line[..sep]] = line[(sep + 1)..];
                }
            }

            var durableChanged = hashes.Any(kv =>
                !volatileFiles.Contains(kv.Key) &&
                (!previous.TryGetValue(kv.Key, out var old) || !string.Equals(old, kv.Value, StringComparison.Ordinal)));
            var setChanged = hashes.Count != previous.Count;

            if (previous.Count > 0 && !durableChanged && !setChanged)
                return null;   // 只有易变文件动过（或什么都没变），不占用快照名额

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(BackupRoot, stamp);
            Directory.CreateDirectory(target);

            foreach (var rel in files)
            {
                var src = Path.Combine(DshHome, rel);
                var dst = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                try { File.Copy(src, dst, overwrite: true); } catch { }
            }

            File.WriteAllText(Path.Combine(target, "backup-info.txt"),
                $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"原因: {reason}\n" +
                $"引擎: {EngineVersionAtBackup()}\n" +
                $"文件: {files.Count} 个\n" +
                "恢复方法：把这里的文件按相同相对路径覆盖回 %USERPROFILE%\\.dsh\\ " +
                "（覆盖前建议先关掉引擎）\n",
                new UTF8Encoding(false));

            File.WriteAllText(manifestPath,
                string.Join("\n", hashes.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                        .Select(kv => kv.Key + "\t" + kv.Value)),
                new UTF8Encoding(false));
            PruneOldSnapshots();
            return target;
        }
        catch { return null; }
    }

    private static string EngineVersionAtBackup()
    {
        try
        {
            var manifest = Path.Combine(LocalAppDir, "engine", "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(manifest)) return "未安装";
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?";
        }
        catch { return "?"; }
    }

    private static void PruneOldSnapshots()
    {
        try
        {
            var dirs = Directory.GetDirectories(BackupRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(d => d.Name.Length == 15 && d.Name[8] == '-')   // yyyyMMdd-HHmmss
                .OrderByDescending(d => d.Name)
                .ToList();
            foreach (var old in dirs.Skip(KeepSnapshots))
            {
                try { old.Delete(recursive: true); } catch { }
            }
        }
        catch { }
    }

    /// <summary>最新一份快照的路径，没有则 null。</summary>
    internal static string? LatestSnapshot()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return null;
            return Directory.GetDirectories(BackupRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(d => d.Name.Length == 15 && d.Name[8] == '-')
                .OrderByDescending(d => d.Name)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>
    /// 把一份快照覆盖回 $DSH_HOME。返回恢复的文件数；-1 表示失败。
    /// 只恢复快照里存在的文件，不动其他任何东西。
    /// </summary>
    internal static int Restore(string snapshotDir)
    {
        try
        {
            if (!Directory.Exists(snapshotDir)) return -1;
            var restored = 0;
            foreach (var src in Directory.GetFiles(snapshotDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(snapshotDir, src);
                if (rel.Equals("backup-info.txt", StringComparison.OrdinalIgnoreCase)) continue;
                var dst = Path.Combine(DshHome, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: true);
                    restored++;
                }
                catch { }
            }
            return restored;
        }
        catch { return -1; }
    }
}
