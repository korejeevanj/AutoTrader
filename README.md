# AutoTrader

A local .NET and React workspace for the multi-index paper-trading application described in [`docs/`](docs/00-START-HERE.md).

**Current state:** interactive **SYNTHETIC TEST** workspaces for **Bank Nifty, Nifty 50 and Sensex**, plus historical candle research and an experimental **Live paper** workspace. The live path includes Kite full-mode streaming, SQLite received-event recording, delayed quote-qualified local paper execution, recovery, existing-engine integration and recorded-quote replay. Authenticated live-session readiness and actual-machine recording throughput still require measurement. API health verifies the application, not market-feed freshness. The [live-paper agreement and runbook](docs/22-LIVE-QUOTE-PAPER-TRADING.md) describes setup, assumptions and limitations; the [decision-engine audit](docs/17-DECISION-ENGINE-AUDIT.md) covers the earlier engine increment.

## Live paper trading

Open **Live paper** (`http://localhost:5173/#live`). Supply the day's server-side
Kite token using the [daily setup guide](docs/12-DAILY-KITE-SETUP.md), then select
**Start / reconnect recording** before 09:15 IST. The backend records independently
of the page. Validate the manual paper lifecycle before explicitly enabling auto.
Missing opening history, reference evidence, costs or risk qualification remain
visible entry blockers. The initial ₹400 modeled-risk ceiling can reject every
candidate; it is never widened to obtain a demonstration trade.

Raw messages and immutable run manifests live in `data/live/market-YYYY-MM-DD.sqlite` (one file per IST trading date, older closed days compressed to `.sqlite.br`); paper
checkpoints, fills and compressed decision history live in `data/live/paper.sqlite`.
Separate quote-replay results live in `data/live/quote-replays.sqlite`. These are
distinct from candle research and synthetic plans. The Development-only **Run
synthetic quote rehearsal** button exercises the real backend pipeline with fictional
packets and a supplied clock, without connecting to a provider.
SQLite market recording supersedes the sprint's earlier raw-event file choice for
this increment. Read the runbook before the first real session.

## Dashboard workflow

**Historical Replay:** the UI's **Replay** workspace now prepares a selected day from
Kite through the backend, reuses immutable cached data and exposes playback/research
reports. Start with the [daily token/setup guide](docs/12-DAILY-KITE-SETUP.md), also
linked directly in the page. Complete historical contract catalogues/weights and
effective fees remain required for qualified research; missing evidence is explicit.
The [replay guide](src/Trading.Replay/README.md) describes the CLI and candle model.
The [paper-trader guide](docs/13-REPLAY-PAPER-TRADER.md) describes automatic decisions,
the persisted paper account/ledger, monthly weight convention and reference costs.
The [Replay lab guide](docs/14-REPLAY-RELIABILITY-AND-DIAGNOSTICS.md) covers paced versus
maximum-speed playback, data-health errors, complete run logs and same-dataset comparison.
The [historical readiness guide](docs/15-HISTORICAL-DATA-READINESS.md) defines
essential-first acquisition, coverage-gap recovery, reference-data provenance
and qualification separately from successful playback.
The [paper profit-exit guide](docs/16-PAPER-PROFIT-EXITS.md) covers configurable
fixed net targets, ratcheting trailing locks and same-dataset policy comparisons.

1. Choose a cockpit index and fixture scenario: normal, missing inputs, stale quotes, feed loss or open position.
2. Inspect the completed one-minute futures chart, approximate VWAP, opening range and synthetic constituent participation.
3. Select an option to open its plan drawer; pin two contracts and freeze their timestamped comparison.
4. Enter whole lots, considered/planned/skipped status, thesis, stop/invalidation text and a note. The backend derives units; fees/cash/net-risk estimates are unavailable.
5. Save, then open **Journal** to retrieve the original snapshot evidence across all indices or filter by index. Saved entries survive refresh and backend restart; unsaved drafts remain only in the browser tab.

Switching indices clears selection, pins and frozen comparison, while drafts remain keyed by index and contract. Switching is disabled during a pending save. Existing schema-1 Bank Nifty plans remain readable without rewriting their stored evidence.

