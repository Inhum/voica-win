using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Voica;

/// <summary>
/// Local-engine model lifecycle (spec §2.5): download-on-demand from this repo's dedicated
/// model release (progress + SHA-256 verification), storage under %APPDATA%\Voica\models\,
/// and deletion. The cloud engine keeps working while (or instead of) this.
/// </summary>
public static class ModelManager
{
    /// <summary>Dedicated model-release tag in Inhum/voica-win (never "latest").</summary>
    public const string ReleaseTag = "model-gigaam-v3-e2e-ctc-int8-1";

    /// <summary>Engine/model name recorded in history's model column.</summary>
    public const string ModelName = "gigaam-v3-e2e-ctc-int8";

    public sealed record ModelFile(string FileName, string Sha256, long Size);

    public static readonly ModelFile[] Files =
    {
        new("v3_e2e_ctc.int8.onnx", "2e3fcb7a7b66030336fd10c2fcfb033bd1dc7e1bf238fe5cfd83b1d0cfc9d28e", 224_893_347),
        new("v3_e2e_ctc.yaml", "e67eca3a311ad7c8813d36dff6b8eeba7ad3459fd811d6faea2a26535754a358", 899),
        new("v3_e2e_ctc_vocab.txt", "142de7570b3de5b3035ce111a89c228e80e6085273731d944093ddf24fa539cd", 2_007),
    };

    public static string ModelsDir => Path.Combine(Paths.DataDir, "models");
    public static string PathFor(string fileName) => Path.Combine(ModelsDir, fileName);
    public static string OnnxPath => PathFor(Files[0].FileName);
    public static string VocabPath => PathFor(Files[2].FileName);

    /// <summary>Where the files come from — also what a proxy failure has to be reported against.</summary>
    public static Uri ReleaseUri => new(DownloadUrl(Files[0].FileName));

    private static string DownloadUrl(string fileName) =>
        $"https://github.com/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/download/{ReleaseTag}/{fileName}";

    // The shared client, so the proxy setting applies here too (spec §9.5) — a corporate proxy
    // wanting credentials is the most common reason the local engine "cannot be installed".
    private static HttpClient Http => Net.Shared;

    /// <summary>True when every model file is present with the right size.</summary>
    public static bool IsInstalled()
    {
        foreach (var f in Files)
        {
            var fi = new FileInfo(PathFor(f.FileName));
            if (!fi.Exists || fi.Length != f.Size) return false;
        }
        return true;
    }

    /// <summary>Total download size in bytes (for UI).</summary>
    public static long TotalSize
    {
        get
        {
            long sum = 0;
            foreach (var f in Files) sum += f.Size;
            return sum;
        }
    }

