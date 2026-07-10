# Contributing to Voica for Windows

Thanks for your interest! This document covers how the project is structured and the conventions to
follow.

## Governing rule: the spec is canonical

Behavior is defined by the cross‑platform spec at [docs/CORE-SPEC.md](docs/CORE-SPEC.md) (a mirror
of `Inhum/voica/docs/CORE-SPEC.md`). **Read it before changing behavior.** Every default, endpoint,
and message traces to a spec section (cited in code as `§N`). If you change cross‑platform behavior,
update the spec first, then mirror it here and in the macOS app (the parity rule).

Windows defaults intentionally differ from macOS where the spec says so (e.g. Toggle + Right Alt vs
PTT + Right Option).

## Build, run, test

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build Voica.sln -c Debug

# Self-test: pure logic, no GUI/network. Exit 0/1. It's a WinExe, so run via Start-Process:
Start-Process -FilePath "src\Voica\bin\Debug\net8.0-windows10.0.17763.0\Voica.exe" `
  -ArgumentList "--test-all" -Wait -PassThru -NoNewWindow

# Run the tray app
& "src\Voica\bin\Debug\net8.0-windows10.0.17763.0\Voica.exe"
```

**Note:** a running `Voica.exe` locks its output file. Stop it before rebuilding:
`Get-Process Voica -ErrorAction SilentlyContinue | Stop-Process -Force`.

## The self-test is the test suite

There is no separate unit‑test project. [`SelfTest.cs`](src/Voica/SelfTest.cs) is run via
`--test-all`; each check is a named `Check(...)` printing `[+]`/`[-]`. **Add a check for every new
piece of pure logic.** The suite must stay green. Tests that mutate real state (settings, key file,
DB) must snapshot and restore it. CI runs this on `windows-latest`.

## Architecture

Tray‑only background app. Entry point is [`Program.cs`](src/Voica/Program.cs): `--test-all`
short‑circuits before any WPF init. Code splits into `Core/` (logic, spec‑mapped) and `UI/` (WPF
windows + tray).

- `DictationController` — orchestrator/state machine (`idle → recording → transcribing → idle`).
- `Hotkey` / `HotkeyBinding` — global `WH_KEYBOARD_LL` hook; a bare key is swallowed, a combination
  is intercepted only when fully pressed.
- `Recorder` — NAudio → WAV 16 kHz mono PCM.
- `GroqClient` — the Groq request contract and vocabulary→prompt prep.
- `Store` — SQLite history, serialized through one connection + a lock.
- `KeyStore` — DPAPI‑encrypted key file (+ `GROQ_API_KEY` env fallback).
- `Prefs` — JSON settings; `Loc`/`S` — en/ru localization.

More detail is in [CLAUDE.md](CLAUDE.md).

## Pull requests

- Keep changes focused; match the surrounding code style.
- Update the self‑test for new logic, and keep the build warning‑free.
- Note any behavior change against the spec section it implements.
- Be kind — see the [Code of Conduct](CODE_OF_CONDUCT.md).
