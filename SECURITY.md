# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Instead, use GitHub's private
[**Report a vulnerability**](https://github.com/Inhum/voica-win/security/advisories/new) flow, or
email the maintainer.

Include steps to reproduce and the impact. We'll acknowledge as soon as we can and keep you updated
on the fix.

## Scope & design notes

- Voica has **no backend and no telemetry**. Network traffic goes only to `api.groq.com`
  (transcription) and `api.github.com` (optional update checks).
- The Groq API key is stored in `%APPDATA%\Voica\credentials.dat`, encrypted with **Windows DPAPI**
  (CurrentUser scope) — readable only by the same Windows user on the same machine. It is never
  committed to the repo. A `GROQ_API_KEY` environment variable is supported as a development
  fallback.
- Transcription history and audio live under `%APPDATA%\Voica\`. Use **Delete all data…** in
  Settings to wipe everything (history, audio, key, settings).
- Releases are currently **unsigned**; SmartScreen may warn. Verify you downloaded `Voica.exe` from
  the official [releases page](https://github.com/Inhum/voica-win/releases). Code signing via
  SignPath is planned.

## Supported versions

This is an early‑stage project; only the latest release is supported. Please update before
reporting an issue.
