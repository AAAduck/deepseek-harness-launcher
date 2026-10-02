// ── HarnessForm 的「插件兼容性检查与 semver 判定」部分 ──────────────────────────────────────────
// 由 HarnessForm.cs 按本文件原有的「// ---- 分段 ----」分隔线机械切分而来。
// 切分只搬位置、不改任何一行成员代码；partial class 之间共享全部字段与成员。
// 各段清单见 README「文件说明」。改动请落在语义所属的那一段里。

using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing.Drawing2D;
using System.Windows.Forms.Automation;

namespace DeepSeekHarness;

internal sealed partial class HarnessForm : Form
{

    // ---- 引擎升级前的插件兼容性检查（判定内核在 Semver）------------------
    //
    // 背景（实测本机）：profile 只声明 6 个插件依赖，@deepseek-ai/* 一个都不声明，
    // 全部由引擎提供。所以升级引擎不会动插件文件，但插件声明的 peer 要求
    // 是针对特定引擎版本写的——例如皮肤插件要求 @deepseek-ai/dsh >=0.1.7-rc.1，
    // 而当时引擎是 0.1.5-rc.2，属于"能跑但插件不被满足"的状态。
    // 升级前把这类不满足列出来，用户才知道点下去会不会让插件失效。

    /// <summary>某插件对某个 DSH 包的版本要求；没有声明则为 null。</summary>
    private sealed record PluginRequirement(string Plugin, string Package, string Range);

