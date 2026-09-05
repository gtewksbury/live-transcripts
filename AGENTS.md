## Agent skills

### Issue tracker

Issues are tracked as local markdown files under `.scratch/<feature>/`. See `docs/agents/issue-tracker.md`.

### Triage labels

The default canonical triage label vocabulary is used. See `docs/agents/triage-labels.md`.

### Domain docs

Domain documentation uses the single-context layout. See `docs/agents/domain.md`.

### Unit testing

- Run `dotnet test LiveTranscripts.slnx --configuration Release` after code changes.
- Add tests to `tests/LiveTranscripts.Tests/` and exercise behavior through the CLI command interface.
- Use controlled adapters for audio, cloud, process, and time dependencies so tests remain deterministic and require no physical hardware or external services.
- Assert observable results such as exit codes, JSON on standard output, diagnostics on standard error, files, and process-visible state rather than implementation details.