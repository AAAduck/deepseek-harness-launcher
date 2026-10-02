// ── HarnessForm 的「环境检测报告」部分 ──────────────────────────────────────────
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

    // ---- 环境检测 -----------------------------------------------------------

    private async Task RunEnvCheckAsync()
    {
        // busy 也挡：环境检测会改状态文案（"检测中"会覆盖"启动中"）并弹模态报告，
        // 与进行中的启动/升级互相踩。**而且本方法自己也要占 busy**：探测段是
        // 4 个候选 × 10 秒级的等待，这期间「启动/升级」全亮——用户点「启动」把引擎
        // 拉起来，再在随后弹出的报告里点「是」恢复配置，就是启动器自己把"快照机制
        // 要防的那件事"（引擎运行中被改写配置）给诱发了。
        if (busy || closing || IsDisposed) return;
        var self = new object();
        EnterBusy(envButton, "检测中", self);
        try
        {
            // ── 报告正文 ────────────────────────────────────────────────────────
            // 组织原则：默认只列"会影响能否使用"的结论，一切正常的项一行带过。
            // 之前那份把路径、监听端口全表、操作指南都塞了进来，读的人得自己筛。
            // 三条判据：① 能不能跑 ② 有没有风险 ③ 出问题去哪儿查。
            var sb = new StringBuilder();
            var issues = new List<string>();      // 需要用户处理的问题
            var notes = new List<string>();       // 值得知道的提醒（不是错误）

            // ① 必需组件。只报"能否用"，不报路径——路径属于排查时才需要的信息。
            string? nodeExe = null;
            try { nodeExe = await ResolveNodeAsync(CancellationToken.None); }
            catch (Exception ex) { issues.Add("Node.js：" + ex.Message.Split('\n')[0]); }

            if (nodeExe is not null)
            {
                var nodeVersion = (await GetToolVersionAsync(nodeExe, "--version"))?.Trim();
                var npm = FindToolBeside(nodeExe, "npm.cmd") ?? FindTool("npm.cmd");
                if (npm is null)
                    issues.Add("npm 未找到（npm 随 Node 一起安装，建议重装 Node.js）");
                else
                    sb.AppendLine($"✓ Node.js {nodeVersion ?? "?"}　npm 就绪");
            }

            var engine = ReadEngineVersion(engineDir);
            if (engine is null)
                issues.Add("DSH 引擎未安装（点「启动」会自动安装，约 1–2 分钟）");
            else
                sb.AppendLine($"✓ DSH 引擎 {engine}");

            var profileReady = File.Exists(Path.Combine(webProfileDir, "package.json"));
            if (!profileReady)
                issues.Add("profile 未初始化（首次点「启动」会自动完成）");

            // 端口：只有被占才算问题；空闲时不必占一行。
            if (await IsPortListeningAsync(DefaultPort))
            {
                if (await ProbeServerAsync(DefaultPort, CancellationToken.None))
                    sb.AppendLine($"✓ 端口 {DefaultPort}：DSH 正在运行");
                else
                {
                    issues.Add($"端口 {DefaultPort} 被其他程序占用");
                    var hint = await DescribeDynamicPortRangeAsync(DefaultPort);
                    if (hint is not null) notes.Add(hint);
                }
            }

            // ② 可选组件。缺失只提醒，不算问题——它们只影响插件更新。
            var pnpm = ResolvePnpmPath(nodeExe);
            var corepack = ResolveCorepackPath(nodeExe);
            if (pnpm is null && corepack is null)
                notes.Add("pnpm 未安装，插件更新会被跳过（npm install -g pnpm 可补上）");

            // ③ 风险项：版本锁、可回退版本、插件兼容性。
            var pinnedNow = ReadPinnedEngineVersion();
            if (pinnedNow is not null)
                sb.AppendLine($"🔒 引擎已锁定 {pinnedNow}（升级被禁用）");

            var reqs = CollectPluginRequirements(webProfileDir);
            if (reqs.Count > 0 && engine is not null)
            {
                var (unknown, bad) = CheckPluginCompatibility(engine, webProfileDir);
                if (bad.Count == 0 && unknown == 0)
                    sb.AppendLine($"✓ 插件兼容性：{reqs.Count} 项要求全部满足");
                else if (bad.Count == 0)
                    sb.AppendLine($"✓ 插件兼容性：{reqs.Count} 项中 {unknown} 项无法判定，未见冲突");
                else
                {
                    // 不满足的明细要全部列出——这正是用户需要据此行动的信息。
                    sb.AppendLine($"⚠ 插件兼容性：{bad.Count} 项不满足");
                    foreach (var b in bad) sb.AppendLine("    · " + b);
                }
            }

            if (Directory.Exists(engineStageDir))
                notes.Add("存在未完成的安装残留 engine.tmp（下次安装会自动清理）");

            if (Directory.Exists(engineMigratingDir))
            {
                // 它不参与版本列举，界面上看不见，却实实在在占着一份引擎的空间——
                // 不说出来就等于"坏了没人知道"。下一次点「启动」会自动收尾归档。
                var claimed = ReadEngineVersion(engineMigratingDir);
                notes.Add(claimed is null
                    ? "存在未完成的版本归档 engine.migrating（下次点「启动」会处理；版本读不出时将原样保留）"
                    : $"存在待归档的上一版本 {claimed}（engine.migrating，下次点「启动」会自动归档）");
            }

            var oldEngine = ReadEngineVersion(engineOldDir);
            if (oldEngine is not null)
                notes.Add($"可回退到 {oldEngine}（在 engine.old）");

            var latestBackup = ConfigBackup.LatestSnapshot();
            if (latestBackup is not null)
                notes.Add($"配置备份最新一份：{Directory.GetCreationTime(latestBackup):MM-dd HH:mm}");
            else
                notes.Add("尚无配置备份（点「启动」会自动生成）");

            // ── 凭据落盘形态 ──────────────────────────────────────────────────
            // web-url.txt 平时是 DPAPI 密文（只有当前 Windows 用户能解开）。
            // DPAPI 被企业策略禁用时会静默降级成明文——那是**威胁模型失效**，
            // 而所有对外信号都还在说"已加密"。降级事实只在 startup-log 里留痕不够：
            // 用户不会去翻日志，环境检测是唯一他会主动看的面板，所以在这里报出来。
            try
            {
                if (File.Exists(urlFile) && !DpapiFile.IsEncryptedFile(urlFile))
                    issues.Add(
                        "认证链接 web-url.txt 当前是**明文**（DPAPI 加密未生效，多半是企业策略禁用）。" +
                        "同用户下的任何进程都能读到其中的 token；删掉该文件会在下次启动时重新生成。");
            }
            catch { /* 读不了属性就当作没这条结论，不影响其余检测 */ }

            // ── 汇总 ────────────────────────────────────────────────────────────
            var head = new StringBuilder();
            if (issues.Count == 0)
            {
                head.AppendLine("环境正常，可以启动。");
            }
            else
            {
                head.AppendLine($"发现 {issues.Count} 个问题：");
                foreach (var issue in issues) head.AppendLine("  ✗ " + issue);
            }
            head.AppendLine();
            head.Append(sb);

            if (notes.Count > 0)
            {
                head.AppendLine();
                head.AppendLine("其他：");
                foreach (var note in notes) head.AppendLine("  ○ " + note);
            }

            // 恢复入口只在真有快照时才提，且不打扰"没问题"的情况。
            // Yes/No 的默认按钮必须落在「否」：Yes 的动作是**用旧快照覆盖当前配置**，
            // 环境一切正常时用户习惯性回车/顺手一点，不该触发回滚——这是脚枪。
            var footer = latestBackup is null
                ? string.Empty
                : "\n\n（仅当确实要回滚配置时才点「是」——会用最新快照覆盖当前配置；「否」= 只关闭）";
            // 关窗守卫必须在这里（弹窗前）做：环境检测的 await 段最长几十秒
            // （ResolveNodeAsync 逐个 node --version、端口探测、netsh），期间关窗，
            // 续延仍会被分发（见 StartHarnessAsync "取消检查点"那段注释的自证）。
            // 对已释放窗体 MessageBox.Show(this,…) 抛 ObjectDisposedException，
            // 落进下面 catch 后 ShowError 会**再抛一次**——异常逃出 async void
            // 事件处理器（envButton.Click），无人接管。本文件其余弹窗路径
            // （RunStartAsync/StopClickedAsync/RunEngineUpgradeAsync）都有这道守卫，
            // 这里是最后一个漏网点。
            if (closing || IsDisposed) return;
            var restore = MessageBox.Show(this, head + footer, "环境检测",
                latestBackup is null ? MessageBoxButtons.OK : MessageBoxButtons.YesNo,
                issues.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button2);
            if (restore == DialogResult.Yes && latestBackup is not null && !closing && !IsDisposed)
                await RestoreLatestBackupAsync(latestBackup);
        }
        catch (Exception ex)
        {
            // catch 里同样不能对已释放窗体弹窗——否则上面的守卫形同虚设
            // （异常从 ShowError 二次逃逸）。窗体没了就退回 startup-log 留痕。
            if (closing || IsDisposed)
                try { AppendStartupLog("环境检测失败：" + ex.Message); } catch { }
            else
                ShowError("环境检测失败", ex.Message);
        }
        finally
        {
            // 与 RunStartAsync 同一套：EndBusy 只在"我还是这次忙碌态的主人"时收口。
            // 本方法不参与 startCts（不注册可取消的 CTS），所以此前是无条件 EndBusy；
            // 仍改成按 busyOwner 判定，避免窗口期间被后继操作接管时替它收尾。
            if (ReferenceEquals(busyOwner, self))
            {
                busyOwner = null;
                EndBusy();
            }
            if (!closing && !IsDisposed) await RefreshStatusAsync();
        }
    }

    /// <summary>
    /// 用最新快照覆盖回 $DSH_HOME。刻意只覆盖快照里有的文件、不动其他任何东西，
    /// 并在覆盖前把"当前状态"再存一份——万一恢复错了还能退回来。
    /// </summary>
    private async Task RestoreLatestBackupAsync(string snapshotDir)
    {
        var confirm = MessageBox.Show(this,
            $"用这份快照覆盖当前配置？\n\n快照：{Path.GetFileName(snapshotDir)}\n" +
            $"目标：{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\.dsh\\\n\n" +
            "只覆盖快照里存在的文件（.credentials.yaml、settings.yaml、各 profile 的 cordis*.yml 等），\n" +
            "不碰 node_modules 与引擎。覆盖前我会先把当前状态另存一份。\n\n" +
            "恢复后需要重启引擎（点「停止」再点「启动」）才会生效。",
            "恢复配置备份", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        // 先给"恢复前"的状态留一份，保证这一步本身也可逆。
        // force: true——留底**不参与**"内容有变化才拍"的去重：若距上次快照只有
        // 凭据轮换过（易变豁免会判成"不用拍"），不带 force 就不留底，随后照常
        // 覆盖，用户最新一次登录拿到的 token 被旧快照顶掉——而确认框刚刚
        // 承诺了"覆盖前我会先把当前状态另存一份"。
        // 放后台线程：与启动/升级前的快照同一条纪律（真实磁盘 I/O 不上 UI 线程）。
        var rollback = await Task.Run(
            () => ConfigBackup.CreateSnapshot("恢复配置前（当前状态）", force: true));
        // await 期间用户可能关窗：后续两个 MessageBox.Show(this,…) 都会抛
        // ObjectDisposedException 并从 async void 路径逃逸。恢复动作本身已经
        // 完成与否都无妨——窗体没了，没有任何反馈可以落到。
        if (closing || IsDisposed) return;
        if (rollback is null)
        {
            // 留底失败就**中止**：确认框刚刚承诺"覆盖前我会先把当前状态另存一份"，
            // 而恢复的全部安全性都建立在这份留底上。备份目录写不进（磁盘满/权限被撤/
            // 路径无法解析）而配置目录写得进时，硬着头皮恢复 = 用旧快照覆盖当前配置
            // 且没有任何回退副本——宁可让用户多排查一步，也不能替他做不可逆的决定。
            // 此前只是记一条 startup-log 后照常恢复、覆盖完才在结果框里补一句警告，
            // 承诺兑现不了仍然执行——正是"留底"要防的那种不可逆覆盖。
            AppendStartupLog("恢复前未能留底（快照失败）：已中止恢复，当前配置未被改动。");
            MessageBox.Show(this,
                "恢复已中止，当前配置没有任何改动。\n\n" +
                "原因：覆盖前需要先把当前状态另存一份（留底），但这一步失败了——" +
                "最常见的原因是磁盘空间不足，或 config-backups 目录写入被拒。\n\n" +
                "排查后重新点「环境」再试；确实要恢复时，也可以手动把快照目录里的文件" +
                "按相同相对路径复制回 .dsh（覆盖前先点「停止」）。",
                "恢复配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Restore 也是逐文件覆盖写，同样不挂 UI 线程。
        var result = await Task.Run(() => ConfigBackup.Restore(snapshotDir));
        if (closing || IsDisposed) return;
        if (result.Restored == 0 && result.AnyFailed)
        {
            MessageBox.Show(this,
                "恢复失败，没有任何一个文件被写回：\n\n" + DescribeRestoreFailures(result) +
                "\n\n最常见的原因是引擎正在运行、占着这些文件：请先点「停止」再试。",
                "恢复配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageBox.Show(this,
            $"已从快照恢复 {result.Restored} 个配置文件。" +
            (result.AnyFailed
                ? $"\n\n⚠ 但有 {result.Failed.Count} 个文件**没能恢复**，" +
                  $"当前配置处于新旧混合状态：\n{DescribeRestoreFailures(result)}" +
                  "\n\n请点「停止」后重新恢复一次。"
                : "") +
            "\n\n请点「停止」再点「启动」重启引擎使其生效。",
            "恢复配置", MessageBoxButtons.OK,
            result.AnyFailed ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>失败清单的展示形态：最多列 12 条，其余只报数量。</summary>
    private static string DescribeRestoreFailures(ConfigBackup.RestoreResult result) =>
        string.Join("\n", result.Failed.Take(12)) +
        (result.Failed.Count > 12 ? $"\n……另有 {result.Failed.Count - 12} 个" : string.Empty);

    private static async Task<string?> GetToolVersionAsync(string exe, string args)
    {
        Process? proc = null;
        Task<string>? outTask = null;
        Task<string>? errTask = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            // 取消击杀挂同步回调（理由见 RunCmdAsync 同款注释）；超时（本方法的唯一
            // 取消源）同样经 CancelAfter 触发回调，node --version 挂死也不会留孤儿。
            using var killOnCancel = cts.Token.Register(() =>
            {
                try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            // 与 RunCmdAsync 同一套纪律：① stderr 必须读掉（管道写满会让子进程卡死）；
            // ② 不给 ReadToEndAsync 传 token——被取消后剩余数据就没人读，任务会带着
            // 未观察异常退场；③ 超时/取消必须 Kill 整棵树。三个合起来才是不留孤儿进程的做法。
            outTask = proc.StandardOutput.ReadToEndAsync();
            errTask = proc.StandardError.ReadToEndAsync();
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }

            // 进程退出了，管道**不一定**读完：孙进程若继承了 stdout 句柄且自己不退出，
            // ReadToEndAsync 会一直挂着——而这一段原本没有任何超时，整个方法就此永挂。
            // 工具版本读不出来是可接受的降级，界面卡住不是。
            var drained = await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(DrainTimeoutMs));
            // 排干超时（孙进程继承句柄、读取永不完成）也要收尾——这条早退路原本
            // 漏掉了 DrainQuietlyAsync，与 OCE/catch 两个分支自己声明的纪律不一致，
            // 留下的挂起读取任务会带着未观察异常退场。
            if (drained is not Task<string[]> done)
            {
                await DrainQuietlyAsync(outTask, errTask);
                return null;
            }
            try
            {
                var text = done.Result[0] + done.Result[1];
                var first = text.Split('\n').FirstOrDefault();
                return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
            }
            catch { return null; }
        }
        catch
        {
            // 其余异常（启动失败、句柄已被释放等）：同样可能留下活着孤儿进程。
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            // 读取任务同样要收尾，否则它们带着未观察异常退场。
            await DrainQuietlyAsync(outTask, errTask);
            return null;
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
        }
    }

    /// <summary>子进程已退出后等待两个读取任务收尾的上限（毫秒）。</summary>
    private const int DrainTimeoutMs = 2000;

    /// <summary>
    /// 有界地等两个读取任务收尾：句柄被孙进程继承时它们可能永不完成，
    /// 所以既不能裸 await（挂死），也不能丢下不管（未观察异常）。
    /// </summary>
    private static async Task DrainQuietlyAsync(Task<string>? outTask, Task<string>? errTask)
    {
        var pending = new[] { outTask, errTask }.Where(t => t is not null).Select(t => t!).ToArray();
        if (pending.Length == 0) return;
        try { await Task.WhenAny(Task.WhenAll(pending), Task.Delay(DrainTimeoutMs)); }
        catch { }
        foreach (var t in pending)
            _ = t.ContinueWith(static x => { _ = x.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>
    /// 可选代理。注意 7897 这个探测：本机没有 HTTP(S)_PROXY 变量时，此前每次启动
    /// 都会去连一次 127.0.0.1:7897 并等满超时（实测 150–206 ms）；而绝大多数机器
    /// 上那个端口并不存在，等于每次启动白付一笔。现在只有用户明确设过代理变量
    /// （说明他确实在用本地代理）才做这次探测。
    ///
    /// 探测结果正负分开缓存（见 <see cref="ShouldProbeLocalProxy"/> 与 <see cref="IsLocalProxyOpen"/>）：
    ///   探到了 → 永久缓存（代理开着是稳定状态，之后零成本）；
    ///   探不到 → 负缓存 60 秒。四条调用路径挤在一次启动里，60 秒窗口消掉重复探测；
    ///   而"启动器开着才打开代理"的用户最多多等 60 秒。
    /// </summary>
    private static void ConfigureOptionalProxy(ProcessStartInfo psi)
    {
        var http = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var https = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var hasProxyVars = !string.IsNullOrWhiteSpace(http) || !string.IsNullOrWhiteSpace(https);
        if (hasProxyVars)
        {
            // 用户自己配了系统代理：npm 会读 HTTP(S)_PROXY，但 Node 24+ 还需要这个开关
            // 才会让 node/npx 也走代理（旧版 Node 忽略该变量，设了无害）。
            psi.Environment["NODE_USE_ENV_PROXY"] = "1";
            return;
        }

        if (!ShouldProbeLocalProxy()) return;
        if (!IsLocalProxyOpen()) return;
        psi.Environment["NODE_USE_ENV_PROXY"] = "1";
        psi.Environment["HTTP_PROXY"] = $"http://127.0.0.1:{ProxyPort}";
        psi.Environment["HTTPS_PROXY"] = $"http://127.0.0.1:{ProxyPort}";
    }

    /// <summary>
    /// 是否值得去探一次本机代理端口（结果缓存到进程结束）。
    /// 只在系统里存在任何代理线索时才探，避免无代理机器每次启动白等一个超时。
    /// DSH_FORCE_LOCAL_PROXY=1 可强制探测（给"代理变量没设但确实在跑 7897"的用户留出口）。
    /// </summary>
    private static bool? localProxyProbeWanted;

    private static bool ShouldProbeLocalProxy()
    {
        if (localProxyProbeWanted.HasValue) return localProxyProbeWanted.Value;
        var probe = string.Equals(
            Environment.GetEnvironmentVariable("DSH_FORCE_LOCAL_PROXY"), "1", StringComparison.Ordinal);
        if (!probe)
        {
            foreach (var name in new[] { "ALL_PROXY", "all_proxy", "NO_PROXY", "npm_config_proxy", "npm_config_https_proxy" })
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) continue;
                probe = true;
                break;
            }
        }
        localProxyProbeWanted = probe;
        return probe;
    }

    // ── 7897 探测结果缓存（正负分开）──────────────────────────────────────────
    // 刻意是“引用类型快照 + Volatile”而不是两个散装字段（bool? + DateTime）：
    // bool? 是 2 字节、DateTime 是 8 字节结构，跨线程裸读都不保证原子；Volatile.Read
    // 又只接受引用类型。整份快照原子替换后，“open 状态”与“探测时刻”永远成对，
    // 不会读到“新状态配旧时刻”——与 GetDynamicPortRangeAsync 的
    // DynamicPortRangeSnapshot 同一形状、同一条纪律。
    private sealed record LocalProxyProbe(bool Open, long AtUtcTicks);

    private static volatile LocalProxyProbe? localProxyProbe;
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 127.0.0.1:7897 是否开放，带正负分开缓存。
    /// 探到了永久缓存；探不到缓存 60 秒后重探。
    /// </summary>
    private static bool IsLocalProxyOpen()
    {
        var probe = localProxyProbe;
        if (probe is not null)
        {
            if (probe.Open) return true;
            if (DateTime.UtcNow.Ticks - probe.AtUtcTicks < NegativeCacheTtl.Ticks)
                return false;
        }

        var open = IsTcpOpen("127.0.0.1", ProxyPort);
        localProxyProbe = new LocalProxyProbe(open, DateTime.UtcNow.Ticks);
        return open;
    }

    private static bool IsTcpOpen(string host, int port, int timeoutMs = 150)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            // 用 WaitAny 而不是 task.Wait(timeout)：超时后能被取消，也不吞掉 AggregateException。
            if (Task.WaitAny(new Task[] { connect }, timeoutMs) != 0)
            {
                // 超时分支：连接任务还在后台跑，finally 的 Dispose 之后它会带着
                // ObjectDisposedException（或 refused 的 SocketException）完成——
                // 不观察就是未观察异常退场，与本文件"永不带未观察异常退场"的
                // 纪律（GetToolVersionAsync/DrainQuietlyAsync）不一致。
                ObserveTaskFault(connect);
                return false;
            }
            // **快速拒绝**分支：任务已完成且多半是 faulted（回环端口没人在听时
            // ConnectAsync 立刻抛 SocketException，这是最常见的负例）。原实现在
            // 这里直接 return client.Connected，既不看 IsFaulted 也不读 Exception——
            // 任务上再无其他引用，随 GC 触发 TaskScheduler.UnobservedTaskException，
            // 与本文件自己的纪律直接冲突（而且恰好发生在最常见的路径上）。
            if (connect.IsFaulted)
            {
                ObserveTaskFault(connect);
                return false;
            }
            if (connect.IsCanceled) return false;
            return client.Connected;
        }
        catch { return false; }
        finally { try { client?.Dispose(); } catch { } }
    }

    /// <summary>
    /// 观察一个已（可能）故障的 Task 的异常，使它不再以"未观察异常"退场。
    /// 读 <see cref="Task.Exception"/> 本身就完成观察；挂一个续延同样可以，
    /// 两者都在异常已发生时立刻生效，与有没有人 await 无关。
    /// </summary>
    private static void ObserveTaskFault(Task task)
    {
        try
        {
            if (task.IsFaulted) { _ = task.Exception; return; }
        }
        catch { }
        try
        {
            _ = task.ContinueWith(static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch { }
    }

    /// <summary>
    /// <see cref="IsTcpOpen"/> 的异步版。**轮询与等待路径一律走这个**：
    /// 同步版靠 Task.WaitAny 阻塞调用线程，而它在启动等待循环里每 250 ms 调一次
    /// （回环端口被防火墙 DROP 时每次都等满超时），等于把 UI 线程卡掉近一半时间。
    /// 这里直接给 ConnectAsync 挂超时，await 期间线程完全释放。
    /// </summary>
    private static async Task<bool> IsTcpOpenAsync(string host, int port, int timeoutMs,
                                                    CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
        finally { try { client.Dispose(); } catch { } }
    }

    private static bool ProcessHasExited(Process process)
    {
        // 进程对象可能已被 StopHarnessProcessesAsync / FormClosing 释放，
        // 此时 HasExited 会抛 InvalidOperationException("No process is associated with this object.")。
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }
}
