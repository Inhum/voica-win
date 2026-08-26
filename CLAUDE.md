# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`voica-win` is a **new native Windows implementation** of Voica — dictation → punctuated text via
Groq Whisper — built in C# / .NET 8 / WPF. It is **not** a port of the macOS Swift app; it
reproduces that app's behavior on Windows.

## Governing rule: the spec is canonical

Behavior is defined by the cross-platform spec, vendored at [docs/CORE-SPEC.md](docs/CORE-SPEC.md)
(mirror of `Inhum/voica/docs/CORE-SPEC.md`). **Read it before changing behavior and follow it
exactly — do not invent behavior.** The reference implementation for cross-checking logic is the
Swift app at `github.com/Inhum/voica`. Every default/endpoint/message in the code traces to a spec
section (cited inline as `§N`). Windows defaults intentionally differ from macOS where the spec says
so (e.g. dictation mode **Toggle** + **Right Alt**, vs macOS PTT + Right Option).

## Build / run / test

The .NET SDK is not always on PATH. Prepend it per shell:
`$env:Path = "C:\Program Files\dotnet;$env:Path"` (PowerShell state does not persist between tool
calls) or call `C:\Program Files\dotnet\dotnet.exe` directly. Target framework is
`net8.0-windows10.0.17763.0` (Win10 1809 floor). NuGet source is pinned in `nuget.config`.

```powershell
# Build
dotnet build Voica.sln -c Debug

# Self-test (spec §12) — pure logic, no GUI/network. Exit 0/1.
# It is a WinExe, so run via Start-Process to see output + capture exit code:
Start-Process -FilePath "src\Voica\bin\Debug\net8.0-windows10.0.17763.0\Voica.exe" `
  -ArgumentList "--test-all" -Wait -PassThru -NoNewWindow

# Run the app (tray icon; no main window)
& "src\Voica\bin\Debug\net8.0-windows10.0.17763.0\Voica.exe"

# Single-file self-contained publish (Phase 6)
dotnet publish src\Voica\Voica.csproj -c Release -r win-x64 -p:PublishSingleFile=true
```

There is no separate unit-test project. **`--test-all` is the test suite** ([SelfTest.cs](src/Voica/SelfTest.cs)):
each check is a named `Check(...)` line printing `[+]`/`[-]`. Add a check here for every new piece of
pure logic; the suite grows per phase and must stay green. Tests that mutate real state (settings,
key file, DB) must snapshot and restore it. To run "a single test", temporarily comment out the
others — there is no per-test filter.

**`--normalize-corpus <file>`** runs the §6.2 term rules over a file of past dictations (one per
line) with the current vocabulary and prints only the lines they changed. Spec §6.2 makes this
mandatory whenever a threshold moves: thresholds come from data, and every change over the whole
history has to be justified before the result is frozen as a self-test fixture.

**Proxy behaviour (§9.5) is checked with a stub, not by reading code.** There is no proxy at home,
and the system network settings are off limits for a test, so:

```powershell
# terminal 1 — Deny answers every request 407; Allow tunnels for real
.\scripts\fake-proxy.ps1 -Mode Deny

# terminal 2 — VOICA_PROXY overrides the system proxy for this process only
$env:VOICA_PROXY = "127.0.0.1:18899"
Start-Process -FilePath "src\Voica\bin\Debug\net8.0-windows10.0.17763.0\Voica.exe" `
  -ArgumentList "--probe-net" -Wait -NoNewWindow
```

**`--probe-net`** walks every network surface — key check, chat model, a real (silent) dictation,
the update check, the model download — and prints what each would tell the user. In Deny mode all
five must NAME the proxy; anything that prints a raw error code is the §9.5 defect coming back.
It sends real requests with the saved key.

**`--probe-settings [all|<tab>]`** shows the Settings window on its own and prints its size: `all`
cycles the tabs, a number opens straight at one the way the tray does. §11.4's sizing rules — the
window grows *and* shrinks per tab, and a window opened at a tab is sized for that tab on the first
frame — are invisible in the XAML and only provable by reading the height back. Pair it with
`PrintWindow` from PowerShell to shoot a tab (see the window-screenshot notes).

**Rebuild gotcha:** a running `Voica.exe` locks the output exe. Stop it first:
`Get-Process Voica -ErrorAction SilentlyContinue | Stop-Process -Force`.

## Architecture

Tray-only background app. Entry point is **[Program.cs](src/Voica/Program.cs)** (not the WPF default):
`App.xaml` is compiled as `<Page>` with `<StartupObject>Voica.Program`, so `--test-all` short-circuits
before any WPF init. `Program.Main` calls `AttachConsole` so the self-test prints to the launching
terminal. [App.xaml.cs](src/Voica/App.xaml.cs) enforces a single instance (named mutex), runs
retention, and hosts the tray controller with `ShutdownMode=OnExplicitShutdown`.

