namespace Voica;

/// <summary>
/// Dictation state machine (spec §4): idle → recording → transcribing → idle.
/// The tray icon reflects the current state.
/// </summary>
public enum DictationState
{
    Idle,
    Recording,
    Transcribing,
}
