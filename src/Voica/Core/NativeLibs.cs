using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Voica;

/// <summary>
/// Keeps the app's own native libraries on disk for as long as it runs.
///
/// A single-file build unpacks its native DLLs (onnxruntime, SQLite, the WPF ones) into
/// <c>%TEMP%\.net\Voica\…</c> at start and loads each one only when it is first needed. Anything
/// that tidies the temp folder — Windows Storage Sense does, by default on many machines — deletes
/// the ones not loaded yet, because nothing holds them. The local engine loads onnxruntime at the
/// first local dictation, which can be days after start.
///
/// What happened then, on the owner's machine after two days of uptime: the loader did not find
/// <c>onnxruntime.dll</c> beside the app, went on down the search path and found the 3 KB stub of
/// the same name that Windows keeps in System32. The dictation was recorded, and failed with "The
/// type initializer for 'Microsoft.ML.OnnxRuntime.NativeMethods' threw an exception" — a message that
/// tells a person nothing, for a recording that was then thrown away.
///
/// So every unpacked file is held open, without permission to delete, until the app exits. A clean-up
/// then skips them like any file in use. The files are opened, not loaded: nothing of theirs runs
/// before it is needed. On the next start the runtime restores whatever is missing by itself.
/// </summary>
public static class NativeLibs
{
    private static readonly List<FileStream> Held = new();

    /// <summary>
    /// The directories whose files must outlive a clean-up: where the loader looks for native
    /// libraries, minus the app's own folder (an installed file is nobody's temp file) and minus
    /// anything under Windows.
    /// </summary>
    public static IReadOnlyList<string> UnpackDirs(string? nativeSearchDirs, string appDir, string windowsDir)
    {
        static string Norm(string p) => p.TrimEnd('\\', '/');
        return (nativeSearchDirs ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Norm)
            .Where(d => d.Length > 0
                && !string.Equals(d, Norm(appDir), StringComparison.OrdinalIgnoreCase)
                && !(d + "\\").StartsWith(Norm(windowsDir) + "\\", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Holds every unpacked file open. Call once, early; safe to call when there is nothing to hold.</summary>
    public static void Pin()
    {
        try
        {
            var dirs = UnpackDirs(AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string,
                AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            int held = 0;
            foreach (var dir in dirs.Where(Directory.Exists))
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        // Others may read (the loader must), nobody may delete or write.
                        lock (Held) Held.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                        held++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"native libraries: could not hold {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
            if (held > 0) Log.Info($"native libraries: holding {held} unpacked files against temp clean-up");
        }
        catch (Exception ex)
        {
            Log.Error("native libraries: pinning failed", ex);
        }
    }

    /// <summary>
    /// True when an error means "one of our own native libraries is gone or was replaced by a
    /// stranger of the same name": not found, or found without the entry point we call. Looks
    /// through the wrappers the runtime puts around it (a type initializer, an aggregate).
    /// </summary>
    public static bool IsMissingNativeLibrary(Exception? ex)
    {
        for (int depth = 0; ex is not null && depth < 8; depth++, ex = ex.InnerException)
            if (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                return true;
        return false;
    }
}