Code splits into `Core/` (logic, spec-mapped) and `UI/` (WPF windows + tray):

- **[DictationController.cs](src/Voica/Core/DictationController.cs)** is the orchestrator and state
  machine (`idle → recording → transcribing → idle`, spec §4). It wires the hotkey → recorder →
  Groq → delivery, raises `StateChanged`/`Error`/`Notice`/`ResultReady`, and persists results to the
  Store. It lives on the WPF UI thread; async continuations resume there (hotkey callbacks fire on
  the UI thread, so the sync context is captured).
- **[Hotkey.cs](src/Voica/Core/Hotkey.cs) / [HotkeyBinding.cs](src/Voica/Core/HotkeyBinding.cs)** —
  global `WH_KEYBOARD_LL` hook (needed for PTT hold / clean Toggle edges). A binding is either a
  **bare key** (Right/Left Alt, CapsLock, ScrollLock, Pause, F13–F24), which the hook **swallows**
  entirely (dedicated to dictation), or a **combination** (Ctrl/Alt/Shift/Win + a main key, e.g.
  Ctrl+Shift+Space), where only the main key is swallowed while the modifiers are held — so the
  modifiers keep working and system shortcuts aren't broken. Swallowing a bare key is required
  because a stray Alt press/release activates the active window's menu bar and steals focus,
  breaking auto-insert. Ctrl/Win are not offered as bare keys for that reason. Must be
  created/disposed on the UI thread (needs a message loop).
- **[AutoInsert.cs](src/Voica/Core/AutoInsert.cs)** — always sets the clipboard (the spec §5
  fallback), then `SendInput` Ctrl+V in insert mode. **The native `INPUT` struct must marshal to 40
  bytes on x64** (union sized to `MOUSEINPUT`); an undersized struct makes `SendInput` silently
  reject `cbSize` and inject nothing. Guarded by a self-test.
- **[Store.cs](src/Voica/Core/Store.cs)** — SQLite history. All access is serialized through one
  connection + a `lock` (the Windows equivalent of the macOS serial queue, spec §7). Owns the audio
  file lifecycle and honors "store audio" (§8).
- **[GroqClient.cs](src/Voica/Core/GroqClient.cs)** — the §2 request contract + §6 vocabulary→`prompt`
  prep (trim / empty→null / keep last 800 chars) + error→message mapping. Static, no secrets.
- **[KeyStore.cs](src/Voica/Core/KeyStore.cs)** — Groq key in a DPAPI file
  (`%APPDATA%\Voica\credentials.dat`), with a `GROQ_API_KEY` env fallback (§9).
- **[Prefs.cs](src/Voica/Core/Prefs.cs)** — settings as JSON (`settings.json`); property initializers
  are the spec defaults. **[Paths.cs](src/Voica/Core/Paths.cs)** centralizes `%APPDATA%\Voica\`.
  Recognized text/audio never live next to the exe (survive updates).

Data lives outside the exe in `%APPDATA%\Voica\` (`history.sqlite`, `audio\*.wav`,
`credentials.dat`, `settings.json`, `voica.log`).

## Phased delivery

Work proceeds in phases mirroring the macOS app (see the plan / task list): 0 Scaffold · 1 Record+Groq ·
2 Storage · 3 Settings+KeyStore · 4 Updater+About+polish · 5 Self-test · 6 Packaging+OSS docs. Each
phase must end on a clean build with the self-test green. Localization (en/ru) and user-facing
message wording land in Phase 4; earlier strings are placeholder English.

## Releasing (spec §13)

Everything a user can see goes out as a **candidate first**: tag `vX.Y.Z-rc.N`, which the release
workflow publishes as a GitHub **pre-release**. GitHub keeps pre-releases out of `/releases/latest`,
which is where the update check looks (§10), so a candidate never reaches ordinary users while the
build, the link and the version answer "which one are you running" for a tester.

- `<Version>` in [Voica.csproj](src/Voica/Voica.csproj) stays the **base** `X.Y.Z` the whole time —
  the suffix lives only in the tag. CI verifies the tag against it and fails on a mismatch.
- CI stamps the full version (suffix included) into the binary via `-p:InformationalVersion`, so
  About shows `0.8.2-rc.1`. Everything else — the installer's version fields (Inno only accepts
  numbers) and the asset names — uses the base version. Only the release title carries the tag.
- `Updater.IsNewer` compares the numeric core and treats a release as newer than its own candidate,
  so a tester on `rc.1` is told when the final ships.
- When nothing is left to fix, tag `vX.Y.Z` with no suffix; that one becomes `latest`. No release
  branch: candidates are cut from main.

## Workflow constraints

- **No remote actions without explicit user approval**: no `git` commit/push, no creating the public
  `Inhum/voica-win` repo, no `gh release`. Build locally until told otherwise.
- Update checks target **`Inhum/voica-win`** (each OS checks its own repo, §10) — never `Inhum/voica`.
