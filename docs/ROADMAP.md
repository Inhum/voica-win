# Voica — Roadmap / Future scope

Ideas not yet in scope. Per the parity rule, anything cross-platform must be added to
[CORE-SPEC.md](CORE-SPEC.md) first, then mirrored into each OS implementation.

## Live (streaming) dictation — text visible while you speak

**Requested by users:** see the text appear in real time as they talk, instead of after they stop.

Today Voica is **batch**: record the whole clip → upload the WAV → Groq Whisper returns the final
text (spec §2/§3). This cannot show text mid-utterance by design — the model only sees the finished
recording.

Live dictation needs **streaming ASR** that emits interim (partial) hypotheses as audio streams in:
audio is sent in chunks (typically over a websocket) and text is progressively inserted/revised.

Backend options:
- **Cloud streaming STT:** Deepgram, AssemblyAI Realtime, Azure / Google streaming Speech, OpenAI Realtime.
- **Local streaming:** whisper.cpp (streaming mode), Vosk; NVIDIA's streaming ASR is **Parakeet / Canary**
  (via NeMo / Riva).
- **Not** Nemotron — that is an NVIDIA **LLM** (text generation/reasoning), not a speech-to-text model.
- **Groq** currently exposes only batch Whisper (no streaming STT endpoint), so a live mode would use a
  different, Settings-selectable backend; batch Groq stays the default.

Hard parts to design:
- Rendering interim results and **replacing** already-inserted text in an arbitrary focused field —
  there is no universal "replace last N characters" across apps. Likely needs either the result
  window for live mode, or careful `SendInput` backspacing, or a dedicated overlay.
- Latency/cost trade-offs and provider selection in Settings.

## LLM post-processing / glossary replacement

From the core spec's own roadmap: hard term replacement (beyond the Whisper `prompt` hint, spec §6)
via an LLM post-processing pass or a glossary of forced substitutions. A common future feature for
both platforms.
