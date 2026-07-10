using System;
using System.Reflection;

namespace Voica;

/// <summary>
/// Single source of truth for app identity/version, read from the assembly (spec §12).
/// </summary>
public static class AppInfo
{
    public const string Name = "Voica";

    /// <summary>Owner/repo for update checks (spec §10): Windows checks its own repo.</summary>
    public const string RepoOwner = "Inhum";
    public const string RepoName = "voica-win";

    /// <summary>Semver version string, e.g. "0.1.0", from the assembly's informational version.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip build metadata like "+abcdef" that the SDK may append.
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        // Fallback (avoids Assembly.Location, which is empty in a single-file app).
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
