using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Voica;

/// <summary>A finished recording: the WAV file path and its duration.</summary>
public sealed record Recording(string FilePath, double DurationSeconds);

/// <summary>
/// Microphone recorder (spec §3): mono, 16 kHz PCM WAV via NAudio. Recordings shorter than
/// 0.3 s are treated as an accidental key press — the file is discarded and nothing is sent.
/// </summary>
public sealed class Recorder : IDisposable
{
    public const double MinDurationSeconds = 0.3;

    private static readonly WaveFormat Format = new(rate: 16000, bits: 16, channels: 1);

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _filePath;
    private DateTime _startUtc;
    private TaskCompletionSource<bool>? _stopped;
    private double _level;
    private DateTime? _firstSampleUtc;
    private long _lastSampleTicks;
    private volatile bool _stopRequested;
    private volatile bool _meterWanted;
    private volatile bool _captureLostReported;
    private System.Threading.Timer? _watchdog;

    /// <summary>
    /// How long the device may go without handing over a buffer before capture counts as dead.
    /// Buffers are 50 ms of PCM and arrive whether or not anyone is speaking, so a gap this long
    /// is a stopped device, never a pause in speech.
    /// </summary>
    private const double SilenceGapSeconds = 3.0;

    /// <summary>
    /// Raised when capture ends on its own while the app still believes it is recording — the
    /// device errored out or simply stopped handing over buffers. The argument is the reason, if
    /// the driver gave one.
    ///
    /// This is not theoretical: a headset once stopped delivering audio 40 seconds into a
    /// 5:48 dictation, and because nothing noticed, the user went on speaking into a dead
    /// microphone for five more minutes. Whatever was captured is still worth transcribing —
    /// but the user has to be told at once, not when they finally press the hotkey.
    /// </summary>
    public event Action<string?>? CaptureLost;

    public bool IsRecording => _waveIn is not null;

    /// <summary>
    /// Current input peak, 0..1, updated on the capture thread — drives the overlay wave (spec §4.2).
    /// Read-only snapshot; 0 while not recording.
    /// </summary>
    public double Level => System.Threading.Volatile.Read(ref _level);

    /// <summary>Begins recording to a fresh temp WAV file. Throws if the mic can't be opened.</summary>
    public void Start()
    {
        if (IsRecording) return;

        Paths.EnsureCreated();
        _filePath = Path.Combine(Paths.AudioDir, $"rec-{Guid.NewGuid():N}.wav");

        _waveIn = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 50 };
        _writer = new WaveFileWriter(_filePath, Format);
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _startUtc = DateTime.UtcNow;
        _firstSampleUtc = null;
        _stopRequested = false;
        _captureLostReported = false;
        // Read once per recording, not per buffer: settings are locked behind a mutex.
        _meterWanted = Prefs.ShowOverlay;
        System.Threading.Volatile.Write(ref _lastSampleTicks, DateTime.UtcNow.Ticks);

        System.Threading.Volatile.Write(ref _level, 0);

        _waveIn.StartRecording();
        _watchdog = new System.Threading.Timer(_ => CheckForSilentDevice(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        // Name the device: when capture misbehaves, the first question is always which mic ran.
        try
        {
            var caps = WaveInEvent.GetCapabilities(_waveIn.DeviceNumber);
            Log.Info($"mic: {caps.ProductName} (device {_waveIn.DeviceNumber})");
        }
        catch { /* diagnostics only */ }
    }

    /// <summary>
    /// Stops recording and returns the file, or null if it was shorter than the 0.3 s floor
    /// (in which case the file is deleted).
    /// </summary>
    public async Task<Recording?> StopAsync()
    {
        if (_waveIn is null) return null;

        var waveIn = _waveIn;
        var stopRequestedUtc = DateTime.UtcNow;
        _stopRequested = true;
        StopWatchdog();
        waveIn.StopRecording();
        await (_stopped?.Task ?? Task.CompletedTask);

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;
        _writer?.Dispose();   // finalizes the RIFF header
        _writer = null;
        waveIn.Dispose();
        _waveIn = null;
        System.Threading.Volatile.Write(ref _level, 0);

        var path = _filePath!;
        _filePath = null;

        double duration = (DateTime.UtcNow - _startUtc).TotalSeconds;
        try
        {
            using var reader = new WaveFileReader(path);
            duration = reader.TotalTime.TotalSeconds;
        }
        catch
        {
            // Fall back to wall-clock duration if the file can't be reopened.
        }

        // A short file has three very different causes, and "no speech" hides all of them: the
        // device started delivering late, it dropped buffers mid-way, or the recording was stopped
        // sooner than the user thinks. Split them apart so the log names the culprit.
        double held = (DateTime.UtcNow - _startUtc).TotalSeconds;
        if (held - duration > 0.75)
        {
            double startLag = _firstSampleUtc is { } first ? (first - _startUtc).TotalSeconds : held;
            double stopCost = (DateTime.UtcNow - stopRequestedUtc).TotalSeconds;
            double midGap = held - duration - startLag - stopCost;
            Log.Error($"mic short: wrote {duration:0.00}s over {held:0.00}s — " +
                      $"first sample after {startLag:0.00}s, stop took {stopCost:0.00}s, unaccounted {midGap:0.00}s");
        }

        if (duration < MinDurationSeconds)
        {
            TryDelete(path);
            return null;
        }

        return new Recording(path, duration);
    }

    /// <summary>Aborts recording and deletes the file (e.g. on shutdown or error).</summary>
    public void Cancel()
    {
        if (_waveIn is null) return;
        var path = _filePath;
        _stopRequested = true;
        StopWatchdog();
        try
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.StopRecording();
        }
        catch { /* best effort */ }
        _writer?.Dispose();
        _writer = null;
        _waveIn.Dispose();
        _waveIn = null;
        _filePath = null;
        System.Threading.Volatile.Write(ref _level, 0);
        if (path is not null) TryDelete(path);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _firstSampleUtc ??= DateTime.UtcNow;
        System.Threading.Volatile.Write(ref _lastSampleTicks, DateTime.UtcNow.Ticks);
        // Writing the buffer to the file is the only thing that HAS to happen here — it is the
        // dictation. It goes first, and everything else is optional work that must never come
        // before it. An exception thrown out of this callback does not just lose one buffer:
        // NAudio stops the capture, and the app records silence for as long as the user keeps
        // talking. So it is swallowed and logged.
        try
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            Log.Error("dropped an audio buffer", ex);
        }

