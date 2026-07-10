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
        public bool NotifyOnInsert { get; set; } = true;         // show the "Inserted" balloon
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

    /// <summary>Whether to show the "Inserted" balloon after a successful insert.</summary>
    public static bool NotifyOnInsert
    {
        get { lock (Gate) return _data.NotifyOnInsert; }
        set { lock (Gate) { _data.NotifyOnInsert = value; Save(); } }
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
