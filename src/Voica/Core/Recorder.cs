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

    public bool IsRecording => _waveIn is not null;

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

        _waveIn.StartRecording();
    }

    /// <summary>
    /// Stops recording and returns the file, or null if it was shorter than the 0.3 s floor
    /// (in which case the file is deleted).
    /// </summary>
    public async Task<Recording?> StopAsync()
    {
        if (_waveIn is null) return null;

        var waveIn = _waveIn;
        waveIn.StopRecording();
        await (_stopped?.Task ?? Task.CompletedTask);

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;
        _writer?.Dispose();   // finalizes the RIFF header
        _writer = null;
        waveIn.Dispose();
        _waveIn = null;

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
        if (path is not null) TryDelete(path);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) =>
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
        _stopped?.TrySetResult(true);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose() => Cancel();
}