    private static List<PluginRequirement> CollectPluginRequirements(string profileDir)
    {
        var found = new List<PluginRequirement>();
        var modules = Path.Combine(profileDir, "node_modules");
        var scopes = new[] { "@deepseek-ai", "@dsh-external", "" };
        foreach (var scope in scopes)
        {
            var dir = scope.Length == 0 ? modules : Path.Combine(modules, scope);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> entries;
            try
            {
                entries = scope.Length == 0
                    ? Directory.GetDirectories(dir).Where(d => Path.GetFileName(d).StartsWith("dsh-", StringComparison.OrdinalIgnoreCase))
                    : Directory.GetDirectories(dir);
            }
            catch { continue; }

            foreach (var entry in entries)
            {
                var manifest = Path.Combine(entry, "package.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (!doc.RootElement.TryGetProperty("peerDependencies", out var peers)) continue;
                    var fallbackName = Path.GetFileName(entry);
                    foreach (var peer in peers.EnumerateObject())
                    {
                        // 只看"由引擎提供"的那批包：@deepseek-ai/* 以及 React 这类外部 peer 不算。
                        if (!peer.Name.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (peer.Value.ValueKind != JsonValueKind.String) continue;
                        var range = peer.Value.GetString();
                        if (string.IsNullOrWhiteSpace(range)) continue;
                        var pluginName = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? fallbackName
                            : fallbackName;
                        found.Add(new PluginRequirement(pluginName, peer.Name, range));
                    }
                }
                catch { }
            }
        }
        return found;
    }

    // ParseVersion / VersionPrerelease / CompareVersionStrings / SatisfiesRange /
    // SatisfiesSingle / PrereleaseAllowedInRange / PrereleaseAdmittedByComparatorSet /
    // NoteComparatorBasis / IsDshVersionedPackage 已迁到 Semver（纯函数内核，
    // 见该类头部说明）。

    /// <summary>
    /// profile 里某个 @deepseek-ai/* 包实际解析到的版本（含 pnpm 的 .pnpm 存放区）。
    /// 找不到返回 null。
    /// </summary>
    private static string? ResolveInstalledPackageVersion(string profileDir, string packageName)
    {
        try
        {
            var modules = Path.Combine(profileDir, "node_modules");
            // ① 直接可见的位置
            var direct = Path.Combine(modules, packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");
            if (File.Exists(direct))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(direct));
                if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            // ② pnpm 的 .pnpm/<名字>@<版本>/node_modules/<名字>（版本号就在目录名里）
            var pnpmDir = Path.Combine(modules, ".pnpm");
            if (!Directory.Exists(pnpmDir)) return null;
            // pnpm 把 scoped 包写成 `@scope+name`（斜杠换加号），所以这里必须用
            // **完整包名**去拼 glob：按最后一段（leaf）拼的话，scoped 包永远匹配不上
            // ——而本方法收集到的需求全是 @deepseek-ai/* ，也就是整条兜底路形同虚设。
            var pnpmLeaf = packageName.Replace('/', '+');
            foreach (var dir in Directory.GetDirectories(pnpmDir, pnpmLeaf + "@*"))
            {
                var version = Semver.ParsePnpmDirVersion(Path.GetFileName(dir));
                if (version is not null) return version;
            }
            return null;
        }
        catch { return null; }
    }

    // ParsePnpmDirVersion 已迁到 Semver（见该类头部说明）。

    /// <summary>
    /// 返回 (无法判定的条数, 明确不满足的明细)。
    /// dsh 那一族与候选引擎版本比较；其余包与"当前实际装着的版本"比较——
    /// 那些包不随引擎升级而变，所以"现在满足、升级后照样满足；现在不满足也不是升级造成的"。
    /// </summary>
    private static (int Unknown, List<string> Violations) CheckPluginCompatibility(string candidateVersion, string profileDir)
    {
        var violations = new List<string>();
        var unknown = 0;
        foreach (var req in CollectPluginRequirements(profileDir))
        {
            bool? verdict;
            string basis;
            if (Semver.IsDshVersionedPackage(req.Package))
            {
                verdict = Semver.SatisfiesRange(candidateVersion, req.Range);
                basis = $"引擎 {candidateVersion}";
            }
            else
            {
                var installed = ResolveInstalledPackageVersion(profileDir, req.Package);
                if (installed is null)
                {
                    // 这个包既不由引擎随版本提供、也不在 profile 里，无从判断。
                    unknown++;
                    continue;
                }
                verdict = Semver.SatisfiesRange(installed, req.Range);
                basis = $"当前 {installed}";
            }

            if (verdict is null) { unknown++; continue; }
            if (!verdict.Value) violations.Add($"{req.Plugin} 要求 {req.Package} {req.Range}（{basis}）");
        }
        return (unknown, violations);
    }

    private static async Task<string?> GetLatestEngineVersionAsync(string node, CancellationToken ct)
    {
        Process? proc = null;
        Task<string>? outTask = null;
        Task<string>? errTask = null;
        try
        {
            var npm = ResolveNpmPath(node);
            using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            queryCts.CancelAfter(TimeSpan.FromSeconds(EngineQueryTimeoutSeconds));

            // 注册表要单独取：RunCmdAsync 是异步的，不能再塞进下面的初始化器里。
            var registry = await ResolveNpmRegistryAsync(node, queryCts.Token);
            // 同 InstallEngineAsync：闸门设在拼接点，不靠调用方自觉。
            registry = CommandGuard.GuardRegistryForCommandLine(registry);
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c \"\"{npm}\" view {EnginePackageName} version --registry {registry}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var nodeDir = Path.GetDirectoryName(node);
            // 同 StartHarnessAsync：读父进程 PATH，只在缺时前置 nodeDir。
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(nodeDir) && !path.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(nodeDir, StringComparer.OrdinalIgnoreCase))
                psi.Environment["PATH"] = nodeDir + ";" + path;
            ConfigureOptionalProxy(psi);

            proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            // 取消击杀挂同步回调（理由见 RunCmdAsync 同款注释）：关窗丢续延时
            // 不能把孤儿 npm 留在后台。
            using var killOnCancel = queryCts.Token.Register(() =>
            {
                try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            // 与 RunCmdAsync 同一套纪律（理由见那里的注释，此处此前是反面教材）：
            // ① stderr 必须读掉——npm 往里写多了管道写满，子进程自己会卡死；
            // ② 不给 ReadToEndAsync 传 token——取消后剩余数据没人读；
            // ③ 超时/取消必须 Kill 整棵树——只 Dispose 会留下后台挂着的孤儿 npm。
            outTask = proc.StandardOutput.ReadToEndAsync();
            errTask = proc.StandardError.ReadToEndAsync();
            try { await proc.WaitForExitAsync(queryCts.Token); }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }

            // 进程退出了，管道**不一定**读完：孙进程若继承了 stdout 句柄且自己不退出，
            // ReadToEndAsync 会一直挂着——裸 await 会让「升级」永久 busy。版本查不出来
            // 是可接受的降级（保持当前引擎），界面卡死不是。与 GetToolVersionAsync
            // 已确立的排干纪律同一条（那里连更简单的 node --version 都防了）。
            var drained = await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(DrainTimeoutMs));
            if (drained is not Task<string[]> done)
            {
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }
            string stdout, stderr;
            try { stdout = done.Result[0]; stderr = done.Result[1]; }
            catch { return null; }
            // stderr 只进日志、绝不参与取版本（见 ParseNpmVersionOutput）。
            if (stderr.Trim().Length > 0) AppendStartupLog($"查询 {EnginePackageName} 版本时 npm 写到 stderr：{stderr.Trim()}");
            int exitCode;
            try { exitCode = proc.ExitCode; }
            catch { return null; }
            return Semver.ParseNpmVersionOutput(stdout, exitCode);
        }
        catch
        {
            // 其余异常（启动失败等）：同样可能留下孤儿进程与挂着的读取任务。
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            await DrainQuietlyAsync(outTask, errTask);
            return null;
        }
        finally { try { proc?.Dispose(); } catch { } }
    }