    /// <summary>
    /// Downloads and verifies all model files. Progress is overall 0..1. Files are fetched to a
    /// .part file, SHA-256-verified, then moved into place — an interrupted download never leaves
    /// a corrupt "installed" state.
    /// </summary>
    public static async Task DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsDir);
        long total = TotalSize;
        long doneBase = 0;

        foreach (var file in Files)
        {
            var finalPath = PathFor(file.FileName);
            var fi = new FileInfo(finalPath);
            if (fi.Exists && fi.Length == file.Size)
            {
                doneBase += file.Size;
                progress?.Report((double)doneBase / total);
                continue;
            }

            var partPath = finalPath + ".part";
            try
            {
                using var response = await Http.GetAsync(DownloadUrl(file.FileName),
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(partPath);
                var buffer = new byte[1 << 16];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    progress?.Report((doneBase + (double)written) / total);
                }
            }
            catch (OperationCanceledException)
            {
                // Changed their mind part-way. There is no resume yet (§9.5 keeps it as a separate
                // item), so what is on disk is worth nothing — and leaving 200 MB of it in the data
                // folder, invisible in the UI, is not a kindness. The stream is closed by the time
                // we get here, so the file can go.
                TryDelete(partPath);
                throw;
            }

            var sha = await ComputeSha256Async(partPath, cancellationToken);
            if (!sha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partPath);
                throw new InvalidDataException($"SHA-256 mismatch for {file.FileName} — download corrupted.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            doneBase += file.Size;
            progress?.Report((double)doneBase / total);
        }

        MarkVerified();
        Log.Info("local model downloaded and verified");
    }

    /// <summary>Deletes the downloaded model files (frees disk; cloud keeps working, spec §2.5).</summary>
    public static void Delete()
    {
        try
        {
            if (Directory.Exists(ModelsDir))
                foreach (var f in Directory.EnumerateFiles(ModelsDir))
                    TryDelete(f);
            Log.Info("local model deleted");
        }
        catch (Exception ex)
        {
            Log.Error("model delete failed", ex);
        }
    }

    /// <summary>Outcome of checking the installed model against its published checksums.</summary>
    public enum ModelState { Ok, Missing, Corrupt }

    /// <summary>The record of a passed check, so 215 MB are not re-hashed on every dictation.</summary>
    private static string MarkerPath => PathFor("verified.json");

    /// <summary>One file as it was when the checksums last passed: name, size, modification time.</summary>
    public sealed record VerifiedFile(string Name, long Size, long Modified);

    /// <summary>
    /// Checks the installed model against the checksums published with it, and remembers the
    /// answer (spec §2.5 + the manual-install path in the README).
    ///
    /// Two reasons this exists. A model that arrived by hand — copied in on a USB stick, because
    /// the network will not let 215 MB through — has never been verified by anything; a truncated
    /// copy otherwise surfaces as gibberish recognition, which is the worst way to find out.
    /// And ⚠️ the answer has to be remembered: hashing 215 MB before every dictation would add a
    /// second of silence to each one. The marker records size and modification time, so touching
    /// any of the files sends them back through the hash.
    /// </summary>
    public static ModelState Verify()
    {
        if (!IsInstalled()) return ModelState.Missing;
        if (MarkerMatches()) return ModelState.Ok;

        foreach (var f in Files)
        {
            var actual = ComputeSha256(PathFor(f.FileName));
            if (!actual.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error($"model file {f.FileName} fails its checksum (got {actual})", null);
                return ModelState.Corrupt;
            }
        }

        MarkVerified();
        Log.Info("local model verified against its checksums");
        return ModelState.Ok;
    }

    /// <summary>
    /// Records that the files on disk have been checked. Called after a download too: those parts
    /// were hashed on the way in, and re-hashing them at first use would be pure waiting.
    /// </summary>
    public static void MarkVerified()
    {
        try
        {
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(OnDisk()));
        }
        catch (Exception ex)
        {
            // Not fatal: without the marker the check simply runs again next time.
            Log.Error("could not write the model verification marker", ex);
        }
    }

    private static bool MarkerMatches()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return false;
            return MarkerCovers(JsonSerializer.Deserialize<VerifiedFile[]>(File.ReadAllText(MarkerPath)), OnDisk());
        }
        catch { return false; }
    }

    /// <summary>The three files as they are right now.</summary>
    private static VerifiedFile[] OnDisk() => Files.Select(f =>
    {
        var fi = new FileInfo(PathFor(f.FileName));
        return new VerifiedFile(f.FileName, fi.Exists ? fi.Length : -1, fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1);
    }).ToArray();

    /// <summary>
    /// Whether a saved marker still describes the files on disk. Every file has to be named and
    /// unchanged: a marker that covers two of three files out of the box would let a swapped
    /// third one through unverified.
    /// </summary>
    public static bool MarkerCovers(VerifiedFile[]? saved, VerifiedFile[] actual)
    {
        if (saved is null || saved.Length != actual.Length) return false;
        foreach (var a in actual)
        {
            var entry = saved.FirstOrDefault(e => e.Name == a.Name);
            if (entry is null || entry.Size != a.Size || entry.Modified != a.Modified) return false;
        }
        return true;
    }

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
