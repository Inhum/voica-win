namespace Voica;

/// <summary>Dictation trigger mode (spec §4). Windows default is Toggle.</summary>
public enum DictationMode
{
    /// <summary>Push-to-talk: hold the key to record, release to send.</summary>
    Ptt,
    /// <summary>Press to start, press again to stop.</summary>
    Toggle,
}

/// <summary>Where recognized text goes (spec §5). Default is Insert.</summary>
public enum OutputMode
{
    /// <summary>Copy to clipboard and synthesize a paste into the focused field.</summary>
    Insert,
    /// <summary>Show an editable result window.</summary>
    Window,
}
