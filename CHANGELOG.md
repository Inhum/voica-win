# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.0] - 2026-08-21

### Added
- **Filler sounds are removed** (spec §6.3), on by default and switchable. The drawn-out "э-э-э",
  "ммм", "хмм" mean nothing in speech and clutter the text. The rule reads the SHAPE of a word
  rather than a list of spellings — a token that collapses to one or two letters once hyphens and
  repeats are folded — because recognition spells the same mumble differently every time. What it
  deliberately leaves alone is the interesting part, and every item is a line of live text that
  broke: single sounds go only when they were drawn out ("а" is a conjunction, "у" and "о" are
  prepositions), "и" is never touched at all (the engine writes the abbreviation "ИИ" in lower case
  too), "эм" is not a filler (that is how it writes GigaAM), numbers and all-caps abbreviations are
  untouchable, and "мм" after a number is millimetres. A real word that was merely drawn out gets
  straightened rather than dropped — "Ну-у-у" → "Ну" — from an explicit list, because straightening
  everything would turn "PPC" into "Pc". Unlike the term rules this one has a setting: it deletes
  what was said, which is wrong for anyone transcribing speech verbatim.
- **Quotation marks are repaired** (spec §6.4), on by default and switchable. Straight quotes become
  guillemets decided by position, a space missing after a colon comes back, and unpaired marks are
  removed. It runs last, in the single delivery point, after both the rules and the AI pass — the
  engine decodes greedily and cannot remember that a quote is open, the model wraps substituted
  terms in quotes against the prompt, and there is no telling which of them left the mess.
- **A switch for each rule that changes words** (spec §6.2/§6.3/§6.4), all on by default: filler
  removal and quote repair under "Text clean-up" on the Dictation tab, term rules on Vocabulary
  above the AI pass — first what always works and costs nothing, then the optional extra. Without
  switches, a rule that ever got somebody's words wrong could only be escaped by updating the app.
- **The first run without a key is no longer a dead end** (spec §11.3). A line under the key field
  says the local engine needs neither key nor internet, shown only while the cloud engine is
  selected and no key is saved. No button on purpose: the engine switch is right above it, and a
  button would invite a 400 MB download before the reason for it is clear.

### Fixed
- **Windows that split the same place into a different number of words no longer duplicate it**
  (spec §2.5). "3кар" against "Три кар" is four words against five, and word-by-word alignment has
  nothing to line up. A fallback now compares the words glued together without spaces — only after
  the word-level search comes up empty, and only from ten characters up, because gluing compares
  loosely and short pieces all look alike.
- **A filler opening a sentence in the middle of the text left the next word in lower case.** The
  mandatory run over the whole history caught it three times in one dictation: "…на улицу. Э-э, на
  работу" came out as "…на улицу. на работу". The capital now follows the sentence, not the start
  of the text.

## [0.7.0] - 2026-08-20

