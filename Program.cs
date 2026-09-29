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

    [STAThread]
    private static void Main()
    {
        // 构造窗体是会抛异常的（读设置文件、提取 exe 图标、加载字体都可能），
        // 而 Main 里没有任何兜底：之前一旦抛出就是"程序闪一下就没了"，
        // 用户拿不到任何可反馈的信息，也没法区分是缺 Node、缺引擎还是别的。
        try
        {
            // 没有单实例保护时，点两次图标会出现两个启动器，然后：
            // 第二个实例的 StopHarnessProcessesAsync 会按命令行正则命中第一个实例的引擎
            // 并整树杀掉（IsHarnessCommand 只排除 DeepSeekHarness.exe，不排除"别人启的 dsh"），
            // 两者还会争同一个 web-url.txt 和 3080 端口，表现为"莫名其妙就断了"。
            if (!TryAcquireSingleInstance())
            {
                ActivateExistingWindow();
                return;
            }

            ApplicationConfiguration.Initialize();
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
            try { singleInstanceMutex?.ReleaseMutex(); } catch { }
            try { singleInstanceMutex?.Dispose(); } catch { }
        }
    }

    /// <summary>Local\ 作用域 = 每个登录会话一个实例（多用户各自跑一份，互不干扰）。</summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\DeepSeekHarness.Launcher", out var createdNew);
            return createdNew;
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是被强杀的：互斥体归我们，继续启动。
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
            foreach (var process in Process.GetProcessesByName("DeepSeekHarness"))
            {
                if (process.Id == Environment.ProcessId) continue;
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
                return;
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
