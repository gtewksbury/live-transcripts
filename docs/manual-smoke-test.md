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

   ```bash
   rm -rf ./artifacts/windows-x64
   dotnet publish ./src/LiveTranscripts/LiveTranscripts.csproj \
     --configuration Release \
     --property:PublishProfile=WindowsX64 \
     --nologo
   LIVE_TRANSCRIPTS="$(cygpath -am ./artifacts/windows-x64/live-transcripts.exe)"
   ```

2. List devices and confirm the intended headset microphone and playback endpoint have `isDefaultCommunications` set to `true`:

   ```bash
   "$LIVE_TRANSCRIPTS" devices
   ```

3. Start a meeting with another participant or a controlled remote audio source. Start transcription to a new path. Add `--microphone` and `--playback` with IDs from `devices` if the meeting does not use the Windows defaults:

   ```bash
   OUTPUT_PATH="$(cygpath -am /tmp/live-transcripts-smoke.md)"
   STARTED_JSON="$("$LIVE_TRANSCRIPTS" start "$OUTPUT_PATH")"
   START_EXIT=$?
   printf '%s\nexit=%s\n' "$STARTED_JSON" "$START_EXIT"
   SESSION_ID="<session-id-from-start>"
   ```

   Verify exit code `0`, `success: true`, state `running`, and the expected microphone and playback IDs.

4. In a second Git Bash window, read the file while capture remains active:

   ```bash
   tail -f /tmp/live-transcripts-smoke.md
   ```

5. Speak a distinctive sentence into the physical microphone. Ask the remote participant to speak a different sentence through the selected playback endpoint. Verify both finalized results appear while the session is active, normally within two to five seconds:

   - Local microphone text is labeled `**You:**`.
   - Remote playback text is labeled `**Meeting:**`.
   - No named-speaker attribution is expected.

6. Stop gracefully using the returned session ID:

   ```bash
   STOPPED_JSON="$("$LIVE_TRANSCRIPTS" stop "$SESSION_ID")"
   STOP_EXIT=$?
   printf '%s\nexit=%s\n' "$STOPPED_JSON" "$STOP_EXIT"
   ```

   Verify exit code `0`, state `stopped`, and stop reason `requested`. Confirm the live reader receives any final utterance and the process exits.

7. Query the retained final status:

   ```bash
   "$LIVE_TRANSCRIPTS" status "$SESSION_ID"
   ```

   Verify the final status remains `stopped`, reports the same selected devices and output path, and contains no recognized text or Azure key.

The smoke passes only when the real microphone capture, WASAPI loopback capture, both independent Azure recognition streams, concurrent file reading, graceful stop, and retained final status all behave as described.