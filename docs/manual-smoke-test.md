# Windows release manual smoke test

Use this procedure for a release candidate on a supported Windows 11 x64 meeting setup. It intentionally exercises physical audio devices and an approved Azure Speech resource, which are not deterministic enough for the automated suite.

## Prerequisites

- Connect a headset before starting.
- Set the headset microphone and playback endpoints as the Windows communications defaults.
- Configure Teams, Zoom, or the chosen meeting application to use `System default`, or record its explicit endpoint IDs for overrides.
- Confirm the operator is authorized to use the Azure Speech resource and has obtained any required participant consent.
- Close or mute applications that could play unrelated notifications. WASAPI loopback captures the entire selected playback endpoint.

## Procedure

1. Publish from the repository root and configure the generated `appsettings.json` with the approved resource key and Azure region:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-windows.ps1
   $LiveTranscripts = (Resolve-Path ".\artifacts\windows-x64\LiveTranscripts.exe").Path
   ```

2. List devices and confirm the intended headset microphone and playback endpoint have `isDefaultCommunications` set to `true`:

   ```powershell
   & $LiveTranscripts devices
   ```

3. Start a meeting with another participant or a controlled remote audio source. Start transcription to a new path. Add `--microphone` and `--playback` with IDs from `devices` if the meeting does not use the Windows defaults:

   ```powershell
   $Started = & $LiveTranscripts start "$env:TEMP\live-transcripts-smoke.md" | ConvertFrom-Json
   $Started
   ```

   Verify exit code `0`, `success: true`, state `running`, and the expected microphone and playback IDs.

4. In a second PowerShell window, read the file while capture remains active:

   ```powershell
   Get-Content $Started.outputPath -Wait
   ```

5. Speak a distinctive sentence into the physical microphone. Ask the remote participant to speak a different sentence through the selected playback endpoint. Verify both finalized results appear while the session is active, normally within two to five seconds:

   - Local microphone text is labeled `**You:**`.
   - Remote playback text is labeled `**Meeting:**`.
   - No named-speaker attribution is expected.

6. Stop gracefully using the returned session ID:

   ```powershell
   $Stopped = & $LiveTranscripts stop $Started.sessionId | ConvertFrom-Json
   $Stopped
   ```

   Verify exit code `0`, state `stopped`, and stop reason `requested`. Confirm the live reader receives any final utterance and the process exits.

7. Query the retained final status:

   ```powershell
   & $LiveTranscripts status $Started.sessionId
   ```

   Verify the final status remains `stopped`, reports the same selected devices and output path, and contains no recognized text or Azure key.

The smoke passes only when the real microphone capture, WASAPI loopback capture, both independent Azure recognition streams, concurrent file reading, graceful stop, and retained final status all behave as described.