### Added
- **The vocabulary now works on its own, without AI** (spec §6.2). Deterministic rules pull garbled
  spellings back to the terms you listed — no API key, no internet, and the same on both engines.
  They run before the optional AI pass, which is left with what actually needs understanding. The
  match is a consonant skeleton (`Dпсик`, `Dpсиcк`, `Deepsc` all reduce to `DeepSeek`'s `dpsk`), and
  what is required on top depends on the evidence: a word written in two alphabets at once is proof
  of mangling by itself, plain Latin also needs letter closeness, plain Cyrillic only ever matches an
  exact skeleton. Compound terms are found across two words, so `клодкод` and `Tail scale` both land.
  Nothing matches, nothing is touched: the thresholds were set by running the rules over a whole real
  history, and every trap — «Вика», «Папа», «усы», `Greek`, `vice versa` — is frozen as a test.
- **Search in History** (spec §7), with `Ctrl+F` and highlighting. It searches the shown text **and
  what the engine heard before the corrections**, because people remember what they said, not what
  the rules and the model made of it. When a record answers only through that original text, it is
  shown under the record, so you can see what matched.
- **The engine's raw text is kept** with a record whenever correction changed something (spec §7).
  It is what the search above looks into, and it rides along in the JSON export.
- **A warning when a bare Left Alt collides with the system layout switch** (spec §4). Windows
  switches layouts on Left Alt+Shift, and a bare hotkey is taken over entirely, so the layout would
  stop switching. The key stays on offer — Settings just says so under it.

### Changed
- **The History window has two panes**, like the macOS one: the list on the left, the record's text
  on the right, wrapped and scrollable. A dictation longer than a sentence could not be read at all
  before. Language, length and engine moved to the line under the text; Export stays an action over
  the whole history and is not touched by the search.
- **Settings say what the vocabulary now does**, and the AI toggle is «Fix terms with AI (extra Groq
  request)» — an optional pass on top of the rules rather than the only way to fix terms. The long
  explanations moved into the info dots the other tabs already use.

### Fixed
- **A loud word could kill the recording, silently.** The level meter behind the recording capsule
  computed its peak with `Math.Abs` on a 16-bit sample, which overflows on exactly the value a
  microphone produces when the input clips. The exception left the capture callback and took the
  capture thread with it — so the app went on "recording" nothing while the user kept talking. One
  live dictation of 5 minutes 48 seconds came back as 40 seconds of text. Fixed, and the capture path
  now carries nothing but the write to the file.
- **A dead microphone is now noticed within three seconds** (spec §3), whatever the cause: the
  driver's reason is logged instead of dropped, an error in the callback costs one 50 ms buffer
  instead of the recording, and a watchdog watches the gap between buffers. The dictation ends at
  once, says the microphone stopped sending audio, and transcribes what was captured.
- **A phrase could appear twice** in a long local-engine dictation (spec §2.5). Neighbouring windows
  heard one word differently — «управляющий» against «управляющего» — and a single word was enough to
  break the overlap, so both copies reached the text. A run of four words or more now matches with
  one word off, except right at the seam, where a mismatch means a word cut in half and the existing
  fallback recovers it.

## [0.6.3] - 2026-08-19

### Added
- **An installer.** `Voica-Setup-<version>.exe` joins the two bare executables on the release page
  and is now the recommended download: it carries the self-contained build, so there is no longer a
  choice to make between `Voica.exe` and `Voica-fx.exe` — a choice that required knowing whether the
  .NET 8 Desktop Runtime was installed. It sets Voica up for the current user
  (`%LocalAppData%\Programs\Voica`), so **no UAC prompt**, adds a Start-menu entry and a proper
  uninstall record, offers to close a running copy when installing over it, and leaves history,
  settings and the API key untouched on uninstall.

### Fixed
- **Reasoning models leaked their train of thought into the dictated text** (spec §6.1). Some chat
  models return their reasoning inside `content`, wrapped in `<think>…</think>`, with the answer
  after it — and all of it was delivered to you instead of your dictation. Not a corner case:
  `qwen/qwen3.6-27b` is the second link of the correction chain, so anyone whose
  `openai/gpt-oss-120b` is blocked for their Groq organization hit this on every dictation. Voica
  now strips those blocks (any case, attributes, several blocks, and an unclosed one — which means
  the answer was cut off mid-thought). Measured against the live model: 3800–11500 characters of
  reasoning removed per answer.
- **A rambling answer no longer replaces your text.** Term correction swaps individual words, so an
  answer that is empty or more than twice the original length is treated as the model going off the
  rails and the original text is delivered (spec §6.1).
- **`allam-2-7b` is no longer used for correction.** The live model list comes back alphabetically
  and the last-resort pick is the first entry, so an Arabic model was quietly becoming the fallback
  for correcting Russian terms.
- **`--test-all` no longer touches your settings.** The self-test mutates real settings and restored
  them one field at a time, which missed several — including "AI term correction", which it switched
  off behind your back on every run. It now snapshots all settings and restores them whatever
  happens.

### Changed
- The hint under the language picker says auto-detect covers about a hundred languages: the three
  entries (auto / Russian / English) were reading as the full list of what Voica understands (§2).

## [0.6.2] - 2026-08-17

### Fixed
- **AI term correction pointed at a model Groq had withdrawn.** `llama-3.3-70b-versatile` was
  retired on 2026-08-16, and it was both the head of our priority chain and the seed used on a
  first run or offline — so correction aimed at a dead model, and a cached resolution pointing at
  it cost one failed request per dictation before healing itself. The chain is now
  `openai/gpt-oss-120b` → `qwen/qwen3.6-27b` → `openai/gpt-oss-20b` → `llama-3.1-8b-instant`, and
  a saved manual choice of the withdrawn model falls back to "auto" with its cached resolution
  falling back to the seed (spec §6.1). Dictation itself was never affected — correction fails
  open and delivers the original text.

### Added
- **A blocked correction model is no longer silent.** If Groq answers 403 (the model is not
  allowed for your organization) during a dictation, Voica says so once per model per session,
  naming the model and pointing at console.groq.com → Settings → Limits. Unlike a retired model
  (404), this cannot heal itself, so silence made the feature look broken.

### Changed
- `groq/compound` and `compound-mini` are no longer used for correction: they are agentic systems
  with their own routing and tools — an extra layer for one short term fix, and they route to
  models an organization may not have.

## [0.6.1] - 2026-08-14

### Fixed
- **The result window opened on the wrong monitor.** In the "show result window" output mode it
  appeared on the screen holding the *mouse pointer* instead of the screen where the dictation
  happened — so parking the pointer on another monitor while typing sent the text over there.
  Both the dictation bar and the result window now use the screen of the focused window, picked
  once when recording starts (spec §5). Insert mode was never affected.

### Internal
- Window placement lives in one helper (`ScreenPlacement`) shared by the bar and the result
  window: the monitor is resolved from the focused window and positions are set in device pixels,
  which is what a PerMonitorV2 app needs when monitors have different scaling.
- Vendored the updated CORE-SPEC (§5 gains the result-window rule; the multi-monitor rule is now
  confirmed on both platforms) and rewrote [docs/ROADMAP.md](docs/ROADMAP.md) to mirror the macOS
  roadmap.

## [0.6.0] - 2026-08-12

### Added
- **Dictation bar** (spec §4.2, on by default): a floating capsule at the bottom center of the
  screen, above other windows, that never takes focus. While recording it shows a live level wave
  with **×** (cancel — the audio is discarded, nothing is transcribed) and **✓** (stop and
  transcribe); during transcription the wave and buttons give way to a spinner and "Transcribing…".
  Cancelling a dictation is a new path — previously a started recording could only be sent.
  Layout, colors and metrics follow the macOS HUD. Toggle: Settings → Dictation.
  On a multi-monitor setup the bar appears on the screen showing the focused window — where the
  text is going to land — and honors that monitor's scaling.
- **Exactly one indicator at a time** (spec §4.2): while the bar is on, the tray icon stays
  neutral in every state. Turn the bar off and the old icon behavior returns (pulsing while
  recording, a static accent while transcribing).
- **Double tap to start** in Toggle mode (spec §4, on by default, 0.35 s window): a stray press no
  longer starts a dictation. Stopping is always a single press; push-to-talk and the tray's
  "Dictate" item are unaffected. Can be turned off in Settings → Dictation.
- **Multi-select and batch delete in History** (spec §7): Ctrl/Shift/Ctrl+A, the Delete key and
  the context menu; the whole selection (with its audio) is removed in one transaction.
- **"Support the project"** link (Boosty) in Settings → About, plus a GitHub funding entry.
- **Vocabulary and AI-correction user guide** in Russian: [docs/terms-ai.ru.md](docs/terms-ai.ru.md),
  linked from the README.

### Changed
- Settings → Dictation now follows the macOS layout: the long explanations moved into ⓘ tooltips,
  "Double-tap to start" sits on the hotkey row where it belongs, and the model/language controls
  got their own "Cloud recognition" heading. The window is noticeably shorter.
- History: **Play** becomes **Stop** while audio is playing and turns back when playback ends, so
  there is a visible way to stop it (parity with macOS).

## [0.5.0] - 2026-07-30

### Added
- **STT model and language choice** for the cloud engine (spec §2, Settings → Dictation):
  `whisper-large-v3-turbo` (default) or `whisper-large-v3`, and auto-detect / Russian / English.
  Forcing a language fixes short phrases that auto-detect gets wrong. History records the model
  actually used.
- **Dynamic chat-model resolution with self-healing** for AI term correction (spec §6.1): the
  model is no longer hardcoded — Voica reads the live model list, picks the best available one by
  a priority chain, caches it, migrates retired choices, and re-resolves on a 404 mid-dictation.
  Settings shows the resolved model and offers a picker.
- **History export** to Markdown, CSV or JSON (spec §7) — the whole history, text and metadata,
  no audio. CSV carries a UTF-8 BOM for Excel.
- **About is now a Settings tab** (parity with macOS), together with the updates block:
  "Check now", a download button, and the check-on-launch toggle.

### Changed
- Local engine: adjacent 25 s chunks now overlap by 2 s and the transcripts are stitched with
  seam de-duplication, so a word (and its punctuation) at a chunk boundary in long recordings is
  no longer split or lost. Falls back to a plain join when no overlap is detected.
- A transcription blocked for your Groq org (HTTP 403) now explains which model to allow.

### Fixed
- Self-test no longer runs the retention purge with a future cutoff against the real database,
  which could delete stored recordings.

### Internal
- CI: bumped `actions/checkout` and `actions/setup-dotnet` to v5 (removes the Node 20 deprecation
  warning).

## [0.4.0] - 2026-07-22

### Added
- **Local offline engine** (spec §2.5): recognition fully on-device via GigaAM v3 e2e CTC
  (Russian with punctuation, int8 ONNX through ONNX Runtime) — no network, no API key. The
  model (~215 MB) downloads on demand from this repo's dedicated model release with a progress
  bar and SHA-256 verification; deletable in Settings → Data. First use shows a "preparing the
  model" notice; the model unloads from RAM after 5 minutes idle. Long recordings are chunked.
- **Offline fallback**: when the cloud engine is selected but the network is down and the local
  model is installed, dictation transparently falls back to the local engine with a small notice.
- **Tabbed Settings** (General / Dictation / Vocabulary / Data) like the macOS app — fits small
  displays.
- History gains a **Model** column; the local engine reports its language as "Russian" for
  consistency with Groq.

### Changed
- About window mentions both engines and the on-device privacy story.

## [0.3.1] - 2026-07-17

### Fixed
- **AI term correction was silently broken**: Groq removed the `qwen/qwen3-32b` model (HTTP 404),
  so corrections fell back to the raw transcription. Switched to **`llama-3.3-70b-versatile`**
  per the updated spec (§6.1). The availability check in Settings now distinguishes a removed
  model (404 → "update the app") from one blocked in your Groq org (403 → "allow it in the
  console").

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

[0.5.0]: https://github.com/Inhum/voica-win/releases/tag/v0.5.0
[0.4.0]: https://github.com/Inhum/voica-win/releases/tag/v0.4.0
[0.3.1]: https://github.com/Inhum/voica-win/releases/tag/v0.3.1
[0.3.0]: https://github.com/Inhum/voica-win/releases/tag/v0.3.0
[0.2.0]: https://github.com/Inhum/voica-win/releases/tag/v0.2.0
[0.1.0]: https://github.com/Inhum/voica-win/releases/tag/v0.1.0
