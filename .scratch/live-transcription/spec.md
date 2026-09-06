# Live Transcript CLI

Status: ready-for-agent

## Problem Statement

An agent needs to start and control live transcription of meetings running in desktop applications such as Microsoft Teams and Zoom. The transcript must include both the local user's microphone and the meeting audio played by Windows, distinguish those sources as `You` and `Meeting`, and continuously write stable text to a caller-selected Markdown document.

Existing meeting-platform transcription is tied to a specific application, tenant policy, or user interface. The agent instead needs a platform-independent local command that it can invoke, observe, and stop without remaining attached to a foreground terminal. The command must avoid silently losing speech, overwriting files, recording raw audio, or continuing to incur Azure cost after the transcript can no longer be written.

## Solution

Build a Windows 11 command-line application that captures the current Windows communications microphone and playback endpoint as separate audio sources. The application streams both sources independently to an approved Azure AI Speech resource, recognizes fixed US English speech, and appends each finalized utterance to a Markdown document under a `You` or `Meeting` label.

The command starts a detached transcription session and returns a machine-readable session identifier after audio devices, the output document, and both Azure recognition streams are ready. Separate commands report status, stop the session, and list available audio devices. Only one session may run for a Windows user at a time, and every session stops after at most two hours.

The application is delivered as a self-contained .NET 10 Windows x64 executable at a predictable build-artifact location. Agents invoke that executable by its full path; installation and `PATH` modification are unnecessary.

## User Stories

1. As an agent, I want to start transcription with a Markdown output path, so that meeting speech becomes available in a document I can read.
2. As an agent, I want the transcription worker to detach from my command process, so that I can continue performing other work during the meeting.
3. As an agent, I want `start` to return a session identifier, so that I can correlate later lifecycle operations with the session I created.
4. As an agent, I want command results on standard output to be JSON, so that I can consume them without parsing prose.
5. As an agent, I want diagnostics on standard error and meaningful exit codes, so that success data remains machine-readable.
6. As an agent, I want `start` to succeed only after capture and recognition are ready, so that a returned session is actually recording.
7. As an agent, I want startup to time out after 15 seconds, so that failed initialization does not hang indefinitely.
8. As an agent, I want failed startup to leave no background worker, so that retries do not collide with abandoned processes.
9. As an agent, I want to query the current session without supplying an identifier, so that the common one-session workflow is simple.
10. As an agent, I want to optionally supply a session identifier to `status` and `stop`, so that stale or incorrect session references fail explicitly.
11. As an agent, I want to stop a session gracefully, so that in-flight finalized speech reaches the Markdown document.
12. As an agent, I want shutdown to finish within five seconds, so that `stop` cannot wait indefinitely for Azure.
13. As an agent, I want the final session state and stop reason retained after exit, so that I can distinguish a clean stop, timeout, device failure, and other errors.
14. As an agent, I want the last terminal status retained until the next successful session begins, so that short-lived failures remain observable.
15. As an agent, I want only one active session per Windows user, so that audio is not captured twice and Azure charges are not duplicated.
16. As an operator, I want sessions limited to two hours, so that an abandoned worker cannot transcribe indefinitely.
17. As an agent, I want the two-hour limit recorded in both the transcript and status, so that automatic shutdown is not mistaken for failure.
18. As an agent, I want the current Windows communications devices selected by default, so that ordinary headset and speaker setups require no configuration.
19. As an operator, I want to connect a headset before starting and have its Windows defaults used, so that meetings work without device arguments.
20. As an agent, I want to list available microphone and playback endpoints, so that non-default devices can be selected when necessary.
21. As an agent, I want optional microphone and playback device-ID overrides, so that I can capture a meeting application configured away from Windows defaults.
22. As an operator, I want selected devices pinned for the session, so that labels do not silently begin representing different sources.
23. As an agent, I want the session to fail clearly if a selected device disappears, so that missing audio is not silently ignored.
24. As an operator, I want microphone speech labeled `You`, so that my contributions are distinguishable from meeting playback.
25. As an operator, I want playback speech labeled `Meeting`, so that remote contributions remain distinct without promising participant identities.
26. As an agent, I want finalized utterances only, so that I never process changing interim text twice.
27. As an agent, I want each finalized utterance flushed promptly, so that the document trails the conversation by only a few seconds.
28. As a reader, I want each utterance in its own source-labeled Markdown paragraph, so that the transcript remains easy to scan.
29. As a reader, I want no visible timestamps, so that the transcript remains concise.
30. As a reader, I want profanity preserved as recognized, so that the transcript does not silently alter speech.
31. As an agent, I want utterances from the two recognition streams ordered conversationally, so that callback timing does not scramble the transcript.
32. As an agent, I want a new transcript to begin with a clear heading, so that the document is recognizable without extra metadata.
33. As an agent, I want appended sessions separated visibly, so that multiple meetings in one document do not run together.
34. As an agent, I want an existing output document rejected unless I explicitly request append mode, so that prior content is not overwritten accidentally.
35. As an agent, I want missing parent directories created, so that I can provide a new nested output path directly.
36. As an agent, I want relative output paths resolved before the worker detaches, so that the background process writes where the caller intended.
37. As an agent, I want to read the Markdown document while transcription is active, so that I can act on the live conversation.
38. As an operator, I want raw audio to remain in memory only, so that no recoverable meeting recording is left on disk.
39. As an agent, I want brief Azure interruptions buffered for up to 30 seconds per source, so that transient failures do not immediately lose speech.
40. As a reader, I want a visible interruption marker when buffered audio is dropped, so that transcript omissions are not hidden.
41. As an agent, I want degraded recognition reflected in status, so that I can detect trouble before the session ends.
42. As an agent, I want transcription to stop if the Markdown document becomes unwritable, so that Azure usage cannot continue while output is lost.
43. As an operator, I want diagnostic logs to exclude transcript text and audio, so that operational troubleshooting does not duplicate sensitive meeting content.
44. As an operator, I want Azure credentials accepted through executable-local configuration rather than command arguments, so that secrets are not exposed in process listings or session state.
45. As an operator, I want the application to use an existing approved Azure resource, so that it does not provision or select cloud infrastructure itself.
46. As an agent developer, I want a self-contained executable at a predictable path, so that agents can invoke it without installing a .NET runtime or modifying `PATH`.

