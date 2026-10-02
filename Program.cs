using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekHarness;

internal static class Program
{
    /// <summary>
    /// 单实例互斥体。必须是静态字段：局部变量会被 GC 回收，句柄一释放锁就没了。
    /// </summary>
    private static Mutex? singleInstanceMutex;

    /// <summary>
    /// 本实例是否真的**持有**互斥体。named Mutex 在"已存在"时构造函数不会取得所有权，
    /// 无主 ReleaseMutex 只会抛异常再被吞掉——所有权标志记下来，退出时只释放该我们释放的。
    /// </summary>
    private static bool ownsSingleInstanceMutex;

    [STAThread]
    private static void Main()
    {
        // 构造窗体是会抛异常的（读设置文件、提取 exe 图标、加载字体都可能），
        // 而 Main 里没有任何兜底：之前一旦抛出就是"程序闪一下就没了"，
        // 用户拿不到任何可反馈的信息，也没法区分是缺 Node、缺引擎还是别的。
        try
        {
            ApplicationConfiguration.Initialize();

            // 全局异常兜底。此前只有 Main 的 try/catch：UI 线程事件处理器抛异常走
            // WinForms 默认对话框（不落 crash-log），后台线程/async void 逃逸的异常
            // 直接杀进程（WinExe 无控制台，什么都留不下）。三个钩子统一落到
            // WriteCrashLog；必须挂在任何窗口创建**之前**（SetUnhandledExceptionMode
            // 的约束），所以紧跟 Initialize。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            // UI 线程：写完日志后仍弹一次默认对话框——那是改动前用户就有的反馈
            // （可继续/退出），只删不加会让 UI 异常从"看得见"变成"无声继续"。
            Application.ThreadException += (_, e) =>
            {
                WriteCrashLog(e.Exception);
                try { using var dialog = new ThreadExceptionDialog(e.Exception); dialog.ShowDialog(); }
                catch { }
            };
            // 任意线程的未处理异常（多数场景进程随后即终）：只求留下死因。
            // ExceptionObject 契约上是 object（极端情况不是 Exception），直接强转
            // 会在处理器里再抛一次——用模式匹配兜住。
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) WriteCrashLog(ex);
                else WriteCrashLog(new Exception("非 Exception 的未处理对象：" + (e.ExceptionObject?.ToString() ?? "null")));
            };
            // 未观察的 Task 异常（.NET 默认不崩进程，但异常本身会无声消失）。
            // SetObserved 显式认领，防未来有人开启"未观察异常即崩溃"策略时反复触发。
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteCrashLog(e.Exception);
                e.SetObserved();
            };

            // 布局自检入口：只开两个对话框、在多个尺寸下记录几何、退出。不经过主窗体。
            // 加这个是因为主窗体在 DSH_LAYOUT_DUMP=1 下会先自己退出，对话框根本没机会被打开；
            // 而"拉伸对话框时内容不动"正是要验证的那件事，靠肉眼看窗口验证不了。
            if (Environment.GetEnvironmentVariable("DSH_LAYOUT_TEST") == "1")
            {
                RunLayoutSelfTest();
                return;
            }

            // 没有单实例保护时，点两次图标会出现两个启动器，然后：
            // 第二个实例的 StopHarnessProcessesAsync 会按命令行正则命中第一个实例的引擎
            // 并整树杀掉（IsHarnessCommand 只排除 DeepSeekHarness.exe，不排除"别人启的 dsh"），
            // 两者还会争同一个 web-url.txt 和 3080 端口，表现为"莫名其妙就断了"。
            if (!TryAcquireSingleInstance())
            {
                ActivateExistingWindow();
                return;
            }

            Application.Run(new HarnessForm());
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show(
                "DeepSeek Harness 启动器启动失败：\n\n" + ex.Message +
                "\n\n详细信息已写入：\n" + CrashLogPath,
                "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // 只在**真的持有**且**持有者就是当前线程**时释放。ReleaseMutex 跨线程调用
            // 抛 ApplicationException——Main 与 Acquire 在同一线程，理论上不会发生，
            // 但那正是最不该靠"理论上"活着的地方：异常被 catch { } 吞掉之后，
            // 互斥体会一直挂到进程退出，"上一个实例还在跑"的判断也就失真了。
            // 无论哪种失败都留痕——单实例保护的状态是排查"为什么开了两个启动器"的
            // 第一条线索，吞掉它等于把唯一的证据扔了。
            if (ownsSingleInstanceMutex)
            {
                try { singleInstanceMutex?.ReleaseMutex(); }
                catch (Exception ex)
                {
                    HarnessForm.AppendStartupLog(
                        $"[single-instance] 释放互斥体失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
            try { singleInstanceMutex?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 打开两个对话框，在若干尺寸下各排一次并记录几何，然后退出。
    /// DSH_LAYOUT_TEST=1 触发；用于验证"拉伸窗口时内容是否真的跟着走"。
    /// </summary>
    private static void RunLayoutSelfTest()
    {
        // 自检必须真的落盘：LayoutDump.Capture 只认 DSH_LAYOUT_DUMP=1，
        // 只设 DSH_LAYOUT_TEST 的话会"跑了不写、白忙一场"。这里就地补上
        //（只影响本进程，不改外部环境）。
        Environment.SetEnvironmentVariable("DSH_LAYOUT_DUMP", "1");

        try
        {
            var dump = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness", "layout-dump.txt");
            if (File.Exists(dump)) File.Delete(dump);
        }
        catch { }

        using (var versions = new EngineVersionsForm(
                   () => Task.FromResult<IReadOnlyList<EngineVersionEntry>>(Array.Empty<EngineVersionEntry>()),
                   _ => Task.FromResult<string?>(null),
                   _ => Task.FromResult<string?>(null)))
        {
            versions.Show();
            Application.DoEvents();
            foreach (var size in new[] { new Size(560, 250), new Size(560, 380), new Size(780, 300), new Size(440, 214) })
            {
                versions.ClientSize = size;
                Application.DoEvents();
                LayoutDump.Capture($"版本管理 客户区={versions.ClientSize.Width}x{versions.ClientSize.Height}",
                    versions, versions.DumpControls());
            }
            // 静默关闭：编程式 Close() 的 CloseReason 是 UserClosing，busy 没复位时
            // 会弹"确定要关闭吗"确认框——自检进程没人应答，就挂死在那里（Collect
            // 最多 200 个子目录的枚举，慢盘/杀软下完全可能拖过下面那几轮 DoEvents）。
            versions.QuietClose = true;
            versions.Close();
        }

        using (var folders = new FoldersForm())
        {
            folders.Show();
            Application.DoEvents();
            foreach (var size in new[] { new Size(660, 286), new Size(660, 420), new Size(920, 320), new Size(470, 226) })
            {
                folders.ClientSize = size;
                Application.DoEvents();
                LayoutDump.Capture($"目录 客户区={folders.ClientSize.Width}x{folders.ClientSize.Height}",
                    folders, folders.DumpControls());
            }
            folders.QuietClose = true;
            folders.Close();
        }
    }

    /// <summary>
    /// Local\ 作用域 = 每个登录会话一个实例。进程清扫侧另有限定
    /// （IsHarnessCommand 宽松分支只认 %USERPROFILE% 下的路径），会话之间不会互杀引擎；
    /// 但 3080 端口是整机唯一的，同机多用户实际仍只能有一个引擎在跑。
    /// </summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            // 构造函数**不等待**：它只在对象已存在时把 createdNew 置 false 并打开
            // 同一个互斥体，不会抛 AbandonedMutexException（那是 WaitOne 才会抛的）。
            // 所以这里没有对应的 catch —— 此前那个 catch 是死代码，注释还把它说成
            // "上一个实例被强杀"的处理路径，会误导下一个改这里的人。
            //
            // 上一任被强杀的情形本来就自动成立：命名互斥体在最后一个句柄关闭、
            // 且无线程持有它时由内核销毁，于是本进程构造时必然 createdNew = true，
            // 不需要任何补偿。真正需要防的是"另一个实例还活着"（createdNew = false），
            // 那条路走的是 ActivateExistingWindow。
            singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\DeepSeekHarness.Launcher", out var createdNew);
            ownsSingleInstanceMutex = createdNew;
            return createdNew;
        }
        catch (Exception ex)
        {
            // 拿不到互斥体不该拦住用户启动——但**必须留痕**。原实现是一个
            // 没有 catch 变量的 `catch { return true; }`：单实例保护在这一刻失效
            // （两个实例互相杀引擎、抢同一个 web-url.txt 与 3080 端口，
            // 表现为"莫名其妙就断了"），而日志里一个字都没有，
            // 事后排查完全无从知道"当时是不是根本没上锁"。
            HarnessForm.AppendStartupLog(
                $"[single-instance] 创建互斥体失败，本次按“无单实例保护”启动：{ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 第二个实例要做的事：把已经在跑的那个窗口提到前台，而不是静默退出——
    /// 否则用户会以为"双击没反应"。
    /// </summary>
    private static void ActivateExistingWindow()
    {
        // 候选必须是**本程序**：只按进程名找，会把同名的另一个程序（比如从源码树
        // 跑起来的旧版、或用户自己放在别处的可执行文件）的主窗口提到前台——
        // 用户看到的是"我双击了 A，结果弹出来的是 B 的窗口"。与 ProcessMatch.MatchesHarnessCommand
        // 里"同名但路径不对就不是自己人"是同一条纪律，这里只是用在了自己身上。
        var self = Environment.ProcessId;
        var mine = string.Empty;
        try { mine = Environment.ProcessPath ?? Application.ExecutablePath; } catch { }
        var activated = false;
        try
        {
            // 首个实例可能还在构造窗口（MainWindowHandle 暂为 0）：只查一次会"找到进程
            // 却提不起窗"然后静默退出，用户看到的仍是"双击没反应"。轮询 3 秒兜住这个窗口期；
            // 正常情况第一轮就命中，不会增加延迟。
            // Process 对象持有内核句柄，不 Dispose 要等 GC 才释放——这里必须显式收掉，
            // 漏掉的那几个会一直挂在进程表里。
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var process in Process.GetProcessesByName("DeepSeekHarness"))
                {
                    using (process)
                    {
                        if (process.Id == self) continue;
                        if (!IsSameExecutable(process, mine)) continue;
                        var handle = process.MainWindowHandle;
                        if (handle == IntPtr.Zero) continue;
                        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                        // SetForegroundWindow 可能因前台锁策略被拒（返回 false）。
                        // 失败不是"提起来了"，别当成功——否则下面的提示不会弹，
                        // 用户看到的就是纯粹的"双击没反应"。
                        activated = SetForegroundWindow(handle);
                        if (activated) return;
                    }
                }
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            HarnessForm.AppendStartupLog(
                $"[single-instance] 激活已有窗口失败：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // 3 秒内既没找到窗口、也没提起来：不要静默退出。原来这里是"什么都不做就
        // return"，用户双击后的体感与"程序崩了"完全一致，而 startup-log 里同样一个字没有。
        // 弹一句即可：说清是"已有实例在跑但提不起来"，并给出可操作的建议
        // （任务栏里找 / 任务管理器看是不是卡死了）。
        try
        {
            MessageBox.Show(
                "检测到已有一个 DeepSeek Harness 正在运行，但没能把它的窗口提到前台。\n\n" +
                "请在任务栏或 Alt+Tab 里找一下；若那个实例已卡死，用任务管理器结束它再重试。\n\n" +
                $"（已尝试 {ActivateTimeoutSeconds} 秒；窗口路径：{mine}）",
                "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { }
    }

    private const int ActivateTimeoutSeconds = 3;

    /// <summary>两个进程是不是同一个可执行文件（同路径，比路径字符串）。</summary>
    private static bool IsSameExecutable(Process process, string mine)
    {
        if (string.IsNullOrEmpty(mine)) return true;   // 取不到自己的路径就别拦（宁提错别不提示）
        try
        {
            var path = process.MainModule?.FileName;
            return !string.IsNullOrEmpty(path) &&
                   string.Equals(path, mine, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekHarness", "crash-log.txt");

    private static void WriteCrashLog(Exception ex)
    {
        // 三个异常钩子可能并发进来（UI 线程 + 任意后台线程 + 未观察任务），
        // 而它们全都对同一个文件做"读—拼—写"。不加锁时两个写入者会各自读到旧内容、
        // 各自写回自己那份，其中一条堆栈就此丢失——而崩溃日志丢的恰恰是死因。
        lock (crashLogGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                var entry = $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{ex}\n\n";
                var existing = File.Exists(CrashLogPath)
                    ? File.ReadAllText(CrashLogPath, Encoding.UTF8)
                    : string.Empty;
                var combined = existing + entry;
                // 崩溃循环会把它无限撑大：超限时从最近一条旧记录的表头切起，
                // 保留"上一次 + 这一次"。**单条记录本身就超限时**，原实现把
                // head >= 0 判成不可能（上一条前缀怎么也有几百字节）而留下
                // existing[head..] + entry —— combined 其实一直超限，于是每一轮
                // 都要把整份文件读进来、拼一次、写一次，而长度不减：
                // 一个反复抛的大异常（典型是 OOM 或栈溢出）会把"读+写"变成每轮
                // 上百 MB 的 IO，日志目录先被撑爆，死因反而最先被挤出去。
                // 现在直接复用 LogTrim.Tail——它已经处理了
                // "单条超长"这条边界（保留尾部而不是返回空串）。
                var trimmed = LogTrim.Tail(combined);
                File.WriteAllText(CrashLogPath, trimmed, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>崩溃日志的写互斥（进程内）。见 <see cref="WriteCrashLog"/>。</summary>
    private static readonly object crashLogGate = new();
}
