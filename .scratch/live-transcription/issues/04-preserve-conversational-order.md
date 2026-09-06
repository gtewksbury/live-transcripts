# 04: Preserve conversational order across recognition streams

**What to build:** A deterministic merge of microphone and playback recognition results so the Markdown transcript reflects when people spoke rather than the order in which two independent Azure callbacks happened to complete.

**Blocked by:** 02: Run and control a basic live transcription session.

**Status:** resolved

- [x] Finalized results from both recognition streams carry internal monotonic audio offsets into one transcript-ordering component.
- [x] Results are emitted in offset order after an approximately 500 millisecond holdback that allows the other source to catch up.
- [x] Internal offsets and holdback details never appear as visible transcript timestamps or metadata.
- [x] Empty, interim, and no-match results cannot delay or create transcript paragraphs.
- [x] Recognized profanity remains unchanged in the emitted paragraph.
- [x] Each emitted paragraph is flushed and the expected live transcript latency remains approximately two to five seconds under normal Azure finalization timing.
- [x] Command-level tests deliberately complete callbacks out of order and verify stable conversational ordering across `You` and `Meeting` without asserting private queue implementation details.
- [x] No echo-cancellation or duplicate-suppression heuristic is introduced.

## Answer

Azure finalized results now retain their monotonic audio offsets and enter a shared 500 millisecond holdback buffer. Each batch is deterministically ordered by offset before source-labeled Markdown paragraphs are appended and flushed; pending results are drained during graceful stop. Empty results are discarded before the holdback begins, and offsets remain internal.

Command-level coverage completes `You` and `Meeting` callbacks out of order, verifies no paragraph appears before holdback release, and asserts exact timestamp-free Markdown ordering with profanity unchanged. `SessionCommandTests` passes 14 of 14 tests.
