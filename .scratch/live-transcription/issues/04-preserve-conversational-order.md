# 04: Preserve conversational order across recognition streams

**What to build:** A deterministic merge of microphone and playback recognition results so the Markdown transcript reflects when people spoke rather than the order in which two independent Azure callbacks happened to complete.

**Blocked by:** 02: Run and control a basic live transcription session.

**Status:** ready-for-agent

- [ ] Finalized results from both recognition streams carry internal monotonic audio offsets into one transcript-ordering component.
- [ ] Results are emitted in offset order after an approximately 500 millisecond holdback that allows the other source to catch up.
- [ ] Internal offsets and holdback details never appear as visible transcript timestamps or metadata.
- [ ] Empty, interim, and no-match results cannot delay or create transcript paragraphs.
- [ ] Recognized profanity remains unchanged in the emitted paragraph.
- [ ] Each emitted paragraph is flushed and the expected live transcript latency remains approximately two to five seconds under normal Azure finalization timing.
- [ ] Command-level tests deliberately complete callbacks out of order and verify stable conversational ordering across `You` and `Meeting` without asserting private queue implementation details.
- [ ] No echo-cancellation or duplicate-suppression heuristic is introduced.
