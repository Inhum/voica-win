using System;
using System.IO;
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

        // Rule-check tool (spec §6.2/§6.3/§6.4): run the text rules over a corpus of past
        // dictations and print only the lines they changed. Unit tests do not catch these defects —
        // on macOS 172 of them were green while a run over live dictations found five in a row — so
        // every threshold change has to be justified over the whole history first.
        int corpusArg = Array.FindIndex(args, a => a.Equals("--normalize-corpus", StringComparison.OrdinalIgnoreCase));
        if (corpusArg >= 0)
        {
            AttachConsole(AttachParentProcess);
            return NormalizeCorpus(corpusArg + 1 < args.Length ? args[corpusArg + 1] : null);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int NormalizeCorpus(string? path)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console attached */ }

        if (path is null || !File.Exists(path))
        {
            Console.WriteLine($"usage: Voica.exe --normalize-corpus <file>  (one dictation per line){(path is null ? "" : $"\nnot found: {path}")}");
            return 2;
        }

        var vocabulary = Prefs.Vocabulary;
        Console.WriteLine($"vocabulary: {vocabulary}");
        int lines = 0, changed = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            lines++;
            // The whole rule chain in delivery order (spec §6.3 → §6.2 → §6.4), regardless of the
            // switches: the point is to see what the rules would do, not what is enabled today.
            var fixedLine = Quotes.Balance(TermFix.Apply(Fillers.Strip(line), vocabulary));
            if (string.Equals(fixedLine, line, StringComparison.Ordinal)) continue;
            changed++;
            Console.WriteLine($"--- {line}");
            Console.WriteLine($"+++ {fixedLine}");
        }
        Console.WriteLine($"{lines} lines, {changed} changed");
        return 0;
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
