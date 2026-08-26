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
    private readonly LocalEngine _localEngine = new();
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
    /// <summary>
    /// Raised when the local engine is selected but its model is not installed (spec §2.5). The
    /// cloud is NOT used instead — "Local (offline)" is a promise about privacy, and breaking it
    /// quietly is worse than refusing.
    /// </summary>
    public event Action? ModelMissing;

    public DictationController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hotkey.Started += OnPttStart;
        _hotkey.Stopped += OnPttStop;
        _hotkey.Toggled += OnToggle;
        // First-time model load takes seconds (spec §2.5) — tell the user it's not a hang.
        _localEngine.PreparingModel += () => RaiseNotice(S.LocalPreparing);
        _recorder.CaptureLost += OnCaptureLost;
    }

    public DictationState State => _state;

    /// <summary>Current microphone peak (0..1) for the overlay wave (spec §4.2); 0 unless recording.</summary>
    public double InputLevel => _recorder.Level;

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
        _hotkey.DoubleTapToStart = Prefs.DoubleTapToStart;
        _hotkey.IsIdle = () => _state == DictationState.Idle;
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

    /// <summary>
    /// Cancels the current recording from the overlay's «×» (spec §4.2): the audio is thrown away
    /// and nothing is transcribed. No-op unless recording.
    /// </summary>
    public void CancelDictation()
    {
        if (_state != DictationState.Recording) return;
        _recorder.Cancel();          // stops capture and deletes the temp WAV
        SetState(DictationState.Idle);
        Log.Info("recording cancelled by the user");
    }

    /// <summary>
    /// The microphone stopped handing over audio mid-recording (spec §3). Everything captured so
    /// far is still transcribed — it is the user's speech — but the dictation ends right now and
    /// says so. Speaking for another five minutes into a dead microphone is the failure this
    /// exists to prevent; the reason is in the log.
    /// </summary>
    private void OnCaptureLost(string? reason)
    {
        // The driver's callback thread is not ours.
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_state != DictationState.Recording) return;
            RaiseNotice(S.NoticeCaptureLost);
            _ = EndRecordingAndTranscribeAsync();
        }));
    }

    /// <summary>
    /// Whether a dictation must be refused outright (spec §2.5): the local engine is chosen and its
    /// model is not installed. The cloud is NOT an acceptable substitute — "Local (offline)" is a
    /// promise about privacy, and pressing "Download model" is not consent to send a voice out.
    /// A live macOS check found the opposite behaviour shipping: after the model was deleted the
    /// dictation went to Groq and the history recorded `whisper-large-v3`, while the switch still
    /// read offline.
    /// </summary>
    public static bool MustRefuse(EngineKind engine, bool modelInstalled) =>
        engine == EngineKind.Local && !modelInstalled;

    private void BeginRecording()
    {
        // Say it BEFORE the dictation, not after (spec §2.5): telling someone their model is
        // missing once they have spoken for two minutes wastes the two minutes — there is nothing
        // to transcribe that recording with.
        if (MustRefuse(Prefs.Engine, ModelManager.IsInstalled()))
        {
            Log.Error("local engine is selected but its model is not installed");
            ModelMissing?.Invoke();
            return;
        }

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

        // Engine selection (spec §2.5). A chosen local engine is never served by the cloud, not
        // even "while the model downloads": pressing "Download model" is not consent to send your
        // voice out. The check is repeated here because the model can be deleted mid-recording —
        // the one at the start of the dictation is the one the user actually sees.
        bool useLocal = Prefs.Engine == EngineKind.Local;
        if (MustRefuse(Prefs.Engine, ModelManager.IsInstalled()))
        {
            TryDelete(recording.FilePath);
            SetState(DictationState.Idle);
            Log.Error("local model disappeared during the recording — refusing to fall back to the cloud");
            ModelMissing?.Invoke();
            return;
        }

        var key = KeyStore.Load();
        if (!useLocal && key is null)
        {
            TryDelete(recording.FilePath);
            SetState(DictationState.Idle);
            Log.Error("no Groq API key available");
            RaiseError(S.ErrNoKey);
            return;
        }

        try
        {
            Log.Info($"transcribing ({(useLocal ? "local" : "cloud")})…");
            // Vocabulary prompt hint is a Whisper feature — cloud only (spec §6/§2.5).
            TranscriptionResult result;
            string modelUsed;
            if (useLocal)
            {
                result = await _localEngine.TranscribeAsync(recording.FilePath);
                modelUsed = ModelManager.ModelName;
            }
            else
            {
                try
                {
                    // Model/language come from settings (spec §2); history records what actually ran.
                    var sttModel = Prefs.SttModel;
                    result = await GroqClient.TranscribeAsync(recording.FilePath, key!, Prefs.Vocabulary,
                        sttModel, Prefs.Language);
                    modelUsed = sttModel;
                }
                catch (GroqException ex) when (ex.IsNetworkError && ModelManager.IsInstalled())
                {
                    // Offline fallback (spec §2.5): no network but the local model is here —
                    // transcribe on-device and let the user know unobtrusively.
                    Log.Info($"offline fallback to local engine ({ex.Message})");
                    RaiseNotice(S.NoticeOfflineFallback);
                    result = await _localEngine.TranscribeAsync(recording.FilePath);
                    modelUsed = ModelManager.ModelName;
                }
            }
            Log.Info($"transcribed: {result.Text.Length} chars, lang={result.Language ?? "?"}, dur={result.Duration?.ToString("0.00") ?? "?"}");

            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                var finalText = result.Text;

                // Rules first, model second (spec §6.3 → §6.2 → §6.1): no key and no network, and
                // the model is left with only what needs understanding. Each rule has its own
                // switch, because one that ever gets somebody's words wrong must be escapable
                // without an app update.
                if (Prefs.RemoveFillers)
                {
                    // Filler removal goes first: it deletes what was said, so it must not run over
                    // text the term rules have already rewritten.
                    var stripped = Fillers.Strip(finalText);
                    if (!string.Equals(stripped, finalText, StringComparison.Ordinal))
                    {
                        Log.Info("fillers: removed");
                        finalText = stripped;
                    }
                }

                var ruled = Prefs.FixTermsByRules ? TermFix.Apply(finalText, Prefs.Vocabulary) : finalText;
                if (!string.Equals(ruled, finalText, StringComparison.Ordinal))
                {
                    Log.Info("term rules: corrected");
                    finalText = ruled;
                }

                // AI term correction (spec §6.1): opt-in, applies to BOTH engines (needs key+net);
                // fail-open. The state stays Transcribing while this runs.
                if (Prefs.LlmPostProcess && key is not null)
                {
                    // A 403 (model blocked for the Groq org) is reported once per model per
                    // session — it cannot heal itself, unlike a 404 (spec §6.1).
                    // Compare against what went in — the rules of §6.2 may already have changed it.
                    var beforeLlm = finalText;
                    finalText = await GroqClient.PostProcessAsync(finalText, key, Prefs.Vocabulary, RaiseNotice);
                    if (finalText != beforeLlm)
                        Log.Info($"llm post-process: corrected ({beforeLlm.Length} → {finalText.Length} chars)");
                    else
                        Log.Info("llm post-process: no changes (or skipped/fail-open)");
                }

                // Quotation marks last, in the single delivery point (spec §6.4): both the engine
                // and the model leave unpaired quotes behind, and there is no telling which did.
                if (Prefs.FixQuotes)
                {
                    var quoted = Quotes.Balance(finalText);
                    if (!string.Equals(quoted, finalText, StringComparison.Ordinal))
                    {
                        Log.Info("quotes: fixed");
                        finalText = quoted;
                    }
                }

                Deliver(finalText);

                // Persist the FINAL (corrected) text to history (spec §6.1). Store honors
                // "store audio" (spec §8) — it keeps or deletes the temp WAV (already in AudioDir).
                // The engine's own text rides along; Store drops it when correction changed
                // nothing, so a filled raw_text always means "these two differ" (spec §7).
                var id = Store.Shared.Insert(finalText, result.Language, result.Duration,
                    modelUsed, recording.FilePath, rawText: result.Text);
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
            // Whatever slipped past GroqClient's own handling still goes through the one
            // translation point (spec §9.5); anything that is not a network failure keeps its text.
            RaiseError(Net.Describe(ex, GroqClient.Endpoint));
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
        _localEngine.Dispose();
    }
}
