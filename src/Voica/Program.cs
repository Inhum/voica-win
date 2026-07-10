using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace Voica;

/// <summary>
/// Custom entry point. The <c>--test-all</c> self-test (spec §12) runs here before any
/// WPF/GUI or network initialization, so it is safe to run headless in CI.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--test-all", StringComparer.OrdinalIgnoreCase))
        {
            // This is a GUI-subsystem (WinExe) app, so attach to the launching console
            // to make self-test output visible when run from a terminal / CI.
            AttachConsole(AttachParentProcess);
            return SelfTest.Run() ? 0 : 1;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
