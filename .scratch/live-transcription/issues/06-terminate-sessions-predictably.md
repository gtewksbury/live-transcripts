# 06: Make every session terminate predictably

**What to build:** Bounded and inspectable lifecycle behavior for startup, requested shutdown, duration limits, stale workers, device loss, and terminal recognition failures so an agent is never left with an ambiguous or unbounded transcription process.

**Blocked by:** 03: Protect transcript destinations and append safely; 05: Recover from brief Azure Speech interruptions.

**Status:** ready-for-agent

- [ ] Startup has a 15-second readiness deadline covering the output, both audio captures, and both Azure recognizers.
- [ ] Any startup failure or timeout terminates the incomplete worker, releases the per-user session lock, returns a clear non-zero result, and permits an immediate retry.
- [ ] `stop` stops new capture, waits no more than five seconds for in-flight finalized recognition, flushes the document, and reports whether finalization completed or timed out.
- [ ] A selected endpoint disappearing ends the session with a terminal device error and never switches silently to another endpoint.
- [ ] A non-recoverable Azure error ends the session with the source and sanitized Azure error information available in status.
- [ ] Output-write failures from the transcript destination end the session without continuing capture or Azure usage.
- [ ] Every session begins graceful shutdown at two hours, appends `> Transcription stopped: two-hour session limit reached.`, and retains `duration-limit` as its stop reason.
- [ ] Terminal status distinguishes clean stop, stop timeout, duration limit, device failure, Azure failure, write failure, and startup failure.
- [ ] One sanitized completed-session record remains queryable until the next session successfully starts and includes the session ID, absolute output path, final state, stop reason, selected device IDs, elapsed duration, loss indicators, and error where applicable.
- [ ] Session coordination detects stale worker state and recovers without allowing two live workers for one Windows user.
- [ ] Deterministic command-level tests use controlled time and worker adapters to verify every deadline and terminal state without real waiting or cloud access.
