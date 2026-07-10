using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Voica;

/// <summary>
/// Groq API key storage (spec §9): a DPAPI-protected file (CurrentUser scope) at
/// %APPDATA%\Voica\credentials.dat, never in code or the repo. If the file is absent,
/// falls back to the GROQ_API_KEY environment variable (for development).
/// </summary>
public static class KeyStore
{
    private const string EnvVar = "GROQ_API_KEY";

    /// <summary>Returns the stored key, or the env-var fallback, or null if neither is set.</summary>
    public static string? Load()
    {
        try
        {
            if (File.Exists(Paths.CredentialsFile))
            {
                var protectedBytes = File.ReadAllBytes(Paths.CredentialsFile);
                if (protectedBytes.Length > 0)
                {
                    var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                    var key = Encoding.UTF8.GetString(plain).Trim();
                    if (key.Length > 0) return key;
                }
            }
        }
        catch
        {
            // Unreadable/undecryptable file → treat as no stored key and try the env fallback.
        }

        var env = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>Encrypts and writes the key. An empty/whitespace key deletes the file instead.</summary>
    public static void Save(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            Delete();
            return;
        }

        Paths.EnsureCreated();
        var plain = Encoding.UTF8.GetBytes(key.Trim());
        var protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Paths.CredentialsFile, protectedBytes);
    }

    /// <summary>Removes the stored key file (env fallback, if any, still applies).</summary>
    public static void Delete()
    {
        try
        {
            if (File.Exists(Paths.CredentialsFile)) File.Delete(Paths.CredentialsFile);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>True if a key is available from the file or the env fallback.</summary>
    public static bool HasKey => !string.IsNullOrWhiteSpace(Load());
}