## Implementation Decisions

- Target Windows 11 on x64 and .NET 10. Publish a self-contained executable with a stable executable name and predictable build-artifact location.
- Treat this repository as the product repository. Use a conventional .NET solution with production and test projects rather than a monorepo-shaped application directory.
- Use NAudio for Windows audio capture. Capture the communications microphone and WASAPI loopback playback independently.
- Capture the entire selected playback endpoint. Application-specific isolation for Teams, Zoom, or other meeting applications is not part of the MVP.
- Resolve the default communications microphone and playback endpoint at startup. Allow optional microphone and playback endpoint IDs returned by the device-listing command.
- Pin resolved endpoints for the session. Do not follow Windows default-device changes. A removed or invalidated endpoint is a terminal session error.
- Use two independent Azure AI Speech recognition streams so source identity is preserved without remote-speaker diarization.
- Use fixed `en-US` recognition with no language option or automatic language detection.
- Configure Azure Speech with `AzureSpeech:Key` and `AzureSpeech:Region` from `appsettings.json` beside the executable. Do not pass or echo the key in command arguments, results, session state, or diagnostics.
- Assume the Azure Speech resource already exists and is approved. Do not provision Azure infrastructure.
- Preserve profanity in recognition output. Ignore interim hypotheses and no-match events; only finalized non-empty utterances enter the transcript.
- Merge finalized results from both recognition streams using monotonic audio offsets and an approximately 500 millisecond holdback. Internal timing exists only for ordering and is never rendered in Markdown.
- Do not implement heuristic echo or duplicate suppression. Recommend headset use where acoustic bleed causes duplicate recognition.
- Expose `start`, `status`, `stop`, and `devices` commands. `start` requires an output path and accepts `--append`, `--microphone`, and `--playback`. `status` and `stop` accept an optional session identifier.
- Emit exactly one JSON result object to standard output for every command. Send human-readable diagnostics to standard error and use non-zero exit codes for failures.
- `start` launches a detached worker but does not report success until the output document, both audio captures, and both Azure recognizers are ready. Enforce a 15-second readiness timeout and terminate incomplete workers.
- Return a machine-readable session identifier from `start`. With at most one active session, omitted identifiers on `status` and `stop` target the sole session. An explicitly supplied mismatched identifier fails.
- Enforce one active session per Windows user with operating-system process coordination rather than relying only on a state file.
- Use session states sufficient to distinguish startup, healthy capture, degraded capture, graceful shutdown, successful completion, and terminal failure.
- Retain a sanitized terminal status record until the next session successfully starts. It may include the session identifier, absolute output path, selected device identifiers, elapsed duration, stop reason, loss indicators, and last error, but not credentials or transcript content.
- Resolve output paths relative to the caller's working directory before detaching and create missing parent directories.
- Create a new output document without overwriting an existing path. Existing paths require explicit `--append`; append mode never truncates prior content.
- Write UTF-8 Markdown. A new session begins with `# Live Transcript`. In append mode, precede the new heading with a thematic break.
- Render every finalized result as its own paragraph in the form `**You:** <text>` or `**Meeting:** <text>`. Do not render timestamps or other session metadata.
- Flush the document after every utterance. Permit concurrent readers while the worker writes; concurrent external modification is unsupported.
- After a recoverable recognition interruption, buffer at most 30 seconds of audio in memory per source while reconnecting. Never write raw audio to disk.
- If buffered audio must be discarded, append one source-specific blockquote stating that transcription was interrupted and some speech may be missing. Expose the degraded or loss state through `status`.
- If the output document becomes unwritable, stop capture and recognition immediately and retain a terminal failure status.
- `stop` first stops capture, then allows up to five seconds for in-flight finalized results before flushing and exiting. Report whether shutdown completed cleanly or timed out.
- Automatically perform graceful shutdown at two hours, append a blockquote stating that the session limit was reached, and retain `duration-limit` as the stop reason.
- Keep rotating operational logs in per-user application data. Logs may contain lifecycle events, selected device IDs, Azure error codes, and write failures, but never audio or recognized text.
- Do not require a consent acknowledgement flag. The operator remains responsible for authorization and participant consent.
- Keep the executable at a documented repository-relative artifact location. Agents invoke the full executable path; do not add an installer, global tool package, or `PATH` mutation in the MVP.

