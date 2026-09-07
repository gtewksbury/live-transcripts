# 08: Publish and smoke-test the self-contained Windows executable

**What to build:** A documented, predictable Windows x64 release artifact that an agent can invoke directly by full path, plus the narrow automated and manual checks needed to prove the packaged executable works with Windows audio and an approved Azure Speech resource.

**Blocked by:** 01: List Windows audio devices from an agent-callable CLI; 02: Run and control a basic live transcription session; 03: Protect transcript destinations and append safely; 04: Preserve conversational order across recognition streams; 05: Recover from brief Azure Speech interruptions; 06: Make every session terminate predictably; 07: Provide privacy-safe operational diagnostics.

**Status:** resolved

- [x] The release process publishes one self-contained .NET 10 Windows x64 executable with a stable name to a predictable documented repository-relative artifact location.
- [x] The packaged executable runs on a supported Windows 11 x64 machine without a separately installed .NET runtime, installer, global tool package, or `PATH` change.
- [x] A Windows-only automated smoke test invokes the packaged executable as a child process and verifies command parsing, JSON-only standard output, standard-error isolation, and meaningful exit codes.
- [x] Documentation gives agents the full-path invocation contract for `devices`, `start`, `status`, and `stop`, including append and device override options.
- [x] Documentation covers Azure Speech configuration without showing real credentials, fixed `en-US` recognition, the two-hour limit, and the operator's responsibility for authorization and participant consent.
- [x] Documentation prominently explains headset-first setup, Windows communications defaults, whole-endpoint playback capture, possible unrelated notification audio, `You` versus `Meeting` attribution, lack of named remote speakers, and expected two-to-five-second latency.
- [x] A manual smoke procedure verifies real microphone capture, WASAPI loopback capture, both Azure recognition streams, live concurrent reading, graceful stop, and final status on a Windows 11 meeting setup.

## Answer

Added a deterministic Windows x64 single-file publish script and profile that write `live-transcripts.exe` plus a credential-free executable-local configuration to `artifacts/windows-x64`. Added a Windows-only packaged-process smoke test for successful and invalid commands, documented the full-path agent contract and operating constraints, and provided an operator-run physical-device and Azure Speech smoke procedure. Automated validation passes 36 of 36 tests in Release configuration; real meeting hardware and Azure verification remains the explicit manual release step.