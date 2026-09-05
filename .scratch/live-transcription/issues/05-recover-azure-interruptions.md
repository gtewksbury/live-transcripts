# 05: Recover from brief Azure Speech interruptions

**What to build:** Resilient recognition that survives a short Azure interruption without writing raw audio to disk, reports degradation while recovery is underway, and visibly discloses any speech that could not be recovered.

**Blocked by:** 04: Preserve conversational order across recognition streams.

**Status:** ready-for-agent

- [ ] A recoverable Azure interruption moves the affected source into a degraded state visible through `status` while the other source continues when possible.
- [ ] The worker retains at most 30 seconds of audio in memory independently for `You` and `Meeting` while reconnecting.
- [ ] Buffered audio is replayed through the restored recognizer and its finalized results rejoin the normal monotonic ordering flow.
- [ ] Raw or encoded audio is never persisted to disk, session state, status, or diagnostic logs.
- [ ] When a source exceeds its buffer or otherwise loses buffered speech, the transcript appends exactly one source-specific blockquote after recovery stating that transcription was interrupted and some speech may be missing.
- [ ] Loss state remains observable through `status` after recognition recovers.
- [ ] Controlled command-level tests cover recovery within the limit, overflow beyond the limit, independent source interruption, repeated failures within one interruption, marker placement, and the absence of persisted audio.
