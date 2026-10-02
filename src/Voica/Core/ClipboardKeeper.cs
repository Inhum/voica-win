using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Voica;

/// <summary>
/// Lends the clipboard to a dictation and gives it back (spec §5, issue #2).
///
/// Inserting text into somebody else's field goes through the clipboard — there is no other
/// transport that works in an arbitrary application — so every dictation used to destroy whatever
/// the person had copied. Here the previous contents are copied out first, the dictation is put in
/// for the paste, and a moment later the previous contents are put back.
///
/// Windows has no "push and pop" for the clipboard: saving it means copying the bytes of every
/// format into our own memory (never to disk), and what comes back is a SNAPSHOT — we become the
/// owner, so live links to the source application (paste-link, cut mode) are gone. That is why the
/// answer to "can it be kept?" is sometimes no, and every such case falls back to the old behaviour:
/// the dictation simply stays in the clipboard.
///
/// Call on the UI (STA) thread.
/// </summary>
public static class ClipboardKeeper
{
    /// <summary>Above this the previous contents are not saved — a 4K screenshot is ~30 MB per format.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// How long READING the contents may take. Applications such as Excel render their formats on
    /// demand, and asking for all of them holds up the insert — the person is waiting for their text.
    /// ⚠️ Only the reading is timed. The first measurement also counted creating the owner window and
    /// opening the clipboard, and in a cold process that alone was 350–500 ms: plain text of a hundred
    /// bytes was reported as "over budget" and would never have been kept.
    /// The number is measured, not guessed: text, HTML, files and a full-HD screenshot read in
    /// 8–30 ms; a Word table with all 14 formats takes 130–180 ms once Word is warm (1.4 s the very
    /// first time after Word starts — that one dictation falls back). 200 ms sat right on Word's
    /// figure, so the same copy would be kept one time and not the next.
    /// </summary>
    public static readonly TimeSpan CaptureBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>Environment override for the restore delay, for tuning against real applications.</summary>
    public const string DelayVariable = "VOICA_CLIPBOARD_RESTORE_MS";

    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteDelay = TimeSpan.FromSeconds(5);

    // --- what can be saved (pure logic, covered by the self-test) ---

    private const uint CF_TEXT = 1, CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_OEMTEXT = 7, CF_DIB = 8,
        CF_PALETTE = 9, CF_UNICODETEXT = 13, CF_ENHMETAFILE = 14, CF_DIBV5 = 17;

