using System;
using System.Windows.Forms;

namespace DeepSeekHarness;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new HarnessForm());
    }
}
