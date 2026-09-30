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
            if (ownsSingleInstanceMutex)
                try { singleInstanceMutex?.ReleaseMutex(); } catch { }
            try { singleInstanceMutex?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 打开两个对话框，在若干尺寸下各排一次并记录几何，然后退出。
    /// DSH_LAYOUT_TEST=1 触发；用于验证"拉伸窗口时内容是否真的跟着走"。
    /// </summary>
    private static void RunLayoutSelfTest()
    {
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
            folders.Close();
        }
    }

    /// <summary>Local\ 作用域 = 每个登录会话一个实例（多用户各自跑一份，互不干扰）。</summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\DeepSeekHarness.Launcher", out var createdNew);
            ownsSingleInstanceMutex = createdNew;
            return createdNew;
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是被强杀的：互斥体归我们，继续启动。
            ownsSingleInstanceMutex = true;
            return true;
        }
        catch
        {
            // 拿不到互斥体不该拦住用户启动。
            return true;
        }
    }

    /// <summary>
    /// 第二个实例要做的事：把已经在跑的那个窗口提到前台，而不是静默退出——
    /// 否则用户会以为"双击没反应"。
    /// </summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            // Process 对象持有内核句柄，不 Dispose 要等 GC 才释放——这里必须显式收掉，
            // 漏掉的那几个会一直挂在进程表里。
            var candidates = Process.GetProcessesByName("DeepSeekHarness");
            foreach (var process in candidates)
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                    return;
                }
            }
        }
        catch { }
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(
                CrashLogPath,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{ex}\n\n",
                new UTF8Encoding(false));
        }
        catch { }
    }
}
