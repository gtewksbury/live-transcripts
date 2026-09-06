# 06: Make every session terminate predictably

**What to build:** Bounded and inspectable lifecycle behavior for startup, requested shutdown, duration limits, stale workers, device loss, and terminal recognition failures so an agent is never left with an ambiguous or unbounded transcription process.

**Blocked by:** 03: Protect transcript destinations and append safely; 05: Recover from brief Azure Speech interruptions.

**Status:** resolved

- [x] Startup has a 15-second readiness deadline covering the output, both audio captures, and both Azure recognizers.
- [x] Any startup failure or timeout terminates the incomplete worker, releases the per-user session lock, returns a clear non-zero result, and permits an immediate retry.
- [x] `stop` stops new capture, waits no more than five seconds for in-flight finalized recognition, flushes the document, and reports whether finalization completed or timed out.
- [x] A selected endpoint disappearing ends the session with a terminal device error and never switches silently to another endpoint.
- [x] A non-recoverable Azure error ends the session with the source and sanitized Azure error information available in status.
- [x] Output-write failures from the transcript destination end the session without continuing capture or Azure usage.
- [x] Every session begins graceful shutdown at two hours, appends `> Transcription stopped: two-hour session limit reached.`, and retains `duration-limit` as its stop reason.
- [x] Terminal status distinguishes clean stop, stop timeout, duration limit, device failure, Azure failure, write failure, and startup failure.
- [x] One sanitized completed-session record remains queryable until the next session successfully starts and includes the session ID, absolute output path, final state, stop reason, selected device IDs, elapsed duration, loss indicators, and error where applicable.
- [x] Session coordination detects stale worker state and recovers without allowing two live workers for one Windows user.
- [x] Deterministic command-level tests use controlled time and worker adapters to verify every deadline and terminal state without real waiting or cloud access.

## Answer

Implemented bounded startup, stop, and two-hour duration deadlines; terminal device, Azure, write, startup, stale-worker, and stop-timeout handling; retained sanitized status metadata; and deterministic CLI-level lifecycle tests with controlled adapters. Validation: `dotnet test LiveTranscripts.slnx --configuration Release --no-restore` passed 35/35 tests before final review cleanup.
