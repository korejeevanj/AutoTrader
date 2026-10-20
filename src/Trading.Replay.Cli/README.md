# Trading.Replay.Cli

Local executable host for the reusable `Trading.Replay` library. It loads manifest,
replay settings and Engine profile before evaluation, handles interactive/wall-clock
pacing, and writes immutable run reports. No provider authentication or order APIs.

See [`Trading.Replay/README.md`](../Trading.Replay/README.md) for file format,
configuration, commands, research assumptions and verification.

```powershell
dotnet run --project src/Trading.Replay.Cli --configuration Release -- --help
```

`run` covers all requested dates/presets; `step` opens the first date/preset for
inspection. Command errors return exit code 1. Completed diagnostic runs may contain
zero trades or incomplete sessions; the summary lists these instead of claiming success
as a strategy test. Reports require a new or empty `--out` directory.
