using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voica;

/// <summary>
/// Application settings persisted as JSON at %APPDATA%\Voica\settings.json.
/// Defaults follow the spec; the Windows hotkey/mode defaults differ from macOS
/// intentionally (spec §4): Toggle + Right Alt. Key storage is separate (see <see cref="KeyStore"/>).
/// </summary>
public static class Prefs
{
    private static readonly object Gate = new();
    private static Data _data = Load();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Backing data; property initializers double as defaults for missing JSON members.</summary>
    private sealed class Data
    {
        public int RetentionDays { get; set; } = 30;             // spec §8
        public bool StoreAudio { get; set; } = true;             // spec §8
        public string DictationMode { get; set; } = "toggle";    // spec §4 (Windows default)
        public string Hotkey { get; set; } = "";                 // binding storage form; "" = default
        public string HotkeyKey { get; set; } = "";              // legacy fixed-list value (migrated)
        public string OutputMode { get; set; } = "insert";       // spec §5
        public bool CheckUpdatesOnLaunch { get; set; } = true;   // spec §10
        public long LastUpdateCheckUnix { get; set; } = 0;       // 0 = never
        public string Vocabulary { get; set; } = "";             // spec §6
        public bool LlmPostProcess { get; set; } = false;        // spec §6.1, opt-in
        public string ChatModel { get; set; } = ChatModels.Auto;         // spec §6.1: "auto" | model id
        public string ResolvedChatModel { get; set; } = ChatModels.Seed; // cached resolution
        public string Engine { get; set; } = "cloud";            // spec §2.5: "cloud" | "local"
        public bool DoubleTapToStart { get; set; } = true;        // spec §4 (Toggle mode)
        public string SttModel { get; set; } = GroqClient.DefaultSttModel;   // spec §2
        public string Language { get; set; } = "auto";           // spec §2: "auto" | "ru" | "en"
        public bool NotifyOnInsert { get; set; } = true;         // show the "Inserted" balloon
        public bool ShowOverlay { get; set; } = true;            // spec §4.2, on by default
    }

