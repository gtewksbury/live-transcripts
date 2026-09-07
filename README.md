# Live Transcripts

Live Transcripts is a Windows 11 x64 command-line application that writes finalized microphone and meeting-playback speech to a Markdown file. Agents invoke the published executable by full path and receive exactly one JSON result on standard output; human-readable diagnostics are written to standard error.

## Before every session

Connect a headset first and set its microphone and playback endpoints as the Windows communications defaults. When Teams or Zoom uses `System default`, Live Transcripts selects those communications endpoints automatically.

Playback uses whole-endpoint WASAPI loopback capture. Notifications, music, and unrelated application audio played through the selected endpoint can therefore appear in the transcript. Microphone speech is labeled `You`; all playback speech is labeled `Meeting`. The application does not identify named remote speakers.

Recognition is fixed to `en-US`, and finalized text normally appears after approximately two to five seconds. Every session stops after at most two hours. The operator is responsible for authorization to use the configured Azure Speech resource and for obtaining any participant consent required by policy or law.

## Publish

On a Windows x64 build machine with Git Bash and the .NET 10 SDK, run from the repository root:

```bash
rm -rf ./artifacts/windows-x64
dotnet publish ./src/LiveTranscripts/LiveTranscripts.csproj --configuration Release --property:PublishProfile=WindowsX64 --nologo
```

The command creates one self-contained executable at `artifacts/windows-x64/live-transcripts.exe`. The release also contains a credential-free `appsettings.json`. The target Windows 11 x64 machine does not need a separately installed .NET runtime, an installer, a global tool, or a `PATH` change.

Each publish replaces the artifact directory. Set Azure credentials after publishing by editing `artifacts/windows-x64/appsettings.json`:

```json
{
  "AzureSpeech": {
    "Key": "<approved-resource-key>",
    "Region": "<azure-region-name>"
  }
}
```

Keep the configured file beside the executable. Do not commit, log, or pass the key as a command argument.

## Run the API

From the repository root, run:

```bash
dotnet run --project ./src/LiveTranscripts.Api/LiveTranscripts.Api.csproj --configuration Release
```

## Agent invocation

Resolve the full executable path once, then use that path for every operation:

```bash
LIVE_TRANSCRIPTS="$(cygpath -am ./artifacts/windows-x64/live-transcripts.exe)"
```

List available endpoints and their communications-default status:

```bash
"$LIVE_TRANSCRIPTS" devices
```

Start with the Windows communications defaults:

```bash
"$LIVE_TRANSCRIPTS" start "C:/transcripts/meeting.md"
```

Append a visibly separated session to an existing document:

```bash
"$LIVE_TRANSCRIPTS" start "C:/transcripts/meeting.md" --append
```

Override either or both endpoint IDs using values returned by `devices`:

```bash
"$LIVE_TRANSCRIPTS" start "C:/transcripts/meeting.md" --microphone "<microphone-id>" --playback "<playback-id>"
```

Query or stop the sole current session, or supply the `sessionId` returned by `start`:

```bash
"$LIVE_TRANSCRIPTS" status
"$LIVE_TRANSCRIPTS" status "<session-id>"
"$LIVE_TRANSCRIPTS" stop
"$LIVE_TRANSCRIPTS" stop "<session-id>"
```

A successful command exits with code `0`. Invalid arguments, unavailable devices, missing sessions, session-operation failures, and unexpected failures use nonzero exit codes. Always parse standard output as JSON and treat standard error as diagnostic text only.

See [docs/manual-smoke-test.md](docs/manual-smoke-test.md) for the Windows audio and Azure release check.