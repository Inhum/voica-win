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

        // Network diagnostic (spec §9.5, kept documented per §12): hit all three destinations —
        // recognition (§2), the model download (§2.5), the update check (§10) — and print what
        // each of them would tell the user. Behind scripts/fake-proxy.ps1 with VOICA_PROXY set,
        // this is the check that every one of them names the proxy; unit tests cannot see it,
        // and on macOS three of the four surfaces were wrong while the tests stayed green.
        if (args.Contains("--probe-net", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(AttachParentProcess);
            return ProbeNet().GetAwaiter().GetResult();
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
        Console.WriteLine($"rules: fillers={Prefs.RemoveFillers} terms={Prefs.FixTermsByRules} quotes={Prefs.FixQuotes}");
        int lines = 0, changed = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            lines++;
            // The whole rule chain in delivery order (spec §6.3 → §6.2 → §6.4), and it obeys the
            // switches (spec §6.5): a measurement that ignores them shows something other than what
            // the user actually gets.
            var fixedLine = line;
            if (Prefs.RemoveFillers) fixedLine = Fillers.Strip(fixedLine);
            if (Prefs.FixTermsByRules) fixedLine = TermFix.Apply(fixedLine, vocabulary);
            if (Prefs.FixQuotes) fixedLine = Quotes.Balance(fixedLine);
            if (string.Equals(fixedLine, line, StringComparison.Ordinal)) continue;
            changed++;
            Console.WriteLine($"--- {line}");
            Console.WriteLine($"+++ {fixedLine}");
        }
        Console.WriteLine($"{lines} lines, {changed} changed");
        return 0;
    }

    /// <summary>
    /// Walks every network surface once and prints what each would say (spec §9.5). Needs the
    /// saved Groq key to reach the cloud ones; without one they fail on the key, which still
    /// exercises the wording. A real (short, silent) recognition request is sent.
    /// </summary>
    private static async System.Threading.Tasks.Task<int> ProbeNet()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var groq = GroqClient.Endpoint;
        var route = Net.Resolve(groq);
        Console.WriteLine($"route: {route.Source} {(route.Address is null ? "" : route.Address.Host + ":" + route.Address.Port)}");

        var key = KeyStore.Load() ?? "sk-probe-not-a-real-key";

        var validation = await GroqClient.ValidateKeyAsync(key);
        Console.WriteLine($"key check     : {validation.Message}");

        var chat = await GroqClient.CheckChatModelAsync(key);
        Console.WriteLine($"chat model    : {chat.Problem ?? "ok"}");

        try
        {
            var result = await GroqClient.TranscribeAsync(SilenceWav(), key, null);
            Console.WriteLine($"dictation     : ok ({result.Text.Length} chars)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"dictation     : {ex.Message}");
        }

        var update = await Updater.CheckAsync();
        Console.WriteLine($"update check  : {update.Message ?? update.Outcome.ToString()}");

        try
        {
            // Headers only, and on the release URL itself: DownloadAsync skips files already on
            // disk, so on a machine with the model installed it would print "ok" without ever
            // touching the network — a green light that proves nothing.
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var head = await Net.Shared.GetAsync(ModelManager.ReleaseUri,
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Console.WriteLine($"model download: ok (HTTP {(int)head.StatusCode})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"model download: {Net.Describe(ex, ModelManager.ReleaseUri)}");
        }
        return 0;
    }

    /// <summary>A second of silence as a WAV file, so the probe can post a real request.</summary>
    private static string SilenceWav()
    {
        var path = Path.Combine(Path.GetTempPath(), "voica-probe.wav");
        const int rate = 16000, seconds = 1;
        int data = rate * 2 * seconds;
        using var w = new BinaryWriter(File.Create(path));
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data);
        w.Write(new byte[data]);
        return path;
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
