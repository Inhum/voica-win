# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/Inhum/voica-win/commits/main