    // ParseNpmVersionOutput 已迁到 Semver（npm 版本输出的三道闸，见该类）。

    /// <summary>
    /// 「升级引擎」：查最新版 → 装到 staging → 成功才替换 → 重启。
    /// 全程不碰 npx，且任何一步失败都不会动当前能跑的引擎。
    /// </summary>
    private async Task RunEngineUpgradeAsync()
    {
        if (busy || closing || IsDisposed) return;

        var cts = new CancellationTokenSource();
        CancelPendingStart();
        startCts = cts;
        EnterBusy(upgradeButton, "升级中", cts);
        // swapped：引擎目录是否已经被换成新版本（catch 里据此说人话，见那里的三态）。
        // installed：换上去的那个版本号，用于失败提示里点名到底是哪个版本在盘上。
        var swapped = false;
        var installed = "";
        try
        {
            var node = await ResolveNodeAsync(cts.Token);
            var current = ReadEngineVersion(engineDir);

            // 版本锁优先于「升级」：钉住的版本可能正是插件唯一验证过的那个，
            // 按钮不该把它顶掉。想升级先清空 engine-version.txt。
            var pinned = ReadPinnedEngineVersion();
            if (pinned is not null)
            {
                SetInfo($"已锁定引擎版本 {pinned}，升级被跳过（清空 engine-version.txt 可解除）");
                if (!closing && !IsDisposed)
                    MessageBox.Show(this,
                        $"引擎版本已锁定为 {pinned}，不会升级。\n\n" +
                        "这是为了避免新版引擎让你的插件失效。\n" +
                        $"要升级请先删除或清空：\n{engineVersionPinFile}",
                        "引擎已锁定", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetInfo($"正在查询最新版本…（当前 {current ?? "未安装"}）");

            var latest = await GetLatestEngineVersionAsync(node, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (latest is null)
            {
                SetInfo("查不到最新版本（npm 或网络不可用），保持当前引擎");
                return;
            }
            // 纵深防御，且必须在"停引擎"之前：ParseNpmVersionOutput 已经只放行 semver，
            // 这里再卡一道 IsSafeVersionToken，是为了让**任何**取版本路径的异常都无法把
            // 垃圾值带到 StopHarnessProcessesAsync 之后才失败——那时引擎已停、无法自愈。
            // 有了它，下面那句"保持当前引擎"才真的是一条可达的降级路。
            if (!IsSafeVersionToken(latest))
            {
                AppendStartupLog($"拒绝升级：查到的“最新版本”不是合法版本 token（{latest}）");
                SetInfo("查到的版本号不合法，已中止升级，保持当前引擎");
                return;
            }
            if (current == latest)
            {
                SetInfo($"引擎已是最新（{latest}），无需升级");
                return;
            }

            // 兼容性护栏：升级前先拿新版本对一遍 profile 里插件的 peer 要求。
            // 实测本机 profile 只声明 6 个插件依赖、@deepseek-ai/* 全部由引擎提供，
            // 所以升级不会动插件文件，但插件声明的范围可能不覆盖新引擎——
            // 那种情况下升级会让插件失效，必须先让用户知情再决定。
            var (unknownCount, violations) = CheckPluginCompatibility(latest, webProfileDir);
            if (violations.Count > 0)
            {
                var lines = new StringBuilder();
                lines.AppendLine($"即将把引擎从 {current ?? "未安装"} 升级到 {latest}。");
                lines.AppendLine();
                lines.AppendLine($"⚠ 有 {violations.Count} 项插件要求无法满足：");
                foreach (var v in violations.Take(8)) lines.AppendLine("  · " + v);
                if (violations.Count > 8) lines.AppendLine($"  …另有 {violations.Count - 8} 项");
                lines.AppendLine();
                lines.AppendLine("标「引擎 x.y.z」的项与这次升级有关；标「当前 …」的项是那些包");
                lines.AppendLine("本来就不随引擎版本变动，升级不会改善它们。");
                if (unknownCount > 0)
                    lines.AppendLine($"另有 {unknownCount} 项因版本范围无法解析（多为预发布三选一）未能判定。");
                lines.AppendLine();
                lines.AppendLine("选择「否」保持当前引擎不变（插件的现有状态完全不受影响）。");

                // 弹窗挂到主窗体上（与"引擎已锁定"、ShowError 同一条纪律）：这个弹窗
                // 出现在最长可达 60 秒的 npm 查询之后，恰是最容易"用户已切走窗口"的
                // 时点——无属主的框可能被压到别的窗口后面，表现为"点了没反应"。
                var go = MessageBox.Show(this, lines.ToString(), "升级可能影响插件",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (go != DialogResult.Yes)
                {
                    SetInfo($"已取消升级，保持引擎 {current ?? "未安装"}（插件优先）");
                    return;
                }
            }

            // 运行中的实例占着引擎文件，先停干净再换目录。
            await StopHarnessProcessesAsync();
            if (cts.IsCancellationRequested) return;
            await WaitForPortToCloseAsync(DefaultPort, TimeSpan.FromSeconds(10), cts.Token);
            if (cts.IsCancellationRequested) return;

            SetInfo($"正在安装引擎 {latest}（先装临时目录，成功后替换）…");
            // 升级是配置迁移的实际触发点（引擎版本一变，下次启动就可能改写 settings.yaml），
            // 所以这里再拍一份——此时还是"升级前"的配置，是最有价值的还原点。
            // 放后台线程：与 RunStartAsync 的快照同一条纪律。
            await Task.Run(() => ConfigBackup.CreateSnapshot($"升级引擎前（{current ?? "未安装"} → {latest}）"));
            await InstallEngineAsync(node, latest, await ResolveNpmRegistryAsync(node, cts.Token), cts.Token, current);
            if (cts.IsCancellationRequested) return;

            // 替换已经落地：引擎目录此刻**装的就是新版本**。记下来给 catch 用——
            // 此后任何异常（含紧接着的启动失败）都不能再说"当前引擎未被改动"。
            swapped = true;
            installed = ReadEngineVersion(engineDir) ?? latest;

            SetInfo($"引擎已升级到 {installed}，正在重启…");
            await StartHarnessAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 用户主动取消（Esc / 主按钮）：他知道自己刚取消了，无需多言。
        }
        catch (OperationCanceledException)
        {
            // 没人取消却收到 OCE = **安装超时**：InstallEngineAsync 里
            // installCts.CancelAfter(EngineInstallTimeoutSeconds) 触发的取消同样以 OCE
            // 收场，此前它和用户取消共用上面那条静默路径——用户点完「升级」，15 分钟
            // 后一切悄然复位，信息栏无一字，既不知道发生了什么，也不知道引擎没被换掉。
            // （版本查询超时走不到这里：GetLatestEngineVersionAsync 自己吞成 null。）
            SetInfo($"引擎安装超时（{EngineInstallTimeoutSeconds} 秒），已中止升级；当前引擎未受影响。");
        }
        catch (Exception ex)
        {
            if (!closing && !IsDisposed)
            {
                // 尾部提示必须区分**三态**，不能只看 Directory.Exists：
                //
                // ① 还没替换（swapped == false）：当前引擎确实原封未动。
                // ② 替换成功、重启失败：磁盘上**已经是新版本**了。此前这里按
                //    Directory.Exists 判成"未被改动，仍可正常使用"——那是一句明确的假话：
                //    用户据此以为一切照旧，而新引擎会在下次启动静默生效；真出问题了他
                //    也想不到该用「版本管理」切回去。
                // ③ 引擎目录不存在：替换与回滚都失败（engine.old 也搬不回来）。
                var tail = !swapped
                    ? "当前引擎未被改动，仍可正常使用。"
                    : Directory.Exists(engineDir)
                        ? $"⚠ 引擎已替换为 {installed}，但本次启动失败——**当前引擎已被改动**。\n" +
                          "新引擎已在盘上，下次启动会直接使用它；当前引擎仍可正常使用。\n" +
                          "若新版本不合用，用「版本管理」可切回上一版本（替换前的版本被保留为 engine.old）。"
                        : "⚠ 引擎目录当前不存在（替换与回滚都没成功）。" +
                          "下次启动会自动重新安装，约 214 MB、1–2 分钟；历史对话在盘上，不会丢。";
                ShowError("引擎升级失败", ex.Message + "\n\n" + tail);
            }
        }
        finally
        {
            // 与 RunStartAsync 同一纪律：CTS 由创建它的操作负责释放；
            // EndBusy 判 busyOwner（取消会清空 startCts，拿它判就会漏掉复位）。
            if (ReferenceEquals(startCts, cts)) startCts = null;
            if (ReferenceEquals(busyOwner, cts))
            {
                busyOwner = null;
                EndBusy();
            }
            try { cts.Dispose(); } catch { }
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    /// <summary>
    /// 删除目录：node_modules 里常有只读文件，先清属性再删；失败给出可操作的提示
    /// （最常见原因是有 DSH 进程还占着引擎目录）。
    /// 用 \\?\ 扩展前缀绕过 260 字符上限——Windows 的 LongPathsEnabled 默认是关的，
    /// 而同学的用户名一变长（如 C:\Users\ZhangSanxxxx\AppData\...），
    /// 引擎 node_modules 的深路径就会超过 MAX_PATH，不加前缀时删除必定失败。
    /// </summary>
    private static void ForceDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var extended = ToExtendedPath(dir);
        try { ClearReadOnlyAttributes(extended); } catch { }
        try { Directory.Delete(extended, recursive: true); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法删除 {dir}：{ex.Message}\n（若为引擎目录被占用，请先点「停止」再重试）", ex);
        }
    }

    /// <summary>给绝对路径加 \\?\ 前缀，绕过 MAX_PATH（UNC 路径用 \\?\UNC\）。</summary>
    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full.Substring(2)
            : @"\\?\" + full;
    }

    private static void ClearReadOnlyAttributes(string dir)
    {
        // AttributesToSkip 必须含 ReparsePoint，理由与 DirectorySizeBytes 同源（见那里）：
        // ① 指向引擎目录**外部**的 junction 会让这里改掉**外部文件**的只读属性——
        //    用户从没要求我们碰那些文件，而"清只读"是一个带副作用的写操作；
        // ② 环形 junction 让 AllDirectories 永不终止，挂在后台线程上直到进程结束。
        // ForceDeleteDirectory 的下一步是 Directory.Delete(recursive)——**递归删除会跟随
        // junction 吗？**NTFS 上删除目录联接点只删链接本身，不删目标，所以真正的暴露面
        // 正是这里这一步（它会跟着链接走到目标）。跳过 ReparsePoint 后，被清只读的
        // 永远只是这份引擎目录里真正属于它的文件。
        foreach (var file in Directory.EnumerateFiles(
                     dir, "*",
                     new System.IO.EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                         IgnoreInaccessible = true,
                     }))
        {
            try
            {
                var attr = File.GetAttributes(file);
                if (attr.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
            catch { }
        }
    }
}
