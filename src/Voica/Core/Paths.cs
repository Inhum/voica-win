using System;
using System.IO;

namespace Voica;

/// <summary>
/// Application data locations, kept outside the executable so they survive updates
/// (spec §7, §9): %APPDATA%\Voica\ with history.sqlite, audio\, credentials.dat.
/// </summary>
public static class Paths
{
    /// <summary>%APPDATA%\Voica</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voica");

    /// <summary>%APPDATA%\Voica\audio</summary>
    public static string AudioDir { get; } = Path.Combine(DataDir, "audio");

    /// <summary>%APPDATA%\Voica\history.sqlite</summary>
    public static string DatabaseFile { get; } = Path.Combine(DataDir, "history.sqlite");

    /// <summary>%APPDATA%\Voica\credentials.dat (DPAPI-protected Groq key).</summary>
    public static string CredentialsFile { get; } = Path.Combine(DataDir, "credentials.dat");

    /// <summary>%APPDATA%\Voica\settings.json</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");

    /// <summary>Creates the data directories if missing. Safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AudioDir);
    }
}