    /// <summary>
    /// Contents handed over as a stream, not as memory (an Outlook attachment, a file inside an
    /// archive): they cannot be read with GetClipboardData, and a descriptor restored without its
    /// contents would be a paste that fails. Nothing is saved when one of these is present.
    /// </summary>
    private static readonly HashSet<string> VirtualFileFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileGroupDescriptorW", "FileGroupDescriptor", "FileContents",
    };

    /// <summary>
    /// Formats deliberately not put back. The rule for everything else: return what the SYSTEM
    /// itself leaves on the clipboard when the source application closes — every format that can be
    /// read, the OLE ones included (<c>Object Descriptor</c>, <c>Embed Source</c>, <c>Link Source</c>,
    /// <c>Ole Private Data</c>…). The first version dropped those as "links to a live object" and
    /// returned 5 of Word's 14 formats.
    /// <c>DataObject</c> alone stays out: it is the window handle of the source's live data object,
    /// and once the clipboard is ours it names nothing.
    /// ⚠️ What this cannot do: make the source believe the clipboard is still its own. Word treats
    /// anything it does not currently own as "from another program" — measured: even a table copied
    /// by a Word that then simply exited is pasted by another Word under that rule. With Word's
    /// default settings the formatting survives; with "Pasting from other programs: Keep Text Only"
    /// it does not, and no restorer can change that.
    /// Settable only so the diagnostic can try other choices against a real application
    /// (<c>--probe-clipboard --leave-out "A;B"</c>).
    /// </summary>
    public static HashSet<string> LeftOut { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "DataObject" };

    /// <summary>
    /// Formats that carry no data, only a meaning by being present ("do not record this" — set by
    /// password managers among others). Some are placed without a readable handle; they are put back
    /// regardless, because losing one would send a password into the clipboard history.
    /// </summary>
    private static readonly HashSet<string> MarkerFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard", "Clipboard Viewer Ignore",
    };

    public static bool IsMarker(string? formatName) =>
        !string.IsNullOrEmpty(formatName) && MarkerFormats.Contains(formatName);

    /// <summary>
    /// Formats that are routinely announced with nothing behind them. Every rich-edit control
    /// (WordPad, Word, Outlook and anything built on RichEdit) lists
    /// <c>EnterpriseDataProtectionId</c>, and outside a managed enterprise there is no id to hand
    /// over. Being unable to read one of these is normal, not a hole in the snapshot — treating it
    /// as one refused every copy of formatted text, which the first live run showed.
    /// </summary>
    private static readonly HashSet<string> OptionalFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnterpriseDataProtectionId",
    };

    public static bool IsOptional(string? formatName) =>
        !string.IsNullOrEmpty(formatName) && OptionalFormats.Contains(formatName);

    /// <summary>The outcome of looking at what is on the clipboard.</summary>
    public sealed record Plan(bool Possible, IReadOnlyList<uint> Capture, string? Reason);

    /// <summary>
    /// Decides which of the formats on the clipboard (in enumeration order) get copied. Skipped:
    /// handles that are not memory (bitmaps, metafiles, palettes, private and GDI ranges), formats the
    /// system synthesizes from another one that IS copied (ANSI text from Unicode, the second DIB
    /// flavour), and links to live objects.
    /// </summary>
    public static Plan PlanCapture(IReadOnlyList<uint> formats, Func<uint, string?> nameOf)
    {
        if (formats.Count == 0) return new Plan(true, Array.Empty<uint>(), null);

        foreach (var f in formats)
            if (nameOf(f) is { } name && VirtualFileFormats.Contains(name))
                return new Plan(false, Array.Empty<uint>(), "virtual files (contents come as a stream)");

        bool hasUnicode = formats.Contains(CF_UNICODETEXT);
        bool dibTaken = false;
        var capture = new List<uint>();
        foreach (var f in formats)
        {
            // An enhanced metafile is a handle, not memory, but it has its own way of being copied
            // (see ReadHandle); the old-style metafile and the bitmap are made from their twins.
            if (f is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE) continue;
            if (f is >= 0x80 and <= 0x8E) continue;          // owner-display and display formats
            if (f is >= 0x200 and <= 0x3FF) continue;        // private and GDI-object ranges
            if (hasUnicode && f is CF_TEXT or CF_OEMTEXT) continue;
            if (f is CF_DIB or CF_DIBV5)
            {
                if (dibTaken) continue;                      // the system makes the other from the first
                dibTaken = true;
            }
            if (nameOf(f) is { } name && LeftOut.Contains(name)) continue;
            capture.Add(f);
        }

        return capture.Count == 0
            ? new Plan(false, capture, "nothing on the clipboard can be copied as memory")
            : new Plan(true, capture, null);
    }

    /// <summary>Viewers of remote sessions: the paste has to travel a connection before it is read.</summary>
    private static readonly HashSet<string> RemoteViewers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc", "msrdc", "wfica32", "CDViewer", "vmware-view", "vmware-remotemks", "AnyDesk",
        "TeamViewer", "vncviewer", "tvnviewer", "RustDesk", "parsecd",
    };

    public static bool IsRemoteViewer(string? processName) =>
        !string.IsNullOrEmpty(processName) && RemoteViewers.Contains(processName);

    /// <summary>
    /// How long to wait before putting the previous contents back. Too short and the application
    /// reads the clipboard after the swap — the worst failure, it pastes the OLD contents instead of
    /// the dictation. The override wins, then the remote-session delay, then the default.
    /// </summary>
    public static TimeSpan RestoreDelay(string? overrideMs, string? foregroundProcess)
    {
        if (int.TryParse(overrideMs, out var ms) && ms is >= 50 and <= 60000)
            return TimeSpan.FromMilliseconds(ms);
        return IsRemoteViewer(foregroundProcess) ? RemoteDelay : DefaultDelay;
    }

    // --- snapshot ---

    /// <summary>The previous contents: every copied format with its bytes, held in memory only.</summary>
    public sealed class Snapshot
    {
        internal List<(uint Format, byte[] Data)> Items { get; } = new();
        public long Bytes { get; internal set; }
        public int Formats => Items.Count;
        /// <summary>Time spent reading the formats — what <see cref="CaptureBudget"/> limits.</summary>
        public TimeSpan Took { get; internal set; }
        /// <summary>Time spent getting hold of the clipboard first; not part of the budget.</summary>
        public TimeSpan OpenTook { get; internal set; }
        /// <summary>
        /// Only in the diagnostic: why a dictation would NOT keep these contents although they were
        /// read. Outside the diagnostic such a snapshot is never returned.
        /// </summary>
        public string? Problem { get; internal set; }
        internal bool Has(uint format) => Items.Any(i => i.Format == format);
    }

    /// <summary>
    /// True when everything <paramref name="before"/> held is in <paramref name="after"/> byte for
    /// byte. The reverse is not required: a returned clipboard also carries the history markers.
    /// </summary>
    public static bool SameContents(Snapshot before, Snapshot after) =>
        before.Items.All(b => after.Items.Any(a => a.Format == b.Format && a.Data.AsSpan().SequenceEqual(b.Data)));

    /// <summary>One line per format on the clipboard, for <c>--probe-clipboard</c>.</summary>
    public sealed record FormatInfo(uint Id, string Name, long Bytes, string Status);

    /// <summary>
    /// Copies the clipboard's contents out. Null — with the reason — when they cannot be kept: the
    /// source application does not answer, too big, too slow, virtual files, a format that cannot be
    /// read, or the clipboard could not be opened.
    /// </summary>
    /// <param name="report">
    /// Diagnostic mode: every format is listed, and contents that were read but would not be kept
    /// come back with <see cref="Snapshot.Problem"/> set instead of as null.
    /// </param>
    public static Snapshot? TryCapture(out string? reason, List<FormatInfo>? report = null)
    {
        bool diagnostic = report is not null;
        var opening = Stopwatch.StartNew();

        // ⚠️ Before reading anything: is the application that owns the clipboard answering? Formats
        // rendered on demand are produced by the owner when asked, and GetClipboardData WAITS for
        // it — measured at 15 s per format against an owner that was not pumping messages, 90 s for
        // one rich-text copy. A dictation must never hang on somebody else's frozen window, so an
        // owner that does not answer a no-op message means the contents are not read at all.
        var owner = GetClipboardOwner();
        if (owner != IntPtr.Zero && owner != _owner
            && SendMessageTimeout(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, OwnerPingMs, out _) == IntPtr.Zero)
        {
            reason = "the application that owns the clipboard is not responding";
            return null;
        }

        if (!Open()) { reason = HeldBy(); return null; }
        opening.Stop();
        var clock = Stopwatch.StartNew();
        try
        {
            var formats = new List<uint>();
            for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f)) formats.Add(f);

            var plan = PlanCapture(formats, NameOf);
            if (diagnostic)
                foreach (var f in formats.Where(f => !plan.Capture.Contains(f)))
                    report!.Add(new FormatInfo(f, DisplayName(f), 0, "skipped"));
            if (!plan.Possible) { reason = plan.Reason; return null; }

            var snapshot = new Snapshot();
            var unreadable = new List<string>();
            foreach (var format in plan.Capture)
            {
                var handle = GetClipboardData(format);      // may make the owner render the format now
                if (clock.Elapsed > CaptureBudget)
                {
                    snapshot.Problem ??= $"reading took over {CaptureBudget.TotalMilliseconds:F0} ms (the source renders on demand)";
                    if (!diagnostic) { reason = snapshot.Problem; return null; }
                }

                var bytes = ReadHandle(format, handle, out long size);
                if (size > MaxBytes || snapshot.Bytes + size > MaxBytes)
                {
                    reason = $"over {MaxBytes / (1024 * 1024)} MB";
                    report?.Add(new FormatInfo(format, DisplayName(format), size, "over the limit"));
                    return null;
                }
                if (bytes is null)
                {
                    // A marker means something by being there; anything else that cannot be read
                    // would come back missing, and a clipboard returned with holes is worse than one
                    // honestly left alone — so that is a reason not to keep it.
                    if (IsMarker(NameOf(format)))
                    {
                        snapshot.Items.Add((format, new byte[4]));
                        report?.Add(new FormatInfo(format, DisplayName(format), 0, "marker"));
                    }
                    else if (IsOptional(NameOf(format)))
                    {
                        report?.Add(new FormatInfo(format, DisplayName(format), 0, "absent (ok)"));
                    }
                    else
                    {
                        unreadable.Add(DisplayName(format));
                        report?.Add(new FormatInfo(format, DisplayName(format), 0, "NOT READABLE"));
                    }
                    continue;
                }

                snapshot.Items.Add((format, bytes));
                snapshot.Bytes += size;
                report?.Add(new FormatInfo(format, DisplayName(format), size, "saved"));
            }

            if (unreadable.Count > 0)
            {
                snapshot.Problem ??= $"could not read {string.Join(", ", unreadable)}";
                if (!diagnostic) { reason = snapshot.Problem; return null; }
            }
            snapshot.Took = clock.Elapsed;
            snapshot.OpenTook = opening.Elapsed;
            reason = null;
            return snapshot;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// Puts the dictation on the clipboard for the paste, marked so that clipboard history (Win+V),
    /// cloud sync and clipboard managers leave it alone — it is there for half a second. Returns the
    /// clipboard's sequence number afterwards, or 0 on failure.
    /// </summary>
    public static uint SetTemporaryText(string text)
    {
        if (!Open()) return 0;
        try
        {
            if (!EmptyClipboard()) return 0;
            if (!Put(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"))) return 0;
            PutTransientMarkers(skipIfPresent: null);
        }
        finally { CloseClipboard(); }
        return GetClipboardSequenceNumber();
    }

    /// <summary>
    /// Puts the previous contents back — unless the clipboard changed since the dictation was put
    /// there, which means the person copied something new in the meantime and that must stay.
    /// </summary>
    public static bool Restore(Snapshot snapshot, uint expectedSequence, out string? reason)
    {
        if (GetClipboardSequenceNumber() != expectedSequence)
        {
            reason = "the clipboard changed after the insert — leaving the newer contents";
            return false;
        }
        if (!Open()) { reason = HeldBy(); return false; }
        try
        {
            if (!EmptyClipboard()) { reason = "the clipboard could not be emptied"; return false; }
            foreach (var (format, data) in snapshot.Items)
                Put(format, data);
            // The contents were in the history once already, when the person copied them.
            if (snapshot.Items.Count > 0) PutTransientMarkers(skipIfPresent: snapshot);
        }
        finally { CloseClipboard(); }
        reason = null;
        return true;
    }

    // --- one dictation's loan, with the restore scheduled ---

    private static Snapshot? _held;
    private static uint _heldSequence;
    private static DispatcherTimer? _timer;

    /// <summary>
    /// Saves the clipboard and puts the dictation there. False — with the reason — when the previous
    /// contents cannot be kept; the caller then delivers the old way.
    /// </summary>
    public static bool Lend(string text, out string? reason)
    {
        // A second dictation before the first loan was returned: the clipboard still holds the first
        // dictation, so what must come back is the ORIGINAL contents, not that.
        var snapshot = _held is not null && GetClipboardSequenceNumber() == _heldSequence ? _held : null;
        _timer?.Stop();
        _held = null;

        reason = null;
        snapshot ??= TryCapture(out reason);
        if (snapshot is null) { reason ??= "unknown"; return false; }

        var sequence = SetTemporaryText(text);
        if (sequence == 0) { reason = "the clipboard could not be written"; return false; }

        _held = snapshot;
        _heldSequence = sequence;
        reason = null;
        return true;
    }

    /// <summary>Returns the loan after the delay; call right after the paste was sent.</summary>
    public static void GiveBackLater(Dispatcher dispatcher)
    {
        if (_held is null) return;
        var delay = RestoreDelay(Environment.GetEnvironmentVariable(DelayVariable), ForegroundProcessName());
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            var snapshot = _held;
            _held = null;
            if (snapshot is null) return;
            if (Restore(snapshot, _heldSequence, out var why))
                Log.Info($"clipboard: previous contents back after {delay.TotalMilliseconds:F0} ms ({snapshot.Formats} formats, {snapshot.Bytes / 1024} KB)");
            else
                Log.Info($"clipboard: not restored — {why}");
        };
        _timer.Start();
    }

    // --- Win32 ---

    private static IntPtr _owner;

    /// <summary>
    /// Creates the owner window ahead of the first dictation, so that its cost (a few hundred
    /// milliseconds in a cold process) is not paid while the person waits for their text.
    /// </summary>
    public static void Warm() => Owner();

    /// <summary>
    /// SetClipboardData needs an owner window: opened with a null handle the clipboard has no owner
    /// after EmptyClipboard and refuses data. A message-only window is enough.
    /// </summary>
    private static IntPtr Owner()
    {
        if (_owner != IntPtr.Zero) return _owner;
        var source = new HwndSource(new HwndSourceParameters("VoicaClipboard")
        {
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE
            WindowStyle = 0,
        });
        return _owner = source.Handle;
    }

    private static bool Open()
    {
        var owner = Owner();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(40);   // another application has it open — they let go quickly
        }
        return false;
    }

    /// <summary>
    /// The bytes behind a clipboard handle, or null when there is nothing to read. Memory handles are
    /// copied as they are; an enhanced metafile (a chart or drawing copied as a picture) is a GDI
    /// handle and is serialized with its own API. Anything over <see cref="MaxBytes"/> is sized but
    /// not copied.
    /// </summary>
    private static byte[]? ReadHandle(uint format, IntPtr handle, out long size)
    {
        size = 0;
        if (handle == IntPtr.Zero) return null;
        if (format == CF_ENHMETAFILE)
        {
            uint length = GetEnhMetaFileBits(handle, 0, null);
            size = length;
            if (length == 0 || length > MaxBytes) return null;
            var picture = new byte[length];
            return GetEnhMetaFileBits(handle, length, picture) == length ? picture : null;
        }

        size = (long)GlobalSize(handle);
        if (size <= 0 || size > MaxBytes) return null;
        var source = GlobalLock(handle);
        if (source == IntPtr.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(source, data, 0, (int)size);
            return data;
        }
        finally { GlobalUnlock(handle); }
    }

    /// <summary>Why the clipboard could not be opened — naming whoever has it open, when that can be told.</summary>
    private static string HeldBy()
    {
        try
        {
            GetWindowThreadProcessId(GetOpenClipboardWindow(), out var pid);
            if (pid != 0) return $"the clipboard is held open by {Process.GetProcessById((int)pid).ProcessName}";
        }
        catch { /* the holder went away while we were asking */ }
        return "the clipboard is held by another application";
    }

    private static bool Put(uint format, byte[] data)
    {
        if (format == CF_ENHMETAFILE)
        {
            var picture = SetEnhMetaFileBits((uint)data.Length, data);
            if (picture == IntPtr.Zero) return false;
            if (SetClipboardData(format, picture) != IntPtr.Zero) return true;
            DeleteEnhMetaFile(picture);
            return false;
        }

        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return false;
        var target = GlobalLock(handle);
        if (target == IntPtr.Zero) { GlobalFree(handle); return false; }
        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) != IntPtr.Zero) return true;   // the system owns it now
        GlobalFree(handle);
        return false;
    }

    private static void PutTransientMarkers(Snapshot? skipIfPresent)
    {
        var zero = BitConverter.GetBytes(0);
        foreach (var name in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            var format = RegisterClipboardFormat(name);
            if (format != 0 && skipIfPresent?.Has(format) != true) Put(format, zero);
        }
    }

    private static string? NameOf(uint format)
    {
        if (format < 0xC000) return null;   // only registered formats have names
        var buffer = new StringBuilder(256);
        return GetClipboardFormatName(format, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    private static string DisplayName(uint format) => NameOf(format) ?? format switch
    {
        1 => "CF_TEXT", 2 => "CF_BITMAP", 3 => "CF_METAFILEPICT", 7 => "CF_OEMTEXT", 8 => "CF_DIB",
        9 => "CF_PALETTE", 13 => "CF_UNICODETEXT", 14 => "CF_ENHMETAFILE", 15 => "CF_HDROP",
        16 => "CF_LOCALE", 17 => "CF_DIBV5", _ => $"0x{format:X}",
    };

    private static string? ForegroundProcessName()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            return pid == 0 ? null : Process.GetProcessById((int)pid).ProcessName;
        }
        catch { return null; }
    }

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint WM_NULL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    /// <summary>How long the clipboard's owner gets to answer a no-op message. A live window answers in a few ms.</summary>
    private const uint OwnerPingMs = 150;

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("gdi32.dll")] private static extern uint GetEnhMetaFileBits(IntPtr hemf, uint cbBuffer, byte[]? lpbBuffer);
    [DllImport("gdi32.dll")] private static extern IntPtr SetEnhMetaFileBits(uint cbBuffer, byte[] lpData);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr hemf);
}