Fixture timestamps use a fixed synthetic clock displayed in Asia/Kolkata. Fixture contracts, weights and prices are illustrative definitions, not current market facts. Preset paper positions are demonstrations; entry, exit and paper-auto controls are disabled.

## Prerequisites

- .NET SDK **10.0.401**, pinned in `global.json` (patch updates within that SDK feature band are allowed).
- Node **22.23.2** and npm **10.9.8**; frontend dependencies are pinned with an npm lockfile.
- Microsoft Edge for the default Playwright browser checks, or Playwright Chromium as described in the [dashboard guide](ui/trading-dashboard/README.md#browser-verification).

On this development machine, the SDK is installed at `$env:USERPROFILE\.dotnet`. In each PowerShell terminal that launches .NET or browser tests:

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version
```

If the pinned SDK is already on your `PATH`, use it directly. The installed .NET 6/8 SDKs cannot build this solution.

## Restore and start

From the repository root:

```powershell
dotnet restore AutoTrader.slnx --locked-mode
npm --prefix ui/trading-dashboard ci
```

**Terminal 1 — backend**, from the repository root:

```powershell
dotnet run --project src/Trading.Api --launch-profile http
```

**Terminal 2 — dashboard**, from the repository root:

```powershell
npm --prefix ui/trading-dashboard run dev
```

Open **http://localhost:5173**. The API binds **http://localhost:5080**. Vite proxies `/api` and `/hubs` to the API, including WebSocket upgrades. Both ports are fixed; stop an existing process before starting another instance.

**Swagger:** open **http://localhost:5080/swagger**. The OpenAPI document is at **http://localhost:5080/openapi/v1.json**. Both are available in Development, which the `http` launch profile selects. A no-profile launch must explicitly set `ASPNETCORE_ENVIRONMENT=Development` to expose them; Production returns 404. Swagger documents the actual REST schemas, identifiers, success/error responses and synthetic journal semantics.

The API exposes health, dashboard snapshots, journal, historical and live-paper routes; see the [backend route guide](src/Trading.Api/README.md). The live host broadcasts bounded `livePaperChanged` notifications on `/hubs/dashboard`; its UI polls REST status with a deadline. Synthetic snapshots still load on scenario selection/refresh. Production hosting must provide same-origin routing separately from Vite's development proxy.

## Verify

From the repository root after restore:

```powershell
dotnet build AutoTrader.slnx --no-restore --configuration Release
dotnet test AutoTrader.slnx --no-build --no-restore --configuration Release
npm --prefix ui/trading-dashboard run build
```

The frontend build includes strict TypeScript checking. For browser checks, **stop manual API/Vite servers first** and run:

```powershell
npm --prefix ui/trading-dashboard run test:e2e
```

Playwright starts fresh Development backend/frontend processes with an isolated test journal, then shuts them down. Backend tests cover OpenAPI exposure/schemas, configurable indices, cross-index validation, synthetic snapshots, completed-bar availability, immutable/legacy journal evidence and restart recovery. Browser checks cover index switching, draft restoration, journal filtering, charts, comparison freeze, saved-plan retrieval, responsive layout, and failure/recovery. The sprint's paper lifecycle E2E checks will accompany those features.

## Repository map

```text
AutoTrader.slnx
src/
  Trading.Domain/       Canonical market and decision contracts
  Trading.Analytics/    Shared market features
  Trading.Risk/         Shared financial and risk calculations
  Trading.Engine/       Selection and position policy
  Trading.Data/         Provider adapters and persistence
  Trading.Replay/       Deterministic historical/candle research and durable paper replay
  Trading.Replay.Cli/   Independent historical replay/report executable
  Trading.Recorder/     Feed/cache/recording boundary
  Trading.Portfolio/    Paper state and journal boundary
  Trading.Api/          Single executable API host
  Trading.Context/     Documented future module
  Trading.Alerts/      Documented future module
  Trading.Research/    Documented future module
  Trading.Shadow/      Documented future module
ui/trading-dashboard/  React/TypeScript application and browser checks
tests/Trading.Tests/   Backend integration tests
config/                Versioned non-secret configuration examples
data/
  raw/                 Private append-only market observations
  normalized/          Private validated datasets
  quality-reports/     Generated data-quality reports
runs/                  Generated ledgers, manifests, and reports
docs/                  Scope, architecture, and dated research
```

Each module README describes its ownership and current state. The [backend guide](src/Trading.Api/README.md#direct-project-reference-graph) documents the acyclic project-reference graph. Libraries are build boundaries within one application; the newer Engine/Replay guides and source supersede older placeholder summaries. Candle research remains distinct from executable quote-qualified paper trading.

## Configuration

Default storage settings live in `src/Trading.Api/appsettings.json`. `Trading.Data` resolves the journal database path relative to API content root; its default is `data/autotrader.db`. The first journal read/save creates the directory and `journal_plans` schema lazily. Startup, health and snapshot requests do not create a database. Journal records are append-only plans with preserved snapshot provenance, separate from any future paper ledger.

`SyntheticDashboard:Indices` in the same settings file defines the index catalog and illustrative fixture seeds. Adding a complete index definition does not require a new UI enum or a copied cockpit. These definitions do not supply live entitlements, verified contract masters, constituent weights or strategy rules. Keep index identity, strategy configuration and portfolio-wide risk/cash controls separate.

See [`config/README.md`](config/README.md) for the local override example and the [backend configuration guide](src/Trading.Api/README.md#configuration-and-storage) for precedence. Kite credentials belong only in the gitignored `src/Trading.Api/kite.credentials.Local.json`; edit it for automatic reload. Frontend `VITE_*` variables are public build inputs.

Generated/private data, local overrides, credentials, build output, and test artifacts are ignored. Folder README files and dependency lockfiles are tracked.

## Governing documents

- [`08-MONDAY-LIVE-PAPER-SPRINT.md`](docs/08-MONDAY-LIVE-PAPER-SPRINT.md) governs the 12 October milestone: one local host, shared .NET calculations, and local paper execution.
- [`02-ARCHITECTURE-1.md`](docs/02-ARCHITECTURE-1.md) and [`05-SYSTEM-COMPONENTS-AND-PROJECTS.md`](docs/05-SYSTEM-COMPONENTS-AND-PROJECTS.md) describe ownership and future modules.
- [`06-DASHBOARD-SPECIFICATION.md`](docs/06-DASHBOARD-SPECIFICATION.md) describes the planned dashboard.
- [`09-SYNTHETIC-DASHBOARD-INCREMENT.md`](docs/09-SYNTHETIC-DASHBOARD-INCREMENT.md) records the agreed synthetic cockpit, API contracts, persistence and acceptance workflow.
- [`10-MULTI-INDEX-AND-OPENAPI-INCREMENT.md`](docs/10-MULTI-INDEX-AND-OPENAPI-INCREMENT.md) extends those contracts for multiple indices, Swagger, switching and legacy evidence compatibility.
- [`11-ENGINE-SESSION-HANDOFF.md`](docs/11-ENGINE-SESSION-HANDOFF.md) supplies the engine-session prompt and shared-file ownership agreement.
- [`03-DATA-AND-REPLAY.md`](docs/03-DATA-AND-REPLAY.md) defines historical/replay rules.
- [`19-PREDICTION-RESEARCH-BRIEF.md`](docs/19-PREDICTION-RESEARCH-BRIEF.md) records the corrected prediction experiment plan, current engine seams and offline dataset-first delivery boundaries.
- [`20-RULE-BASED-DIRECTIONAL-RESEARCH.md`](docs/20-RULE-BASED-DIRECTIONAL-RESEARCH.md) describes the implemented rules-first directional evidence, offline outcomes and paired rule comparison through Replay.
- [`21-OCTOBER-14-REPLAY-LOSS-AUDIT.md`](docs/21-OCTOBER-14-REPLAY-LOSS-AUDIT.md) reconciles the saved −₹691.88 run, traces its exits/costs and records controlled diagnostic comparisons.
- [`22-LIVE-QUOTE-PAPER-TRADING.md`](docs/22-LIVE-QUOTE-PAPER-TRADING.md) records the agreed SQLite quote recording, live paper policy, deterministic quote replay and session runbook.

Planning documents describe the intended system. This README and the source tree describe the implemented increment.