        // The level meter exists for the recording capsule (spec §4.2) and for nothing else. With
        // the capsule turned off, the capture path does no arithmetic at all.
        if (!_meterWanted) return;
        try
        {
            UpdateLevel(e.Buffer, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            Log.Error("level meter failed", ex);
        }
    }

    /// <summary>
    /// Notices a device that stopped handing over buffers without ever saying so — no error, no
    /// RecordingStopped, just silence. Only the gap between buffers gives it away.
    /// </summary>
    private void CheckForSilentDevice()
    {
        if (_waveIn is null || _stopRequested) return;
        var since = DateTime.UtcNow - new DateTime(System.Threading.Volatile.Read(ref _lastSampleTicks), DateTimeKind.Utc);
        if (since.TotalSeconds < SilenceGapSeconds) return;
        ReportCaptureLost($"the device handed over nothing for {since.TotalSeconds:0.0}s");
    }

    /// <summary>Reports the loss once per recording — the watchdog and the driver may both see it.</summary>
    private void ReportCaptureLost(string reason)
    {
        if (_captureLostReported || _stopRequested) return;
        _captureLostReported = true;
        StopWatchdog();
        Log.Error($"capture lost: {reason}");
        CaptureLost?.Invoke(reason);
    }

    private void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    /// <summary>
    /// Loudest sample in a 16-bit little-endian buffer, 0..32768.
    ///
    /// The sample is widened to <c>int</c> BEFORE the sign is dropped, and that is the whole point:
    /// <c>Math.Abs(short.MinValue)</c> throws, because +32768 does not fit in a short. A microphone
    /// produces exactly that sample the moment the input clips — so the peak meter used to throw on
    /// the first loud word, the exception took NAudio's capture thread with it, and the recording
    /// went silent while the user kept talking. That is the 5:48 dictation that yielded 40 seconds.
    /// </summary>
    internal static int PeakAmplitude(byte[] buffer, int bytes)
    {
        int peak = 0;
        for (int i = 0; i + 1 < bytes && i + 1 < buffer.Length; i += 2)
        {
            int sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            int magnitude = sample < 0 ? -sample : sample;
            if (magnitude > peak) peak = magnitude;
        }
        return peak;
    }

    /// <summary>
    /// Tracks the buffer's peak amplitude for the overlay wave: fast attack, slow decay, so the
    /// bars follow speech instead of flickering with every 50 ms buffer.
    /// </summary>
    private void UpdateLevel(byte[] buffer, int bytes)
    {
        double raw = PeakAmplitude(buffer, bytes) / 32768.0;
        double previous = System.Threading.Volatile.Read(ref _level);
        System.Threading.Volatile.Write(ref _level, Math.Max(raw, previous * 0.65));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopped?.TrySetResult(true);
        if (_stopRequested) return;

        // Capture ended without anyone asking. NAudio carries the driver's reason here, and it used
        // to be dropped on the floor — the one piece of evidence for why a dictation went silent.
        if (e.Exception is { } ex) Log.Error("capture stopped by the device", ex);
        ReportCaptureLost(e.Exception?.Message ?? "the device stopped capture without an error");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose() => Cancel();
}
