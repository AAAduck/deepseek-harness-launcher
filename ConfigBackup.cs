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
    /// <summary>
    /// 保留的最近快照数。每个快照只有几十 KB，留几份成本极低。
    /// 注意其中含 .credentials.yaml 的明文副本——backup-info.txt 里已写明"勿外传"。
    /// </summary>
    private const int KeepSnapshots = 8;

    /// <summary>
    /// 会被引擎例行改写、不该消耗快照名额的文件。
    ///
    /// 提成成员而不是留在 <see cref="CreateSnapshot"/> 的方法体里，是因为单测必须能
    /// 引用**同一份**清单。此前测试只能手抄一份副本，于是"生产把某个文件加进/移出
    /// 易变集"这类改动会让测试全绿通过——恰好是本类最该防的那种静默漂移。
    /// 注意别把成员取名成 <c>Volatile</c>：<c>System.Threading</c> 在隐式 using 里，
    /// 同名会把本类内的 <c>Volatile.Read(...)</c> 变成 CS0119。
    /// </summary>
    internal static readonly ISet<string> VolatileConfigFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".credentials.yaml" };

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
    /// <param name="reason">写进 backup-info.txt 的原因。</param>
    /// <param name="force">跳过"内容有变化才拍"的去重判定，无条件拍一份。
    /// 恢复配置前的留底必须用它：回滚快照的价值恰恰在"覆盖前"这一刻，
    /// 若此刻只有凭据轮换过（易变豁免判成"不用拍"）就不留底，
    /// 用户最新一次登录拿到的 token 会被旧快照覆盖——UI 承诺了
    /// "覆盖前我会先把当前状态另存一份"，这条路径不能例外。</param>
    internal static string? CreateSnapshot(string reason, bool force = false)
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
            // 清单取自共享成员，测试引用的也是它，避免"生产改了、测试没红"的静默漂移。

            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(manifestPath))
            {
                foreach (var line in File.ReadAllLines(manifestPath))
                {
                    var sep = line.IndexOf('\t');
                    if (sep > 0) previous[line[..sep]] = line[(sep + 1)..];
                }
            }

            if (!force && !NeedsSnapshot(hashes, previous, VolatileConfigFiles))
                return null;   // 只有易变文件动过（或什么都没变），不占用快照名额

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(BackupRoot, stamp);
            // 同一秒内两次快照会撞目录名（两次快速点「重启」就可能），加序号岔开。
            for (var n = 2; Directory.Exists(target); n++)
                target = Path.Combine(BackupRoot, $"{stamp}-{n}");
            Directory.CreateDirectory(target);

            // 逐文件记复制成败。此前失败只是静默跳过，manifest 却仍按完整 hashes
            // 落盘——快照目录里没有这个文件，清单却说"已备份"；只要源文件内容
            // 不再变，NeedsSnapshot 永远判"不用拍"，这个文件就再也不会被重试备份。
            // 现在 manifest 只写**确实复制成功**的文件：失败项在清单里缺席，
            // 下一轮 NeedsSnapshot 按"键集合变化"判为要拍，自动重试。
            // （若某文件永久复制失败——权限被撤/磁盘满——每轮都会重拍一份，
            // 但 backup-info 每份都列着失败清单，是可见信号而非静默缺口。）
            var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failedCopies = new List<string>();
            foreach (var rel in files)
            {
                var src = Path.Combine(DshHome, rel);
                var dst = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                try
                {
                    File.Copy(src, dst, overwrite: true);
                    copied[rel] = hashes[rel];
                }
                catch (Exception ex)
                {
                    failedCopies.Add($"{rel}（{ex.GetType().Name}: {ex.Message}）");
                }
            }

            var info = new StringBuilder();
            info.Append($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"原因: {reason}\n" +
                $"引擎: {EngineVersionAtBackup()}\n" +
                $"文件: {copied.Count} 个\n");
            if (failedCopies.Count > 0)
            {
                info.Append("未备份（复制失败，下次启动会自动重试）:\n");
                foreach (var f in failedCopies) info.Append($"  · {f}\n");
            }
            info.Append(
                "恢复方法：把这里的文件按相同相对路径覆盖回 %USERPROFILE%\\.dsh\\ " +
                "（覆盖前建议先关掉引擎）\n" +
                "注意：快照内含 .credentials.yaml（明文密钥，且随快照保留多份），" +
                "整个 config-backups 目录请勿外传、勿贴进截图。\n");
            File.WriteAllText(Path.Combine(target, "backup-info.txt"),
                info.ToString(),
                new UTF8Encoding(false));

            File.WriteAllText(manifestPath,
                string.Join("\n", copied.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                        .Select(kv => kv.Key + "\t" + kv.Value)),
                new UTF8Encoding(false));
            PruneOldSnapshots();
            return target;
        }
        catch (Exception ex) { Swallow.Quiet(ex, "config-snapshot"); return null; }
    }

    /// <summary>
    /// 这一轮要不要新拍一份快照。**纯函数**（只吃两张哈希表，不碰文件系统），
    /// 因为它是整个备份机制里唯一一个"判错了不报错"的决策：判成"不用拍"，
    /// 用户真正需要还原的那一刻才会发现快照根本没留下。
    ///
    /// 规则：
    /// ① 稳定文件（不含易变文件）内容变了 → 要拍。凭据轮换这类例行改写不算。
    /// ② 键集合有任何增删 → 要拍。用**双向集合对比**而不是只比数量：
    ///    "一增一减、数量恰好不变"（换 profile、改 patch 文件名）曾被漏掉。
    /// ③ 上一份清单为空（首次运行）→ 要拍。
    /// </summary>
    internal static bool NeedsSnapshot(
        IReadOnlyDictionary<string, string> hashes,
        IReadOnlyDictionary<string, string> previous,
        ISet<string> volatileFiles)
    {
        if (previous.Count == 0) return true;

        var durableChanged = hashes.Any(kv =>
            !volatileFiles.Contains(kv.Key) &&
            (!previous.TryGetValue(kv.Key, out var old) || !string.Equals(old, kv.Value, StringComparison.Ordinal)));

        var setChanged = hashes.Keys.Except(previous.Keys, StringComparer.OrdinalIgnoreCase).Any() ||
                         previous.Keys.Except(hashes.Keys, StringComparer.OrdinalIgnoreCase).Any();

        return durableChanged || setChanged;
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
            // 目录名 = yyyyMMdd-HHmmss（15 字符）；撞名时带 "-2" 之类后缀，同样纳入清理。
            var dirs = Directory.GetDirectories(BackupRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(d => d.Name.Length >= 15 && d.Name[8] == '-')
                .OrderByDescending(d => d.Name)
                .ToList();
            foreach (var old in dirs.Skip(KeepSnapshots))
            {
                try { old.Delete(recursive: true); } catch { }
            }
        }
        catch (Exception ex) { Swallow.Quiet(ex, "prune-snapshots"); }
    }

    /// <summary>
    /// 目标路径是否真的落在 <paramref name="rootFullPath"/> 目录**之内**。纯函数、可单测——
    /// 它是恢复动作唯一的纵深防御：判错了不会报错，只会把用户的配置写到别处去。
    /// 三条边界都要成立：两边的 <c>..</c> 都被规范化掉、前缀相同、且前缀之后紧跟一个
    /// 分隔符（否则 <c>C:\a\bc</c> 会被 <c>C:\a\b</c> 误判成在内部）。
    /// root 自己不算"内部"（长度相等，在第二条被挡掉）；root 必须写成完整目录路径。
    /// </summary>
    internal static bool IsWithinRoot(string candidateFullPath, string rootFullPath)
    {
        if (string.IsNullOrEmpty(candidateFullPath) || string.IsNullOrEmpty(rootFullPath)) return false;

        // 规范化必须在这里做，不能指望调用方。原实现只比"前缀 + 第 N 位是分隔符"，
        // 对 `C:\Users\me\.dsh\..\evil\x` 这类未展开的 `..` 会判成 true——
        // 而本文件 Restore 处的注释点名的正是"一个 `../` 就可能写到别处去"这个场景。
        // 当时之所以没出事，纯粹因为 Restore 恰好在调用前做了 Path.GetFullPath；
        // 一旦这个守卫被复用到没规范化的路径上（将来支持从压缩包恢复之类），
        // 它就会静默放行。纵深防御不能靠调用点的自觉。
        string root, candidate;
        try
        {
            // root 必须是完整目录路径。`C:`（盘符相对）经 GetFullPath 会变成
            // "盘符 + 当前盘目录"，结论跟着进程工作目录变；相对路径同理（按 CWD 展开）。
            // 守卫不该接受这类输入。
            if (!Path.IsPathFullyQualified(rootFullPath)) return false;
            root = Path.GetFullPath(rootFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            candidate = Path.GetFullPath(candidateFullPath);
            // TrimEnd 把 root 削空只可能发生在 root 写成 `\` 或 `\\` 的时候。
            // 此时**绝不能**拿候选路径自己的盘符根去补：那等于把"必须在这个根里"
            // 变成"必须在任意盘里"，检查直接作废。宁可拒——这形状本来就传错了。
            if (root.Length == 0) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;   // 非法字符 / 非法语法：谈不上"在不在 root 之内"
        }

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        if (candidate.Length <= root.Length) return false;
        // 注意：这里必须比 Path.DirectorySeparatorChar，不能用 char.IsSeparator——
        // 后者判的是 Unicode「分隔符」类别（空格类），对 '\' 返回 false，
        // 会让这道守卫把**所有**路径都判成越界（恢复动作全量跳过，且不报错）。
        var next = candidate[root.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }

    /// <summary>最新一份快照的路径，没有则 null。</summary>
    internal static string? LatestSnapshot()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return null;
            return Directory.GetDirectories(BackupRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(d => d.Name.Length >= 15 && d.Name[8] == '-')
                .OrderByDescending(d => d.Name)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>
    /// 一次恢复的结果：成功写了几个文件，以及**每一个没成的**。
    /// 必须把失败清单带出来——只报成功数的话，"引擎占着配置"这种最常见的失败
    /// 会被显示成"已恢复 N 个文件"，而 $DSH_HOME 实际停在新旧混合态，
    /// 比整体失败更难排查。
    /// </summary>
    internal readonly record struct RestoreResult(int Restored, List<string> Failed)
    {
        public bool AnyFailed => Failed.Count > 0;
    }

    /// <summary>
    /// 把一份快照覆盖回 $DSH_HOME。只恢复快照里存在的文件，不动其他任何东西。
    /// 逐文件失败照旧继续（一个文件写不进去不该让其余的都不恢复），但**必须记账**。
    /// </summary>
    internal static RestoreResult Restore(string snapshotDir)
    {
        var failed = new List<string>();
        var restored = 0;
        try
        {
            if (!Directory.Exists(snapshotDir))
            {
                failed.Add("快照目录不存在：" + snapshotDir);
                return new RestoreResult(0, failed);
            }
            var root = Path.GetFullPath(DshHome);
            foreach (var src in Directory.GetFiles(snapshotDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(snapshotDir, src);
                if (rel.Equals("backup-info.txt", StringComparison.OrdinalIgnoreCase)) continue;
                var dst = Path.Combine(DshHome, rel);
                // 纵深防御：恢复是往用户配置目录**覆盖写入**，路径必须仍在 $DSH_HOME 内。
                // 快照目录由本程序生成、正常不会越界，但一旦目录被外部改动过，
                // 一个 "../" 就可能写到别处去。
                var full = Path.GetFullPath(dst);
                if (!IsWithinRoot(full, root))
                {
                    failed.Add($"{rel}（越出 $DSH_HOME，已跳过）");
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.Copy(src, full, overwrite: true);
                    restored++;
                }
                catch (Exception ex)
                {
                    // 最常见的原因是引擎还开着、正占着这些文件。留一条日志，
                    // 更重要的是把相对路径记进清单交给用户看。
                    Swallow.Quiet(ex, "config-restore-file");
                    failed.Add($"{rel}（{ex.GetType().Name}）");
                }
            }
            return new RestoreResult(restored, failed);
        }
        catch (Exception ex)
        {
            Swallow.Quiet(ex, "config-restore");
            failed.Add("读取快照目录失败：" + ex.Message);
            return new RestoreResult(0, failed);
        }
    }
}
