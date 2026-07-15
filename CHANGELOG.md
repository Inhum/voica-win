# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-07-15

### Added
- **Dictate** tray-menu item (spec §4.1): manual start/stop without the hotkey. Always toggle
  semantics — idle starts, recording stops, transcribing is ignored. Menu order now follows the
  spec (Dictate · History · Settings · About · Check for Updates · Quit), and a left click on the
  tray icon opens the menu too.
- Release workflow: binaries are built, self-tested, and published by GitHub Actions from the
  pushed tag (groundwork for SignPath code signing).

## [0.2.0] - 2026-07-13

### Added
- **AI term correction** (spec §6.1, opt-in, default off): after transcription, mangled vocabulary
  terms are fixed by a Groq chat model (`qwen/qwen3-32b`). Fail-open — on any error the original
  Whisper text is delivered. Includes a model-availability check in Settings (403 → hint to allow
  the model in the Groq console). Mirrors macOS 0.7.0.
- **Reset settings…** button: returns settings to defaults while keeping the API key, history,
  audio, and vocabulary. Mirrors macOS 0.8.0.
- Live vocabulary character counter `N / 800` with a warning color when over budget.

### Fixed
- Stray keyboard-layout switching after dictation: the injected Ctrl+V could combine with a
  physically held Shift/Alt into the system layout-switch chord (Ctrl+Shift / Alt+Shift). Insert
  now waits for physical modifiers to be released; the keyboard hook ignores injected events and
  hotkey callbacks no longer run inside the low-level hook callback.
- Injected keys now carry real scan codes (some layout switchers/IMEs mishandle `wScan=0`).

## [0.1.0] - 2026-07-10

### Added
- First Windows implementation of Voica (C# / .NET 8 / WPF), tracking the cross‑platform
  [CORE‑SPEC](docs/CORE-SPEC.md).
- Global hotkey dictation with PTT and Toggle modes; default **Toggle + Right Alt**.
- Preset hotkeys (Right/Left Alt, CapsLock, ScrollLock, Pause) and **custom key combinations**
  (e.g. `Ctrl+Shift+Space`) captured in Settings.
- Transcription via Groq Whisper (`whisper-large-v3-turbo`); auto‑insert (Ctrl+V) with a clipboard
  fallback, plus an editable result‑window mode.
- SQLite history with re‑copy, audio playback, and deletion; audio retention cleanup on launch.
- Settings: dictation mode, hotkey, output, store‑audio, retention, vocabulary, notification and
  update toggles, and a Groq API key field (Validate + Save, with **Show**).
- Groq key stored encrypted with **Windows DPAPI**, with a `GROQ_API_KEY` env fallback.
- Update checks against `Inhum/voica-win` releases (opt‑in, once a day); opens the release page only.
- Tray state icons with a recording pulse; About window.
- **Delete all data** with random‑phrase confirmation.
- English/Russian localization by system language.
- `--test-all` self‑test (no GUI/network) and a `windows-latest` CI workflow.

[0.3.0]: https://github.com/Inhum/voica-win/releases/tag/v0.3.0
[0.2.0]: https://github.com/Inhum/voica-win/releases/tag/v0.2.0
[0.1.0]: https://github.com/Inhum/voica-win/releases/tag/v0.1.0
