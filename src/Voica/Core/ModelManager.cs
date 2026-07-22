using System;
using System.IO;
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

    private static string DownloadUrl(string fileName) =>
        $"https://github.com/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/download/{ReleaseTag}/{fileName}";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Voica");
        return http;
    }

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
            using (var response = await Http.GetAsync(DownloadUrl(file.FileName),
                       HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
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
