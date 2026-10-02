# Voica for Windows — Roadmap

What's ahead, and what we deliberately decided **not** to do (recorded so the question doesn't get
reopened from scratch). What already shipped is in [CHANGELOG.md](../CHANGELOG.md).

Current as of **0.9.0** (September 2026). The macOS app (`Inhum/voica`) has its own numbering and its
own [ROADMAP](https://github.com/Inhum/voica/blob/main/docs/ROADMAP.md); this file mirrors it for
the Windows side. Per the parity rule, anything cross-platform lands in
[CORE-SPEC.md](CORE-SPEC.md) first, then in both implementations.

## Distribution and signing

- **Not code-signed.** SmartScreen shows "Windows protected your PC" on first run → *More info →
  Run anyway*. Documented in the [README](../README.md#why-does-windows-warn-about-this-app),
  together with how to verify the download against the release.
- **A self-signed certificate — decided against, and this is where the platforms differ.** macOS
  signs with its own "Voica Self-Signed" cert for one concrete reason: the signature is a stable
  identity, and macOS ties the Accessibility grant to it, so without it every update would ask the
  user for permissions again. **Windows has no such tie.** The global hook and `SendInput` need no
  grant at all, nothing is bound to a publisher identity, and SmartScreen trusts a chain to a
  trusted root — which a self-signed cert does not have. So signing with one would change the first
  run from "unknown publisher" to "untrusted publisher": the same click, plus a certificate to keep,
  back up and rotate. The only way to make it count would be asking users to install our root
  certificate, which is a far bigger ask than one click and is not something an app should teach
  people to do.
- **Releases are built by CI from the pushed tag** ([release.yml](../.github/workflows/release.yml)),
  not from a developer machine, so a release is reproducible from its commit — a prerequisite for
  **SignPath Foundation**, the free certificate program for OSS. Signing gets added once the
  project is accepted there. Until then what substitutes for a signature is provenance, not trust
  in a certificate: the release author is `github-actions[bot]`, and every asset carries a
  `sha256:` digest the user can check against their download.
- **Buying a certificate — decided against** (before 1.0 at least): ~$100–400/year for an OV/EV
  cert, or Azure Trusted Signing, which needs a verifiable legal entity. macOS reached the same
  answer for notarization ($99/year plus a developer status unavailable to an RF citizen), so both
  platforms accept the same friction: one extra click on first run.
- Two artifacts per release stay as they are: self-contained (~80 MB, nothing to install) and
  framework-dependent (~37 MB, needs .NET 8 Desktop Runtime), plus an Inno Setup installer.
- **Anything a user can see goes out as a release candidate first** (spec §13, since 0.9.0): the tag
  `vX.Y.Z-rc.N` is published as a pre-release, which GitHub keeps out of `/releases/latest`, so the
  update check never offers it to ordinary users while a tester can still say which build they run.

## Auto-updates

**Checking is done** (Settings → About, spec §10): the app reads GitHub Releases anonymously once
a day, compares versions, and offers a download button. It never downloads or installs anything
itself — the release page opens in the browser.

A check that fails in a closed network **stays silent and takes the daily slot** (0.9.0): it would
otherwise fail at every launch, and retrying helps nobody. A check the user asked for still reports
its error, and names the proxy when that is what refused.

"Updates itself" would mean **Velopack** (one system covering Windows and macOS) or a
platform-specific updater. Both want a real signature, so this sits behind the decision above —
**after 1.0**, if at all.

## Cross-platform parity

Two native codebases (Swift/AppKit and C#/WPF) held together by a document, not by shared code.
As of 0.9.0 the parity matrix at the end of [CORE-SPEC.md](CORE-SPEC.md) again has **no open rows on
the Windows side**; where the platforms differ on purpose the row is marked 🔀.

Feature sets stay in lockstep, **version numbers do not** — each platform numbers independently and
each will reach its own "1.0". The flow keeps going both ways, which is the parity rule working as
intended rather than a one-way port. Windows → macOS so far: chunk overlap with seam de-duplication;
the multi-monitor rule for the dictation bar; retrying a refused correction inside the same dictation
(§6.1, macOS 0.9.20); and the discovery that Groq reports a withdrawn model as `400
model_decommissioned` as well as `404`.

What this costs, recorded after a day of it (2026-09-17): a round trip per finding is expensive,
because a human carries the briefs between the two repositories. Anything found on one side that
affects the other's code is fixed in the same pass and reported once, and fixes are released in
batches rather than one release per fix.

## Possible features

Struck-through items are done — kept visible so the question reads as closed, with the version that
closed it.

- ~~Auto-insert into the focused field~~ — 0.1.0. ~~Vocabulary hint (Whisper `prompt`)~~ — 0.1.0.
- ~~AI term correction (Groq chat model, opt-in)~~ — 0.2.0, with dynamic model resolution and
  self-healing in 0.5.0 (no hardcoded model any more). Separately, **free-form LLM formatting** of
  the text — paragraphs, lists, filler-word cleanup — remains an idea **waiting for demand**.
- ~~Tabbed Settings~~ — 0.4.0. ~~About as a Settings tab~~ — 0.5.0.
- ~~STT model and language choice~~ — 0.5.0. ~~History export (Markdown / CSV / JSON)~~ — 0.5.0.
  ~~Search over history~~ — 0.7.0 (over the final text and the pre-correction `raw_text`).
- ~~Local offline engine (GigaAM v3 on ONNX Runtime, int8)~~ — 0.4.0.
- ~~Dictation bar with cancel, double-tap to start, multi-select in History~~ — 0.6.0.
- ~~Text rules that need no model: fillers, unpaired quotes, deterministic term fixing, each with its
  own switch~~ — 0.8.0 (spec §6.2/§6.3/§6.4).
- ~~Leaving the clipboard alone~~ — 0.10.0 (issue #2, spec §5): the clipboard is borrowed for the
  paste and returned. **Typing the text as keystrokes instead — decided against:** it is the only
  transport that never touches the clipboard, but it behaves differently in every application
  (auto-brackets in editors, autocorrect in Word, a line break sends the message in a chat). What
  the returned clipboard cannot be is the source's own again — Excel pastes values, Word applies its
  "other programs" rule — and no restorer can change that, so giving the clipboard back is opt-in.
- ~~Working through an authenticated corporate proxy, with a Network tab and a route line~~ — 0.9.0
  (spec §9.5/§11.4), verified in a real corporate network. **Installing the local model by hand**
  is documented and checksum-verified, but has not yet been done by anyone in such a network.
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
- **Free-form LLM formatting** — waits for demand.
