# 05: Recover from brief Azure Speech interruptions

**What to build:** Resilient recognition that survives a short Azure interruption without writing raw audio to disk, reports degradation while recovery is underway, and visibly discloses any speech that could not be recovered.

**Blocked by:** 04: Preserve conversational order across recognition streams.

**Status:** resolved

- [x] A recoverable Azure interruption moves the affected source into a degraded state visible through `status` while the other source continues when possible.
- [x] The worker retains at most 30 seconds of audio in memory independently for `You` and `Meeting` while reconnecting.
- [x] Buffered audio is replayed through the restored recognizer and its finalized results rejoin the normal monotonic ordering flow.
- [x] Raw or encoded audio is never persisted to disk, session state, status, or diagnostic logs.
- [x] When a source exceeds its buffer or otherwise loses buffered speech, the transcript appends exactly one source-specific blockquote after recovery stating that transcription was interrupted and some speech may be missing.
- [x] Loss state remains observable through `status` after recognition recovers.
- [x] Controlled command-level tests cover recovery within the limit, overflow beyond the limit, independent source interruption, repeated failures within one interruption, marker placement, and the absence of persisted audio.

## Answer

Each recognition source now has an independent 30-second PCM buffer and recovery state. Recoverable Azure cancellations degrade only the affected source, replay buffered audio through the restored recognizer, rebase restarted recognition offsets onto the original audio timeline, and hold later cross-source results until replay has rejoined monotonic transcript ordering.

Overflow and stop-during-recovery loss remain visible in status and append one source-specific blockquote after recoverable speech. Session-state writes are serialized so stale degraded or recovered states cannot overwrite newer loss information. Command-level coverage exercises recovery, source independence, repeated interruption notifications, overflow, replay ordering, marker placement, stop during recovery, and the absence of persisted audio.
