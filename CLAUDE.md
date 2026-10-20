# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` holds the binding project rules (data/decision traps, paper-execution rules, source precedence among `docs/`). Read it first; this file only adds commands and architecture orientation.

## Commands

The SDK is pinned in `global.json` (.NET 10.0.401); installed .NET 6/8 SDKs cannot build this. On the dev machine it lives in the user profile, so in each PowerShell terminal first:

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

From the repo root:

```powershell
dotnet restore AutoTrader.slnx --locked-mode          # lockfiles are committed; always locked
npm --prefix ui/trading-dashboard ci
dotnet build AutoTrader.slnx --no-restore --configuration Release
dotnet test AutoTrader.slnx --no-build --no-restore --configuration Release
dotnet test tests/Trading.Tests/Trading.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~SomeName"   # single test (xUnit)
npm --prefix ui/trading-dashboard run build            # strict tsc + vite build
npm --prefix ui/trading-dashboard run test:display     # browserless replay-display module checks
npm --prefix ui/trading-dashboard run test:e2e         # Playwright; stop manual servers first
npm --prefix ui/trading-dashboard run test:e2e -- --project=desktop --grep "freezes two"   # single e2e
```

Run locally (ports 5080/5173 are fixed; e2e starts its own processes with an isolated journal, so free both ports first):

```powershell
dotnet run --project src/Trading.Api --launch-profile http   # API + Swagger at :5080/swagger (Development only)
npm --prefix ui/trading-dashboard run dev                     # UI at :5173, proxies /api and /hubs
dotnet run --project src/Trading.Replay.Cli --configuration Release -- run --dataset <dataset.json> --config src/Trading.Replay/replay.example.json --out runs/replay/<name>
```

## Architecture

One local .NET 10 API process (`Trading.Api`) composes everything; the other projects are build boundaries, not services. The reference graph is acyclic:

```
Api -> Engine, Recorder, Portfolio, Replay      Replay -> Engine, Portfolio      Replay.Cli -> Replay
Engine -> Analytics, Risk    Portfolio -> Data, Risk    Recorder -> Data    Analytics/Risk/Data -> Domain
```

- **Domain / Analytics / Risk**: shared contracts and financial calculations. Dashboard and engine must reuse these; React only presents. Code here takes data and a clock from the host, never the system clock or broker endpoints.
- **Engine**: pure setup/entry/position policy with hard gates before ranking. Configured entirely by `src/Trading.Engine/engine.paper.example.json` (typed in `EngineConfiguration.cs`). A separate session owns this module and its tests; coordinate shared Domain/Analytics/Risk/API edits (`docs/11-ENGINE-SESSION-HANDOFF.md`).
- **Replay**: deterministic historical/candle research with a supplied clock. `ReplayRunLifecycle` owns run/preset transitions and manifests; `DurableReplaySession` is the durable adapter the API host uses. The API host owns pacing, commands, and display projections only. Candle fills are explicitly hypothetical and not quote-qualified paper execution.
- **Data**: SQLite journal (`Storage:SqliteJournalPath`, created lazily — startup/health/snapshots never create a DB), Kite historical acquisition, and `HistoricalAcquisitionRevision` (frozen-input recovery, essential-first ordering). Kite credentials live only in gitignored `src/Trading.Api/kite.credentials.Local.json`, deliberately outside normal config precedence.
- **API**: two largely independent surfaces — the synthetic multi-index cockpit (catalog from `SyntheticDashboard:Indices` in `appsettings.json`, deterministic snapshots at a fixed as-of clock, append-only journal with immutable schema-1 evidence for legacy Bank Nifty plans) and the Historical Replay service (background acquisition/playback worker, versioned report files). `/hubs/dashboard` is an empty SignalR hub. The live Kite WebSocket quote feed, SQLite recorder and live paper execution (`LivePaperService`, `Trading.Recorder`, `MarketStoreSet`) are implemented and documented in `docs/22-LIVE-QUOTE-PAPER-TRADING.md`; they have not yet run in an authenticated market session.
- **UI** (`ui/trading-dashboard`): React 19/TypeScript/Vite, REST only. Replay state flows through `replayDisplay.ts` (framework-free module owning polling, ordering, stale-response rejection) bridged by `useReplayDisplay.ts`; components keep layout only. Financial values and qualification always come from the backend.

Config path settings (`Storage:*`, `HistoricalReplay:*`) resolve relative to the API content root (`src/Trading.Api`), not the working directory. Setting precedence: appsettings → environment appsettings → ignored `appsettings.Local.json` → user secrets → env vars (`__`) → CLI args.

Generated/private output (`data/`, `runs/`, local overrides, credentials) is gitignored; `docs/resources/` is dated research, not current scope.

## Agent skills

### Issue tracker

Issues live in GitHub Issues for `korejeevanj/AutoTrader` (via the `gh` CLI). See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one root `GLOSSARY.md` plus `docs/adr/`. See `docs/agents/domain.md`.
