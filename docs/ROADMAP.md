# Voica for Windows — Roadmap

What's ahead, and what we deliberately decided **not** to do (recorded so the question doesn't get
reopened from scratch). What already shipped is in [CHANGELOG.md](../CHANGELOG.md).

Current as of **0.6.0** (August 2026). The macOS app (`Inhum/voica`) has its own numbering and its
own [ROADMAP](https://github.com/Inhum/voica/blob/main/docs/ROADMAP.md); this file mirrors it for
the Windows side. Per the parity rule, anything cross-platform lands in
[CORE-SPEC.md](CORE-SPEC.md) first, then in both implementations.

## Distribution and signing

- **Not code-signed.** SmartScreen shows "Windows protected your PC" on first run → *More info →
  Run anyway*. Documented in the [README](../README.md#why-does-windows-warn-about-this-app),
  together with how to verify the download against the release.
- **Releases are built by CI from the pushed tag** ([release.yml](../.github/workflows/release.yml)),
  not from a developer machine, so a release is reproducible from its commit — a prerequisite for
  **SignPath Foundation**, the free certificate program for OSS. Signing gets added once the
  project is accepted there.
- **Buying a certificate — decided against** (before 1.0 at least): ~$100–400/year for an OV/EV
  cert, or Azure Trusted Signing, which needs a verifiable legal entity. macOS reached the same
  answer for notarization ($99/year plus a developer status unavailable to an RF citizen), so both
  platforms accept the same friction: one extra click on first run.
- Two artifacts per release stay as they are: self-contained (~80 MB, nothing to install) and
  framework-dependent (~37 MB, needs .NET 8 Desktop Runtime).

## Auto-updates

**Checking is done** (Settings → About, spec §10): the app reads GitHub Releases anonymously once
a day, compares versions, and offers a download button. It never downloads or installs anything
itself — the release page opens in the browser.

"Updates itself" would mean **Velopack** (one system covering Windows and macOS) or a
platform-specific updater. Both want a real signature, so this sits behind the decision above —
**after 1.0**, if at all.

## Cross-platform parity

Two native codebases (Swift/AppKit and C#/WPF) held together by a document, not by shared code.
As of 0.6.0 the parity matrix at the end of [CORE-SPEC.md](CORE-SPEC.md) has **no open rows on the
Windows side**; where the platforms differ on purpose the row is marked 🔀.

Feature sets stay in lockstep, **version numbers do not** — each platform numbers independently and
each will reach its own "1.0". Twice now the flow has gone Windows → macOS (chunk overlap with seam
de-duplication; the multi-monitor rule for the dictation bar), which is the parity rule working as
intended rather than a one-way port.

## Possible features

Struck-through items are done — kept visible so the question reads as closed, with the version that
closed it.

- ~~Auto-insert into the focused field~~ — 0.1.0. ~~Vocabulary hint (Whisper `prompt`)~~ — 0.1.0.
- ~~AI term correction (Groq chat model, opt-in)~~ — 0.2.0, with dynamic model resolution and
  self-healing in 0.5.0 (no hardcoded model any more). Separately, **free-form LLM formatting** of
  the text — paragraphs, lists, filler-word cleanup — remains an idea **waiting for demand**.
- ~~Tabbed Settings~~ — 0.4.0. ~~About as a Settings tab~~ — 0.5.0.
- ~~STT model and language choice~~ — 0.5.0. ~~History export (Markdown / CSV / JSON)~~ — 0.5.0.
  **Search over history — still open.**
- ~~Local offline engine (GigaAM v3 on ONNX Runtime, int8)~~ — 0.4.0.
- ~~Dictation bar with cancel, double-tap to start, multi-select in History~~ — 0.6.0.
- **Watch GigaAM Multilingual** (Sber, MIT) as a *cloud* STT option: it beats Whisper on Russian
  WER. It becomes relevant if an API host appears (SaluteSpeech or a third party; a bonus would be
  payment with a Russian card).
- **Auto-split of over-long recordings** — before 1.0. Today a recording too long for Groq comes
  back as HTTP 413 and the user is told to split it by hand; the local engine already chunks with
  overlap, so the pieces exist.
- **Transcribe an audio file** ("Transcribe audio file…") — before 1.0. A menu item: pick an
  audio/video file → re-encode to 16 kHz mono (NAudio / Media Foundation, the counterpart of
  AVFoundation on macOS) → transcribe → show in the result window or save a `.txt`. It is a
  different mode from live dictation: a batch utility whose output is a document, not an insert
  into a field. Works with either engine. The risk is blurring the product's focus on dictation.

## Live (streaming) dictation — text visible while you speak

**Requested by users**, and the request came in on the Windows side; the macOS roadmap now carries
the same section, since the feature is cross-platform.

Today Voica is **batch**: record the whole clip → upload → get the final text (spec §2/§3). That
shape cannot show text mid-utterance by design — the model only ever sees a finished recording.
Live dictation needs **streaming ASR** with interim hypotheses: audio goes out in chunks (usually
over a websocket) and text is progressively inserted and revised.

- **Cloud streaming STT:** Deepgram, AssemblyAI Realtime, Azure / Google streaming, OpenAI Realtime.
  **Groq has no streaming endpoint**, so a live mode means a second, Settings-selectable backend
  while batch Groq stays the default.
- **Local streaming:** whisper.cpp in streaming mode, Vosk; NVIDIA's streaming line is
  **Parakeet / Canary** (via NeMo / Riva) — *not* Nemotron, which is an LLM. GigaAM as we run it
  does not stream: the window is a fixed 25 s.
- **The hard part is insertion, not recognition.** An interim hypothesis has to *replace* text
  already typed into someone else's field, and there is no universal "delete the last N characters"
  across Windows apps. Options: live mode only in the result window; careful backspacing via
  synthetic keys; or a dedicated overlay. Plus provider choice and a latency-versus-cost trade-off
  in Settings.

## Diarization ("who is speaking") — analysed, shelved

**Shared decision: not doing it.** The line is drawn at *file transcription yes, diarization no* —
the full reasoning, the licence check for the pyannote models, and the pipeline that would be used
if we ever return live in the [macOS roadmap](https://github.com/Inhum/voica/blob/main/docs/ROADMAP.md)
(analysis dated 2026-08-14). It only ever arises inside file transcription: a dictation has one
speaker and nothing to label. Once there are "speaker 1" and "speaker 2", the next asks are
timecodes, subtitles and meeting summaries — a different product.

Windows-specific note, should it ever come back: **`sherpa-onnx`** ships diarization on ready-made
ONNX models, and ONNX Runtime is already a dependency here — so the fallback path the macOS notes
describe is the *straightforward* path on this platform.

## Open questions (including monetization)

- ~~Key model: BYO-key vs a managed backend~~ — **decided: BYO-key.** Removing the key requirement
  means running a backend somebody pays for, i.e. a subscription — exactly what was ruled out. The
  onboarding friction is the price of that choice.
- ~~Signing / notarization — when and on whose money~~ — decided, see above.
- **Donations instead of a subscription** — [Boosty](https://boosty.to/voica), linked from Settings →
  About and the README. No paid features will appear in the app; everything ships to everyone.
- **Search over history** and **free-form LLM formatting** — both wait for demand.
