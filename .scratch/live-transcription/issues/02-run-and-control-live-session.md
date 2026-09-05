# 02: Run and control a basic live transcription session

**What to build:** The first complete live-meeting workflow: an agent starts a detached session using the selected Windows microphone and playback endpoint, receives a session ID after both Azure recognizers are ready, observes it with `status`, reads source-labeled finalized speech from Markdown, and ends it with `stop`.

**Blocked by:** 01: List Windows audio devices from an agent-callable CLI.

**Status:** ready-for-agent

- [ ] `start` accepts an output path plus optional `--microphone` and `--playback` endpoint IDs; omitted IDs resolve to the Windows communications defaults.
- [ ] The selected microphone and playback endpoints are pinned for the life of the session rather than following later default-device changes.
- [ ] Production capture uses NAudio microphone capture for `You` and WASAPI loopback capture for `Meeting`, including all audio played through the selected endpoint.
- [ ] Separate Azure AI Speech streams recognize the two sources using fixed `en-US`, inherited `AZURE_SPEECH_KEY` and `AZURE_SPEECH_REGION` values, and verbatim profanity.
- [ ] `start` detaches the worker and returns a machine-readable session ID only after the output, both captures, and both recognizers report ready.
- [ ] Only finalized, non-empty recognition results are written as separate `**You:**` or `**Meeting:**` Markdown paragraphs; interim and no-match results are ignored.
- [ ] `status` reports the sole active session when no ID is supplied and reports the matching session when the optional ID is supplied; an explicit mismatched ID fails clearly.
- [ ] `stop` supports the same optional-ID behavior, stops capture, permits in-flight finalized speech to be written, flushes the transcript, and exits the worker.
- [ ] Operating-system process coordination prevents more than one active session for the same Windows user.
- [ ] Every command emits exactly one stable JSON result on standard output, sends diagnostics only to standard error, and uses meaningful exit codes.
- [ ] Command-level tests exercise the detached `start`/`status`/`stop` workflow through controlled audio, recognition, and worker adapters while asserting the resulting Markdown and process-visible state.
