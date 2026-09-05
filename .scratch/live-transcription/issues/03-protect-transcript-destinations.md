# 03: Protect transcript destinations and append safely

**What to build:** Safe file handling around the live session so an agent can choose a new or existing Markdown destination without accidental overwrite, can read it while capture continues, and receives an immediate terminal failure if transcription can no longer be persisted.

**Blocked by:** 02: Run and control a basic live transcription session.

**Status:** ready-for-agent

- [ ] `start` resolves a relative output path against the caller's working directory before detaching and reports the absolute path in session results.
- [ ] Missing parent directories are created before the session is declared ready.
- [ ] A new transcript is written as UTF-8 and begins with `# Live Transcript` followed by source-labeled paragraphs.
- [ ] An existing destination is rejected unless `--append` is supplied, and rejection leaves no transcription worker running.
- [ ] Append mode preserves all existing bytes and starts the new session with a thematic break followed by `# Live Transcript`.
- [ ] Every finalized utterance is flushed so another process can read the growing document during capture.
- [ ] The worker does not claim to support concurrent external modification of the document.
- [ ] If the document disappears or becomes unwritable, capture and recognition stop immediately and `status` retains a terminal write-failure result.
- [ ] Command-level tests cover nested directory creation, caller-relative paths, overwrite refusal, append formatting, concurrent reads, flush visibility, deletion, and loss of write access using real temporary files.
