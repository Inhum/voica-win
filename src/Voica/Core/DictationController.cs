using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Voica;

/// <summary>
/// Orchestrates the dictation loop and state machine (spec §4): idle → recording →
/// transcribing → idle. Wires the hotkey to the recorder, Groq, and text delivery.
/// Lives on the UI thread; its events are raised there.
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly HotkeyManager _hotkey = new();
    private readonly Recorder _recorder = new();
    private readonly Dispatcher _dispatcher;

    private DictationState _state = DictationState.Idle;

    /// <summary>Raised when the state changes (for the tray icon).</summary>
    public event Action<DictationState>? StateChanged;
    /// <summary>Raised with a user-facing error message.</summary>
    public event Action<string>? Error;
    /// <summary>Raised with a low-severity informational message.</summary>
    public event Action<string>? Notice;
    /// <summary>Raised with recognized text when the output mode is Window.</summary>
    public event Action<string>? ResultReady;

    public DictationController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hotkey.Started += OnPttStart;
        _hotkey.Stopped += OnPttStop;
        _hotkey.Toggled += OnToggle;
    }

    public DictationState State => _state;

    /// <summary>Installs the hotkey and applies current settings.</summary>
    public void Start()
    {
        ApplySettings();
        _hotkey.Start();
    }

    /// <summary>Re-reads hotkey mode/key from settings (call after Settings changes).</summary>
    public void ApplySettings()
    {
        _hotkey.Mode = Prefs.Mode;
        _hotkey.Binding = Prefs.Hotkey;
    }

    private void OnPttStart()
    {
        if (_state == DictationState.Idle) BeginRecording();
    }

    private void OnPttStop()
    {
        if (_state == DictationState.Recording) _ = EndRecordingAndTranscribeAsync();
    }

    private void OnToggle()
    {
        if (_state == DictationState.Idle) BeginRecording();
        else if (_state == DictationState.Recording) _ = EndRecordingAndTranscribeAsync();
        // Ignore while transcribing.
    }

    /// <summary>
    /// Manual start/stop from the tray menu (spec §4.1). Always toggle semantics regardless of
    /// the hotkey mode: idle → start, recording → stop, transcribing → ignored.
    /// </summary>
    public void ToggleDictation() => OnToggle();

    private void BeginRecording()
    {
        try
        {
            _recorder.Start();
            SetState(DictationState.Recording);
            Log.Info("recording started");
        }
        catch (Exception ex)
        {
            SetState(DictationState.Idle);
            Log.Error("recording start failed", ex);
            RaiseError(string.Format(S.ErrRecordingStartFmt, ex.Message));
        }
    }

    private async Task EndRecordingAndTranscribeAsync()
    {
        SetState(DictationState.Transcribing);

        Recording? recording;
        try
        {
            recording = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            SetState(DictationState.Idle);
            Log.Error("recording stop failed", ex);
            RaiseError(string.Format(S.ErrRecordingFailedFmt, ex.Message));
            return;
        }

        if (recording is null)
        {
            // Too short — treated as an accidental press (spec §3).
            Log.Info("recording discarded (shorter than 0.3 s)");
            SetState(DictationState.Idle);
            return;
        }

        Log.Info($"recording stopped: {recording.DurationSeconds:0.00}s, file {Path.GetFileName(recording.FilePath)}");

        var key = KeyStore.Load();
        if (key is null)
        {
            TryDelete(recording.FilePath);
            SetState(DictationState.Idle);
            Log.Error("no Groq API key available");
            RaiseError(S.ErrNoKey);
            return;
        }

        try
        {
            Log.Info("transcribing…");
            var result = await GroqClient.TranscribeAsync(recording.FilePath, key, Prefs.Vocabulary);
            Log.Info($"transcribed: {result.Text.Length} chars, lang={result.Language ?? "?"}, dur={result.Duration?.ToString("0.00") ?? "?"}");

            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                var finalText = result.Text;

                // AI term correction (spec §6.1): opt-in, needs a non-empty vocabulary; fail-open.
                // The state stays Transcribing while this runs (icon keeps showing work).
                if (Prefs.LlmPostProcess)
                {
                    finalText = await GroqClient.PostProcessAsync(finalText, key, Prefs.Vocabulary);
                    if (!ReferenceEquals(finalText, result.Text) && finalText != result.Text)
                        Log.Info($"llm post-process: corrected ({result.Text.Length} → {finalText.Length} chars)");
                    else
                        Log.Info("llm post-process: no changes (or skipped/fail-open)");
                }

                Deliver(finalText);

                // Persist the FINAL (corrected) text to history (spec §6.1). Store honors
                // "store audio" (spec §8) — it keeps or deletes the temp WAV (already in AudioDir).
                var id = Store.Shared.Insert(finalText, result.Language, result.Duration,
                    GroqClient.Model, recording.FilePath);
                Log.Info($"saved to history id={id?.ToString() ?? "null"}");
            }
            else
            {
                Log.Info("empty transcription — nothing to deliver");
                TryDelete(recording.FilePath);
                RaiseNotice(S.NoticeNoSpeech);
            }
        }
        catch (GroqException ex)
        {
            Log.Error("groq error", ex);
            TryDelete(recording.FilePath);
            RaiseError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("transcription failed", ex);
            TryDelete(recording.FilePath);
            RaiseError(ex.Message);
        }
        finally
        {
            SetState(DictationState.Idle);
        }
    }

    private void Deliver(string text)
    {
        // Text is ALWAYS copied (spec §5), then either pasted or shown.
        var mode = Prefs.Output;
        AutoInsert.CopyToClipboard(text);
        if (mode == OutputMode.Insert)
        {
            AutoInsert.SendCtrlV();
            Log.Info($"delivered via insert (clipboard + Ctrl+V), {text.Length} chars");
            if (Prefs.NotifyOnInsert)
                RaiseNotice(S.NoticeInserted);
        }
        else
        {
            Log.Info($"delivered via window, {text.Length} chars");
            OnUi(() => ResultReady?.Invoke(text));
        }
    }

    private void SetState(DictationState state)
    {
        _state = state;
        OnUi(() => StateChanged?.Invoke(state));
    }

    private void RaiseError(string message) => OnUi(() => Error?.Invoke(message));

    private void RaiseNotice(string message) => OnUi(() => Notice?.Invoke(message));

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        _recorder.Dispose();
    }
}