## Testing Decisions

- Use one primary seam at the CLI command interface. Tests issue the same `start`, `status`, `stop`, and `devices` operations as an agent and assert JSON results, process lifecycle, session state, and Markdown output.
- Keep NAudio capture, Azure recognition, monotonic time, and detached worker control behind internal interfaces supplied to the command-level application module. Production and controlled test adapters are the two implementations that make each seam real.
- Prefer behavior-level tests spanning command handling, session orchestration, and actual temporary files. Do not assert private classes, callback wiring, SDK call order, or internal queue structure.
- Test new-document creation, append mode, existing-file refusal, parent-directory creation, relative-path resolution, UTF-8 output, headings, session separators, labels, flushing, and interruption markers through observable files and command results.
- Test source ordering with controlled recognition results whose completion order differs from their monotonic audio offsets.
- Test one-session enforcement, optional and mismatched session identifiers, readiness timeout, graceful stop, stop timeout, two-hour termination, retained terminal status, stale-worker detection, and output-write failure through controlled adapters.
- Test transient Azure interruption, successful replay within the in-memory limit, buffer overflow, source-specific loss reporting, and terminal recognition failure without persisting audio.
- Test default device resolution, explicit endpoint selection, device enumeration, and endpoint disappearance through a controlled Windows-audio adapter.
- Test that JSON is the only standard-output content, failures use non-zero exit codes, and diagnostic text is isolated to standard error.
- Test that status and logs never contain the Azure key, transcript text, or audio data.
- Add a small Windows-only smoke test for executable startup and command parsing. Keep real-device and real-Azure verification as an explicit manual smoke procedure because those systems are nondeterministic and environment-dependent.
- There is no existing application or test prior art in this greenfield repository. Establish command-level behavior tests as the repository's initial testing convention.

## Out of Scope

- macOS, Linux, Windows on ARM, and Windows versions earlier than Windows 11.
- Meeting bots, calendar integration, joining meetings automatically, or using Teams and Zoom platform APIs.
- Capturing only one application's playback when other applications share the selected Windows endpoint.
- Named remote participants, remote-speaker diarization, or mapping speech to Teams or Zoom identities.
- Automatic language detection, multilingual meetings, and a recognition-language command option.
- Interim transcript text, live rewriting of partial hypotheses, visible timestamps, summaries, action-item extraction, and transcript post-processing.
- Heuristic echo cancellation or duplicate utterance suppression.
- Persisting raw audio, durable offline transcription queues, or later reprocessing of meeting recordings.
- More than one simultaneous transcription session per Windows user.
- Runtime consent prompts or consent-attestation command options.
- Azure resource provisioning, Azure deployment automation, and credential management beyond executable-local configuration.
- A system service, graphical interface, notification-area application, installer, .NET global tool, package feed, or automatic `PATH` configuration.
- Supporting concurrent external edits to the transcript while capture is active.

## Further Notes

- Headphones should be connected before starting. When Teams or Zoom uses `System default`, the current Windows communications endpoints should be selected automatically. Explicit device IDs remain an escape hatch when the meeting application is configured to different endpoints.
- Whole-endpoint loopback capture includes notifications, music, and audio from unrelated applications. This limitation must be documented prominently.
- The target transcript latency is approximately two to five seconds, governed by Azure's finalized-utterance timing rather than interim hypotheses.
- The initial implementation should optimize for a simple agent contract and reliable failure reporting rather than expanding configuration options.