    private static Data Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var json = File.ReadAllText(Paths.SettingsFile);
                var data = JsonSerializer.Deserialize<Data>(json, JsonOptions);
                if (data is not null) return data;
            }
        }
        catch
        {
            // Corrupt/unreadable settings fall back to defaults rather than crashing.
        }
        return new Data();
    }

    private static void Save()
    {
        Paths.EnsureCreated();
        var json = JsonSerializer.Serialize(_data, JsonOptions);
        File.WriteAllText(Paths.SettingsFile, json);
    }

    // --- Typed accessors ---

    public static int RetentionDays
    {
        get { lock (Gate) return _data.RetentionDays; }
        set { lock (Gate) { _data.RetentionDays = value; Save(); } }
    }

    public static bool StoreAudio
    {
        get { lock (Gate) return _data.StoreAudio; }
        set { lock (Gate) { _data.StoreAudio = value; Save(); } }
    }

    public static DictationMode Mode
    {
        get { lock (Gate) return _data.DictationMode.Equals("ptt", StringComparison.OrdinalIgnoreCase) ? DictationMode.Ptt : DictationMode.Toggle; }
        set { lock (Gate) { _data.DictationMode = value == DictationMode.Ptt ? "ptt" : "toggle"; Save(); } }
    }

    public static HotkeyBinding Hotkey
    {
        get
        {
            lock (Gate)
            {
                if (!string.IsNullOrWhiteSpace(_data.Hotkey))
                    return HotkeyBinding.Parse(_data.Hotkey);

                // Migrate legacy fixed-list value (RightAlt/LeftAlt/…); anything else → default.
                return _data.HotkeyKey.Trim().ToLowerInvariant() switch
                {
                    "leftalt" => new HotkeyBinding { MainVk = HotkeyBinding.VK_LMENU },
                    _ => HotkeyBinding.Default,
                };
            }
        }
        set { lock (Gate) { _data.Hotkey = value.ToStorage(); Save(); } }
    }

    public static OutputMode Output
    {
        get { lock (Gate) return _data.OutputMode.Equals("window", StringComparison.OrdinalIgnoreCase) ? OutputMode.Window : OutputMode.Insert; }
        set { lock (Gate) { _data.OutputMode = value == OutputMode.Window ? "window" : "insert"; Save(); } }
    }

    public static bool CheckUpdatesOnLaunch
    {
        get { lock (Gate) return _data.CheckUpdatesOnLaunch; }
        set { lock (Gate) { _data.CheckUpdatesOnLaunch = value; Save(); } }
    }

    /// <summary>Last update-check moment (for the once-a-day throttle, spec §10). Null = never.</summary>
    public static DateTime? LastUpdateCheck
    {
        get { lock (Gate) return _data.LastUpdateCheckUnix == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(_data.LastUpdateCheckUnix).UtcDateTime; }
        set { lock (Gate) { _data.LastUpdateCheckUnix = value is null ? 0 : new DateTimeOffset(value.Value.ToUniversalTime()).ToUnixTimeSeconds(); Save(); } }
    }

    public static string Vocabulary
    {
        get { lock (Gate) return _data.Vocabulary; }
        set { lock (Gate) { _data.Vocabulary = value ?? ""; Save(); } }
    }

    /// <summary>Recognition engine (spec §2.5): cloud (Groq, default) or local offline.</summary>
    public static EngineKind Engine
    {
        get { lock (Gate) return _data.Engine.Equals("local", StringComparison.OrdinalIgnoreCase) ? EngineKind.Local : EngineKind.Cloud; }
        set { lock (Gate) { _data.Engine = value == EngineKind.Local ? "local" : "cloud"; Save(); } }
    }

    /// <summary>Require a double tap to start recording in Toggle mode (spec §4, default on).</summary>
    public static bool DoubleTapToStart
    {
        get { lock (Gate) return _data.DoubleTapToStart; }
        set { lock (Gate) { _data.DoubleTapToStart = value; Save(); } }
    }

    /// <summary>Cloud speech-to-text model (spec §2). Unknown stored values fall back to the default.</summary>
    public static string SttModel
    {
        get { lock (Gate) return GroqClient.NormalizeSttModel(_data.SttModel); }
        set { lock (Gate) { _data.SttModel = GroqClient.NormalizeSttModel(value); Save(); } }
    }

    /// <summary>Recognition language for the cloud engine (spec §2): "auto" (default), "ru" or "en".</summary>
    public static string Language
    {
        get { lock (Gate) return GroqClient.NormalizeLanguage(_data.Language); }
        set { lock (Gate) { _data.Language = GroqClient.NormalizeLanguage(value); Save(); } }
    }

    /// <summary>Whether to fix vocabulary terms via the Groq LLM after transcription (spec §6.1, opt-in).</summary>
    public static bool LlmPostProcess
    {
        get { lock (Gate) return _data.LlmPostProcess; }
        set { lock (Gate) { _data.LlmPostProcess = value; Save(); } }
    }

    /// <summary>
    /// Chat model for AI term correction (spec §6.1): <c>"auto"</c> (default) or an explicit id.
    /// Models Groq has retired are migrated back to "auto" on read.
    /// </summary>
    public static string ChatModel
    {
        get
        {
            lock (Gate)
            {
                var v = _data.ChatModel;
                if (string.IsNullOrWhiteSpace(v) || RetiredChatModels.Contains(v)) return ChatModels.Auto;
                return v;
            }
        }
        set { lock (Gate) { _data.ChatModel = string.IsNullOrWhiteSpace(value) ? ChatModels.Auto : value; Save(); } }
    }

    /// <summary>
    /// Models Groq has withdrawn; a saved choice pointing at one falls back to "auto" and a cached
    /// resolution falls back to the seed (spec §6.1). The list is maintained by hand and grows as
    /// models are retired — self-healing on a 404 is not enough on its own, or a user whose manual
    /// pick is dead would pay one failed request on every single dictation.
    /// </summary>
    private static readonly string[] RetiredChatModels =
    {
        "qwen/qwen3-32b",
        "llama-3.3-70b-versatile",   // withdrawn by Groq 2026-08-16
    };

    /// <summary>Last successfully resolved chat model — used offline and on first run (spec §6.1).</summary>
    public static string ResolvedChatModel
    {
        get
        {
            lock (Gate)
            {
                var v = _data.ResolvedChatModel;
                return string.IsNullOrWhiteSpace(v) || RetiredChatModels.Contains(v) ? ChatModels.Seed : v;
            }
        }
        set { lock (Gate) { _data.ResolvedChatModel = value; Save(); } }
    }

    /// <summary>The model actually sent to Groq: an explicit choice, else the cached resolution.</summary>
    public static string ActiveChatModel
    {
        get
        {
            var chosen = ChatModel;
            return chosen == ChatModels.Auto ? ResolvedChatModel : chosen;
        }
    }

    /// <summary>Whether to show the "Inserted" balloon after a successful insert.</summary>
    public static bool NotifyOnInsert
    {
        get { lock (Gate) return _data.NotifyOnInsert; }
        set { lock (Gate) { _data.NotifyOnInsert = value; Save(); } }
    }

    /// <summary>
    /// Show the floating dictation capsule at the bottom of the screen (spec §4.2, default on).
    /// While it is on the tray icon stays neutral; off falls back to the icon-only indication.
    /// </summary>
    public static bool ShowOverlay
    {
        get { lock (Gate) return _data.ShowOverlay; }
        set { lock (Gate) { _data.ShowOverlay = value; Save(); } }
    }

    /// <summary>Resets all settings to defaults (for Delete all data, spec §11).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _data = new Data();
            Save();
        }
    }
}
