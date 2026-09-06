# 02: Run and control a basic live transcription session

**What to build:** The first complete live-meeting workflow: an agent starts a detached session using the selected Windows microphone and playback endpoint, receives a session ID after both Azure recognizers are ready, observes it with `status`, reads source-labeled finalized speech from Markdown, and ends it with `stop`.

**Blocked by:** 01: List Windows audio devices from an agent-callable CLI.

**Status:** complete

- [x] `start` accepts an output path plus optional `--microphone` and `--playback` endpoint IDs; omitted IDs resolve to the Windows communications defaults.
- [x] The selected microphone and playback endpoints are pinned for the life of the session rather than following later default-device changes.
- [x] Production capture uses NAudio microphone capture for `You` and WASAPI loopback capture for `Meeting`, including all audio played through the selected endpoint.
- [x] Separate Azure AI Speech streams recognize the two sources using fixed `en-US`, `AzureSpeech:Key` and `AzureSpeech:Region` values from `appsettings.json`, and verbatim profanity.
- [x] `start` detaches the worker and returns a machine-readable session ID only after the output, both captures, and both recognizers report ready.
- [x] Only finalized, non-empty recognition results are written as separate `**You:**` or `**Meeting:**` Markdown paragraphs; interim and no-match results are ignored.
- [x] `status` reports the sole active session when no ID is supplied and reports the matching session when the optional ID is supplied; an explicit mismatched ID fails clearly.
- [x] `stop` supports the same optional-ID behavior, stops capture, permits in-flight finalized speech to be written, flushes the transcript, and exits the worker.
- [x] Operating-system process coordination prevents more than one active session for the same Windows user.
- [x] Every command emits exactly one stable JSON result on standard output, sends diagnostics only to standard error, and uses meaningful exit codes.
- [x] Command-level tests exercise the detached `start`/`status`/`stop` workflow through controlled audio, recognition, and worker adapters while asserting the resulting Markdown and process-visible state.
