# 07: Provide privacy-safe operational diagnostics

**What to build:** Useful rotating diagnostics for troubleshooting lifecycle and integration failures without creating a second store of meeting content, audio, or credentials.

**Blocked by:** 06: Make every session terminate predictably.

**Status:** ready-for-agent

- [ ] Operational logs rotate under the current user's application-data location with documented size or retention bounds.
- [ ] Logs capture session lifecycle transitions, selected device IDs, sanitized Azure error codes, and transcript write failures with enough context to diagnose the failed operation.
- [ ] Logs never contain recognized transcript text, interim hypotheses, raw or encoded audio, `AzureSpeech:Key`, or other credential values.
- [ ] Command results and retained session status follow the same redaction rules and never expose inherited secret values.
- [ ] Logging failures cannot write diagnostics to standard output or terminate an otherwise healthy transcription session.
- [ ] Automated tests inject recognizable secret, transcript, and audio sentinels through success and failure paths and prove they are absent from logs, status, and command diagnostics.
