using System;
using System.IO;

namespace Voica;

/// <summary>
/// Minimal append-only diagnostic log at %APPDATA%\Voica\voica.log. No telemetry, local only
/// (spec §12). Best-effort: logging never throws into the caller.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(Paths.DataDir, "voica.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            Paths.EnsureCreated();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(FilePath, line);
            }
        }
        catch
        {
            // Never let logging break the app.
        }
    }
}
