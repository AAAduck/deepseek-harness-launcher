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
    /// 凭据在快照里是 DPAPI 密文（见 <see cref="CredentialBackupName"/>），
    /// 但它们**跨凭据轮换留存**：轮换 token 是为了吊销旧凭据，而把旧密文再留 8 份
    /// 会削弱这个意图——密文虽解不开（只对当前用户），可它仍把"存在过一枚有效密钥"
    /// 这件事记录在盘上。8 份的窗口有限，但值得知情，README 也有对应说明。
    /// </summary>
    private const int KeepSnapshots = 8;

    /// <summary>撕裂快照目录（没有 backup-info.txt）的最长存活期，见 <see cref="PruneOldSnapshots"/>。</summary>
    private static readonly TimeSpan TornSnapshotMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// 快照的进程级互斥。三个调用点（启动前 / 升级前 / 恢复配置前的留底）各自包在
    /// Task.Run 里、彼此无互斥，而目录名撞车只对"前一个目录已存在"的时序防得住
    /// （<see cref="CreateSnapshotCore"/> 里的 for 序号岔开）：两个线程同一秒并发进入时，
    /// 双方都能在对方 CreateDirectory 之前通过 Directory.Exists 检查——两份快照写进
    /// 同一个目录，恢复方可能读到写一半的文件。函数体全程同步 IO，lock 就够；
    /// 写快照的只有本进程，跨会话场景由单实例互斥兜住。
    /// </summary>
    private static readonly object SnapshotGate = new();

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
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CredentialFile };

    /// <summary>
    /// 凭据文件名。提成常量是因为它现在出现在三个地方（备份清单、易变集、加密判定），
    /// 各自手写字符串迟早会漂移。
    /// </summary>
    internal const string CredentialFile = ".credentials.yaml";

    /// <summary>
    /// 快照里凭据那份的**落盘名**：原名 + <c>.dpapi</c>。
    ///
    /// 为什么要在快照里加密：.credentials.yaml 是明文密钥，而快照默认留 8 份、
    /// 每次配置变更都追加一份。这与本项目"web-url.txt 为什么要 DPAPI"的自身
    /// 威胁模型直接矛盾——同一个目录里，一个文件为了"别的用户读不出"专门加密，
    /// 另一个却把同样的密钥原样复制 8 份摊在磁盘上。更糟的是它**跨凭据轮换留存**：
    /// 用户轮换 token 正是为了吊销旧凭据，可 8 份历史快照把吊销意图原样抵消掉了。
    ///
    /// 改名的理由不是"防偷看"（加密才是），而是**可辨识**：一眼就能看出哪份是密文，
    /// 也保证 <see cref="Restore"/> 的通用拷贝分支绝不会把密文当明文盖回 $DSH_HOME。
    /// 读旧快照（明文同名）仍然支持，见 Restore 里的解密判定。
    /// </summary>
    internal const string CredentialBackupName = CredentialFile + ".dpapi";

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
        yield return CredentialFile;
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
        lock (SnapshotGate) return CreateSnapshotCore(reason, force);
    }

    private static string? CreateSnapshotCore(string reason, bool force)
    {
        try
        {
            // GetFolderPath 失败时文档化返回**空串**，Path.Combine 出来的是相对路径
            // （".dsh"、"DeepSeekHarness\config-backups"）——后续所有读写会按**当前
            // 工作目录**解析：快照写进不明位置，Restore 把文件恢复进不明位置还报
            // "成功"。这类机器上宁可不做、留痕（FoldersForm.Collect 对同一形状有
            // 同款护栏："宁可少列几项"）。
            if (!Path.IsPathFullyQualified(DshHome))
            {
                HarnessForm.AppendStartupLog("USERPROFILE 解析为空，跳过配置快照（避免按相对路径读写 .dsh）");
                return null;
            }
            if (!Path.IsPathFullyQualified(BackupRoot))
            {
                HarnessForm.AppendStartupLog("LOCALAPPDATA 解析为空，跳过配置快照（避免按相对路径写 config-backups）");
                return null;
            }

            var files = ConfigFiles().Where(f => File.Exists(Path.Combine(DshHome, f))).ToList();
            if (files.Count == 0) return null;

            // 逐个文件算内容哈希，而不是把全部内容拼起来算一个总哈希。
            // 实测教训：.credentials.yaml 每次启动都会被引擎重写（token 轮换），
            // 字节数常常分毫不差（17051 -> 17051）但内容确实变了。用总哈希判断，
            // 结果就是"每次启动都算配置有变化"，快照名额会被这种例行改写迅速耗尽，
            // 真正需要留住的配置快照反而被挤掉。
            //
            // ⚠ 截断到 16 个十六进制字符（64 bit）是**有意的**，且只能用于"变了没有"
            // 这个判断，绝不能当成完整性校验：
            //   • 用途极窄——只比"这一轮和上一轮是不是同一份"，不比"对不对"。
            //   • 64 bit 的碰撞概率在这台机器的全部快照轮次里可以忽略（生日界约
            //     50 亿次才有一半概率），而 SHA-256 全文会让清单大出 60 倍。
            //   • 万一碰撞，后果是"少拍一份快照"（NeedsSnapshot 判成没变），
            //     而绝不会把损坏的内容当成好的——恢复动作读的是盘上的真实文件。
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in files)
            {
                try
                {
                    using var stream = File.OpenRead(Path.Combine(DshHome, rel));
                    hashes[rel] = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(stream))[..16];
                }
                catch { /* 读取失败：hashes 不含该项，下面复制也会跳过它 */ }
            }

            // 读取失败的文件**不能**往 hashes 里塞占位符再照常落 manifest：
            // 占位符会被当成"该文件的哈希"写进 last-hashes.txt，此后每次启动真实哈希
            // 都与它不等 → NeedsSnapshot 恒为 true → 每轮重拍一份，8 个名额被无意义
            // 快照轮流挤掉。改成"读取失败即跳过复制"：该文件本轮不在 hashes 里，
            // manifest 也只写确实复制成功的项（见下面 copied 的记账），于是下一轮
            // NeedsSnapshot 按"键缺席"判为要拍，自动重试——与复制失败的语义完全一致。

            Directory.CreateDirectory(BackupRoot);
            var manifestPath = Path.Combine(BackupRoot, "last-hashes.txt");

            // 只有"稳定文件"变了才值得新建快照。这类文件是真正的用户配置：
            // patch / settings / profile 清单，改动都出自主观操作。
            // 易变文件（凭据）单独排除在外——它的例行轮换不该消耗快照名额。
            // 清单取自共享成员，测试引用的也是它，避免"生产改了、测试没红"的静默漂移。

            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(manifestPath))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(manifestPath);
                }
                catch (Exception ex)
                {
                    // 读不出清单 ≠ 清单为空，但**效果相同**：NeedsSnapshot 的
                    // "上一份清单为空 → 要拍"这条规则会把两种情况都导向"重新拍一份"，
                    // 那正是此刻该做的事。此前是静默跳过——读失败与"首次运行"走同一条路，
                    // 却连一条日志都没有，用户永远不知道自己那份去重基准已经失灵。
                    Swallow.Quiet(ex, "config-manifest-read");
                    lines = Array.Empty<string>();
                }
                foreach (var line in lines)
                {
                    var sep = line.IndexOf('\t');
                    // 半截行（写入中途被杀，见下面 manifest 的原子写）没有制表符，
                    // 此前被静默丢弃——而丢掉的键在 NeedsSnapshot 眼里是"缺席"，
                    // 于是触发一次多余的快照，8 个名额被无意义快照挤掉。
                    // 现在显式留痕：静默丢键比快照多拍难查得多。
                    if (sep <= 0)
                    {
                        HarnessForm.AppendStartupLog($"last-hashes.txt 有无法解析的行（缺少制表符），已忽略：{line}");
                        continue;
                    }
                    previous[line[..sep]] = line[(sep + 1)..];
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
                // 哈希没算出来的文件（上面读取失败）直接跳过：既不复制也不进 manifest，
                // 下一轮按"键缺席"自然重试。hashes[rel] 不能再用索引器硬取——取不到会
                // 抛 KeyNotFoundException，被外层 catch 吞成"整份快照失败"。
                if (!hashes.TryGetValue(rel, out var hash)) continue;
                var src = Path.Combine(DshHome, rel);
                // 凭据单独走 DPAPI：快照里不能留明文密钥（见 CredentialBackupName 的注释）。
                // 加密失败就**跳过这个文件**而不是退化成明文——少备份一份凭据，
                // 远好过在盘上再摊一份明文；跳过项自然不写进 manifest，下轮会重试。
                var isCredential = rel.Equals(CredentialFile, StringComparison.OrdinalIgnoreCase);
                var dst = Path.Combine(target, isCredential ? CredentialBackupName : rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                try
                {
                    if (isCredential)
                    {
                        var plain = File.ReadAllText(src);
                        if (plain.Trim().Length == 0)
                        {
                            // 空/全空白凭据不值得备份，但**必须从 hashes 里同时摘掉**：
                            // 留着它，manifest 就永远缺这个键（copied 不含空文件），下一轮
                            // NeedsSnapshot 按"键缺席"恒判要拍——每次启动都重拍一份、
                            // 8 个名额被无差别轮换，而 failedCopies 里一个字都没有，
                            // 谁也看不出为什么（本类的设计标准是失败"可见"而非静默）。
                            // 摘掉之后两张表都不含它，判等自然通过；下轮凭据非空时键重新
                            // 出现 → 按规则②老老实实拍一份。
                            hashes.Remove(rel);
                            continue;
                        }
                        CopyCredentialIntoSnapshot(plain, dst);
                    }
                    else
                    {
                        File.Copy(src, dst, overwrite: true);
                    }
                    copied[rel] = hash;
                }
                catch (Exception ex)
                {
                    failedCopies.Add($"{rel}（{ex.GetType().Name}: {ex.Message}）");
                }
            }

            // 一个文件都没复制成功时，**不能**返回非 null。
            // 调用方（"恢复配置"前的留底）把"返回值非 null"当成"留底成功"，
            // 返回一个空目录会让它以为"当前状态已另存"，于是放心覆盖——
            // 而那一刻其实什么也没留下，这正是"留底失败即中止"要挡的那一步。
            // 空目录本身也一并删掉：LatestSnapshot 只认有 backup-info.txt 的目录，
            // 留着它只是让用户在自己的备份目录里看见一堆没有内容的文件夹。
            if (copied.Count == 0)
            {
                HarnessForm.AppendStartupLog(
                    $"配置快照一个文件都没复制成功（{failedCopies.Count} 项失败），已放弃并清理空目录");
                try { Directory.Delete(target, recursive: true); } catch { }
                return null;
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
                $"注意：快照内的 {CredentialBackupName} 是 DPAPI 加密的凭据" +
                "（只有当前 Windows 用户能解开；用「恢复配置」按钮会自动解密还原，\n" +
                "  手工覆盖回 .credentials.yaml 前需先自行解密）。\n" +
                "整个 config-backups 目录请勿外传、勿贴进截图。\n");
            File.WriteAllText(Path.Combine(target, "backup-info.txt"),
                info.ToString(),
                new UTF8Encoding(false));

            // 清单**必须**原子写：它是整个去重机制的唯一依据，却是原地覆写
            // （File.WriteAllText）。写到一半被杀 → 半截内容 → 读侧把没有制表符的行
            // 静默丢弃 → 那些键"缺席" → NeedsSnapshot 每轮都判要拍，
            // 8 个快照名额被无意义的快照轮流挤掉，而用户真正需要的那份反而没了。
            // 同卷 Move 在 NTFS 上是原子的，与快照内容、Restore 走同一条纪律。
            AtomicWrite(manifestPath,
                Encoding.UTF8.GetBytes(string.Join("\n",
                    copied.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                           .Select(kv => kv.Key + "\t" + kv.Value))));
            PruneOldSnapshots();
            return target;
        }
        catch (Exception ex) { Swallow.Quiet(ex, "config-snapshot"); return null; }
    }

    /// <summary>
    /// 把凭据内容写进快照落点。唯一的死规矩：**落进快照的那份必须是密文**，否则抛
    /// （异常由逐文件 catch 收进 failedCopies，宁可少备份一份凭据，也不在盘上摊明文）。
    ///
    /// 独立成 internal 是为了可单测：判定错了不报错，而"加密校验条件写反"正是这里
    /// 出过的事故——此前写成与**源文件**状态比较（alreadyEncrypted != dst 是否密文），
    /// 明文源 + DPAPI 正常时必然抛假异常（凭据从此进不了 manifest、快照每轮重拍），
    /// DPAPI 真失效时反而放行明文入库，恰好把该挡的放走、把该过的拦下。
    /// 无条件验"dst 是密文"，明文降级与写入失败两种情形一并挡住。
    ///
    /// 源文件本身可能已是 dpapi 格式（用户手工放进去的旧版状态）：WriteAllText 会把它
    /// 再包一层密文，恢复时解开一层得到的正是原始 dpapi 串，写回 .credentials.yaml
    /// 仍是合法密文——往返无损，不需要特判。
    /// </summary>
    internal static void CopyCredentialIntoSnapshot(string plainText, string dst)
    {
        DpapiFile.WriteAllText(dst, plainText);
        if (!DpapiFile.IsEncryptedFile(dst))
            throw new InvalidOperationException("凭据快照加密失败（DPAPI 不可用），本次未备份该文件");
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

    /// <summary>
    /// 快照目录按"新的在前"排序。
    ///
    /// 主键是目录的<b>创建时间</b>，目录名只作同刻的次序兜底。原先只按名字排，
    /// 而名字里的时间戳是<b>本地时钟</b>：NTP 回拨、夏令时跳变、手动改时间都会让
    /// 排序与真实先后脱钩。后果是具体的两条——
    ///   • <see cref="LatestSnapshot"/> 可能挑中一份比别的更旧的快照去恢复；
    ///   • <see cref="PruneOldSnapshots"/> 按名字倒序保留前 N 份，于是可能删掉
    ///     刚拍的那份、把三天前的留下。用户看到的"最新备份"根本不是最新。
    /// 名字排序在这里不是"更简单"，只是"更常见地正确"。
    /// </summary>
    private static IOrderedEnumerable<DirectoryInfo> OrderByNewestFirst(this IEnumerable<DirectoryInfo> dirs) =>
        dirs.OrderByDescending(d => { try { return d.CreationTimeUtc; } catch { return DateTime.MinValue; } })
            .ThenByDescending(d => d.Name, StringComparer.Ordinal);

    private static void PruneOldSnapshots()
    {
        try
        {
            // 目录名 = yyyyMMdd-HHmmss（15 字符）；撞名时带 "-2" 之类后缀，同样纳入清理。
            var dirs = Directory.GetDirectories(BackupRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(d => d.Name.Length >= 15 && d.Name[8] == '-')
                .OrderByNewestFirst()
                .ToList();
            // 保留名额只留给**完整**的快照（有 backup-info.txt）。LatestSnapshot 把
            // 撕裂目录排除在可恢复集之外，这里若仍按名字形状计入名额，快照中途被强杀、
            // backup-info 写失败留下的目录就会一次次挤掉最旧的**完整**快照——名义上
            // "保留最近 8 份"，实际混着几份根本恢复不了的空壳。
            var complete = new List<DirectoryInfo>(dirs.Count);
            var torn = new List<DirectoryInfo>();
            foreach (var d in dirs)
            {
                try
                {
                    if (File.Exists(Path.Combine(d.FullName, "backup-info.txt"))) complete.Add(d);
                    else torn.Add(d);
                }
                catch { torn.Add(d); }
            }
            foreach (var old in complete.Skip(KeepSnapshots))
            {
                try { old.Delete(recursive: true); }
                catch (Exception ex)
                {
                    // 内层也要留痕：删除失败（被资源管理器/备份工具占着、权限被撤）意味着
                    // 含明文凭据的快照会无信号地累积——README「低频路径异常统一日志」的
                    // 纪律不能只落到外层。Swallow 的每小时节流天然防刷屏。
                    Swallow.Quiet(ex, "prune-snapshot");
                }
            }
            // 撕裂目录不占名额，但也不能永久累积：满 TornSnapshotMaxAge 就清。
            // 它们没有任何可恢复的内容（backup-info 都没写出来），留着只是让
            // config-backups 越长越乱。
            var cutoff = DateTime.UtcNow - TornSnapshotMaxAge;
            foreach (var old in torn)
            {
                try
                {
                    if (old.CreationTimeUtc >= cutoff) continue;
                    old.Delete(recursive: true);
                }
                catch (Exception ex) { Swallow.Quiet(ex, "prune-snapshot-torn"); }
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
        // 已知边界：候选恰为 "root + 一个尾分隔符"（"…\.dsh\"）时会在这一步被判成
        // 界内——它语义上就是 root 本身，Restore 只传 GetFiles 的**文件**路径
        // （相对段恒非空），该形状实际不可达，不值得为它收紧判定。
        var next = candidate[root.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// <paramref name="candidateFullPath"/> 到 <paramref name="rootFullPath"/> 的**每一段**
    /// 上都没有重解析点（junction / 符号链接 / 目录联接）吗？
    ///
    /// 这是 <see cref="IsWithinRoot"/> 看不见的那一半：那条防线只做词法比较，
    /// 而 <c>Path.GetFullPath</c> **不解析重解析点**。于是
    /// <c>C:\Users\me\.dsh\profiles\web</c> 在 <c>profiles</c> 是个指向
    /// <c>D:\elsewhere</c> 的 junction 时，路径字符串完全落在 root 之内、
    /// 防线放行，而实际写入落在 root 之外——"恢复动作唯一的纵深防线"形同虚设。
    /// 源侧同理：快照里塞一个指向外部的链接文件，枚举出的 src 字符串"合法"，
    /// <c>File.ReadAllBytes</c> 读到的却是别处的内容。
    ///
    /// <b>边界</b>：只查 root <b>之下</b>的段，不查 root 自己。把 <c>$DSH_HOME</c>
    /// 整个放到别的盘（重定向到 OneDrive / 另一块盘）是用户自己的正当选择，
    /// 那道 junction 正是他要的，不该被这里判成不安全。root 之下则一律不放行。
    ///
    /// 纯函数内核（可单测）：IO 通过 <paramref name="attributesOf"/> 注入。
    /// 读不到属性一律判 false（不放行）——判不出"安全"时就当不安全。
    /// </summary>
    internal static bool IsSafeRestoreTarget(
        string candidateFullPath, string rootFullPath, Func<string, FileAttributes> attributesOf)
    {
        if (string.IsNullOrEmpty(candidateFullPath) || string.IsNullOrEmpty(rootFullPath)) return false;
        if (attributesOf is null) return false;

        string root, current;
        try
        {
            if (!Path.IsPathFullyQualified(rootFullPath)) return false;
            root = Path.GetFullPath(rootFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            current = Path.GetFullPath(candidateFullPath);
            if (root.Length == 0) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;   // 形状都不对，谈不上安不安全
        }

        // 必须整体在 root 之内（词法那一半，与 IsWithinRoot 同判据）。
        if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
            !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        // 从候选路径逐级往上走到 root 为止，每一段都要不是重解析点。
        while (current.Length > root.Length)
        {
            FileAttributes attr;
            try { attr = attributesOf(current); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // **该段还不存在**——这恰恰是首次恢复时目的地的常态。
                // "不存在"不可能是重解析点（那样 File.Exists/Directory.Exists 会真），
                // 所以把它当成普通目录继续往上走。
                // 不这么处理的话，RestoreInto 会对每一个**新写入**的文件返回 false：
                // 目标文件此刻还不存在，GetAttributes 抛 FileNotFoundException，
                // 于是整条恢复路径上一件都恢复不了（第一版就踩了这个，
                // 由 ConfigBackupRestoreTests 的端到端用例抓出来）。
                attr = FileAttributes.Normal;
            }
            catch { return false; }        // 读不到属性 = 判不出来 = 不放行
            if ((attr & FileAttributes.ReparsePoint) != 0) return false;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length) return false;
            current = parent;
        }
        return string.Equals(current, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><see cref="IsSafeRestoreTarget"/> 的 IO 包装：真的去读文件属性。</summary>
    internal static bool IsSafeRestoreTarget(string candidateFullPath, string rootFullPath) =>
        IsSafeRestoreTarget(candidateFullPath, rootFullPath, File.GetAttributes);

    /// <summary>
    /// 原子覆盖写：先写同目录临时文件，再同卷 Move 覆盖。
    /// <c>File.Copy(overwrite)</c> / <c>File.WriteAllText</c> 都是原地覆写——进程被杀或
    /// 断电落在写入中途，.credentials.yaml / settings.yaml 就停在部分写入状态，
    /// 而这正是备份机制本来要防的损坏。同卷 Move 在 NTFS 上是原子的：任何时刻目标
    /// 要么是旧的完整内容，要么是新的完整内容。临时文件失败时顺带清掉，不留在配置目录里。
    /// </summary>
    private static void AtomicWrite(string full, byte[] contents)
    {
        // 临时文件必须是**每次唯一**的：固定名 "x.tmp" 在两个写入者同时落到同一路径时
        // 会互相覆盖、再互相 Move，最后一次成功把另一次的内容变成孤儿或半份。
        // CreateSnapshot 并没有全局互斥（"启动前"与"升级引擎前"两条路径都调它，
        // 还各自包在 Task.Run 里），所以这是真实可能的并发。
        var tmp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, contents);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 最新一份**完整**快照的路径，没有则 null。
    ///
    /// "完整"= 有 <c>backup-info.txt</c>。它在 <see cref="CreateSnapshot"/> 里是
    /// **所有文件都复制完之后**才写的，因此它的存在等价于"内容已全部落盘"。
    /// 少了这道筛选，一次在复制途中被强杀/断电的**撕裂快照**（目录在、文件半份）
    /// 会成为 LatestSnapshot —— 而用户点「恢复配置」拿到的就是它。
    /// 撕裂目录此刻还没有 backup-info.txt，跳过它之后 LatestSnapshot 自然落到
    /// 上一份完整的；下轮 NeedsSnapshot 因 manifest 未更新还会重拍一次。
    /// </summary>
    internal static string? LatestSnapshot()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return null;
            return Directory.GetDirectories(BackupRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(d => d.Name.Length >= 15 && d.Name[8] == '-')
                .Where(d => File.Exists(Path.Combine(d.FullName, "backup-info.txt")))
                .OrderByNewestFirst()
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
    internal static RestoreResult Restore(string snapshotDir) => RestoreInto(snapshotDir, DshHome);

    /// <summary>
    /// <see cref="Restore"/> 的可测内核：目标根由参数给出。
    ///
    /// 为什么要拆出这个重载：Restore 是**全套件唯一一条"覆盖写用户配置"的路径**，
    /// 数据安全权重最高，却因为硬编码 <see cref="DshHome"/> 而无法用临时目录测——
    /// 真写测试就等于把开发者自己的 ~/.dsh 覆盖一遍。拆开之后，
    /// GetRelativePath → Combine → 越界守卫 → 链接守卫 → tmp+Move → 失败记账 →
    /// 跳过 backup-info.txt / .tmp 这整条链都能在临时目录里钉住，
    /// 而生产入口的语义一字未变（Restore 就是 RestoreInto(snapshotDir, DshHome)）。
    /// </summary>
    internal static RestoreResult RestoreInto(string snapshotDir, string dshHome)
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
            // USERPROFILE 解析为空时 DshHome 是相对路径 ".dsh"——GetFullPath 会把它
            // 落到当前工作目录，恢复"成功"却写错了位置、界面还报成功数。必须在这里
            // 挡住，并把原因如实报给用户（与 CreateSnapshot 的同形状护栏成对）。
            if (!Path.IsPathFullyQualified(dshHome))
            {
                failed.Add("USERPROFILE 解析为空，无法定位 .dsh；未做任何改动。");
                return new RestoreResult(0, failed);
            }
            var root = Path.GetFullPath(dshHome);
            var snapshotRoot = Path.GetFullPath(snapshotDir);
            foreach (var src in Directory.GetFiles(snapshotDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(snapshotDir, src);
                if (rel.Equals("backup-info.txt", StringComparison.OrdinalIgnoreCase)) continue;
                // 跳过写入中断留下的 .tmp 残骸。它们不是配置，是某次 AtomicWrite /
                // File.Copy 写到一半被杀的产物：盖进 $DSH_HOME 只会多出一份垃圾文件，
                // 而 .credentials.yaml.tmp 这类名字还会让某些读取方按前缀匹配到它。
                if (rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                // 快照里的凭据叫 <see cref="CredentialBackupName"/>，落回 $DSH_HOME 时
                // 必须去掉那个后缀——否则引擎会拿一份没人读的文件当凭据，
                // 表现为"恢复了却登不上"。
                var dst = rel.Equals(CredentialBackupName, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dshHome, CredentialFile)
                    : Path.Combine(dshHome, rel);

                // 纵深防御，两道：
                // ① <see cref="IsWithinRoot"/>：路径必须在 $DSH_HOME 内。
                //    快照目录由本程序生成、正常不会越界，但一旦目录被外部改动过，
                //    一个 "../" 就可能写到别处去。**它只是词法比较**——
                //    GetFullPath 只做规范化，不解析重解析点。
                // ② <see cref="IsSafeRestoreTarget"/>：沿途不得有 junction / 符号链接。
                //    ①②都过、而 `profiles\web` 本身是个指向别处的 junction 时，
                //    "路径字符串落在 $DSH_HOME 内"完全成立，**实际写入却在根外**——
                //    这正是纯字符串防线看不见的那一类。源侧同样要查：快照里放一个
                //    指向外部的链接文件，枚举出的 src 字符串"合法"，读到的却是别处的内容。
                var full = Path.GetFullPath(dst);
                if (!IsWithinRoot(full, root))
                {
                    failed.Add($"{rel}（越出 $DSH_HOME，已跳过）");
                    continue;
                }
                if (!IsSafeRestoreTarget(src, snapshotRoot) || !IsSafeRestoreTarget(full, root))
                {
                    failed.Add($"{rel}（路径上有链接/联接点，无法确认写入 $DSH_HOME 之内，已跳过）");
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (rel.Equals(CredentialBackupName, StringComparison.OrdinalIgnoreCase))
                    {
                        // 凭据快照是密文，必须解开再写回——.credentials.yaml 只能是明文。
                        // 解不开（换了用户/换了机器/DPAPI 主密钥被清）就**跳过并记账**：
                        // 静默盖一份解不开的垃圾进去，引擎下次启动会一直认证失败，
                        // 而用户看到的却是"已恢复 N 个文件"。
                        var plain = DpapiFile.ReadAllText(src);
                        if (plain is null)
                        {
                            failed.Add($"{rel}（无法解密：快照由另一个 Windows 用户或另一台机器创建）");
                            continue;
                        }
                        AtomicWrite(full, Encoding.UTF8.GetBytes(plain));
                    }
                    else
                    {
                        AtomicWrite(full, File.ReadAllBytes(src));
                    }
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
