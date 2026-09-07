# Live Transcripts API

This local API provides a persistent process boundary for tools that clean up child processes when a command finishes. Configure the published CLI path in `appsettings.json`, then start the API independently of the calling tool from the repository root:

```bash
dotnet run --project ./src/LiveTranscripts.Api/LiveTranscripts.Api.csproj --configuration Release
```

The API listens only on `http://127.0.0.1:5080`. From Git Bash, call it with:

```bash
curl -sS http://127.0.0.1:5080/health
curl -sS http://127.0.0.1:5080/devices
curl -sS -X POST http://127.0.0.1:5080/start \
  -H 'Content-Type: application/json' \
  -d '{"transcriptPath":"C:\\Transcripts\\meeting.md"}'
curl -sS http://127.0.0.1:5080/status
curl -sS -X POST http://127.0.0.1:5080/stop
```

`transcriptPath` must be a nonempty absolute Windows path. Successful CLI results return HTTP `200`; CLI-declared failures return `400`; invocation and invalid-output failures return `500`. The API invokes `stop` once during graceful shutdown.

The OpenAPI document is available at `http://127.0.0.1:5080/openapi/v1.json` while the API is running.