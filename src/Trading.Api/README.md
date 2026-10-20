# Trading.Api

## Live quote paper host

`LivePaperService` hosts full-mode Kite streaming, a bounded SQLite recorder, the
shared quote-session coordinator and durable paper checkpoints independently of UI.
`GET /api/live/status` is read-only; `POST /api/live/commands` accepts `start`, `pause`,
`arm`, `manual` and `exit`. Manual requests carry stable `requestId` plus a
server-resolved frozen-universe `contractId`; clients never submit quotes/risk.
`GET /api/live/recordings` lists sources and their frozen engine JSON;
`GET /api/live/recordings/{id}/decisions?after=0&limit=50` reads committed decision
batches. `POST /api/live/replays` accepts
`sourceRunId` and optional full `engineJson`, preserves the source universe, and
persists a separately identified result. Replay requires no provider connection.
Development-only `POST /api/live/rehearsal` exercises a separate fictional packet
session through the actual pipeline, returns SYNTHETIC TEST, and never starts live data.

The host emits bounded `livePaperChanged` hub notifications; the current live UI
uses deadline-bounded REST polling. Recording and protection do not await UI sends.
Startup is connection-free unless `LivePaper:ConnectOnStartup` is explicitly true
or a current live run is recovered. Recovered entries remain paused. Prior-session
unresolved positions block new-session rollover while their current verified held-option
feed continues; empty sessions roll to fresh definitions,
with an 08:30 refresh for runs prepared earlier. See the
[agreement/runbook](../../docs/22-LIVE-QUOTE-PAPER-TRADING.md) for policy and limitations.

`LivePaper:RootDirectory` defaults to `../../data/live` relative to API content root.
`market-YYYY-MM-DD.sqlite` (one per IST trading date; older closed days become `.sqlite.br`) stores raw events and manifests; `paper.sqlite` stores acknowledged
state and compressed decisions; `quote-replays.sqlite` isolates comparison results.
Writers use WAL/FULL transactions. A dedicated paper writer never holds the protection
lock during storage; commands await their commit outside that lock. Credentials still
come only from the existing private reloading Kite credential file.

## Historical Replay workspace

The API now hosts a date-driven historical acquisition/replay service in addition to
the synthetic cockpit. Open **Replay** in the UI and select a completed Bank Nifty
session. Credentials come only from the gitignored, reloading backend
`kite.credentials.Local.json` file. The UI links directly to the [daily setup guide](../../docs/12-DAILY-KITE-SETUP.md).

Routes: `GET /api/replay/status`, `GET /api/replay/setup`, `POST /api/replay/jobs`,
`GET /api/replay/jobs/{id}`, `POST /api/replay/jobs/{id}/commands`, and versioned
`GET /api/replay/jobs/{id}/reports/{run}/{file}`. Preparation returns 202 and a job ID;
polling is read-only. Commands control data/research jobs, not broker orders.

The provider boundary implements only fixed-origin Kite GET historical/master
requests, central monotonic throttling, bounded transient retries and code-only
errors. Auth token renewal does not require rebuilding. Successful raw revisions,
dated metadata, configuration snapshots and per-job download hashes are pinned;
restart/retry does not silently rebind a prepared job to newer shared data/settings.
An explicit Refresh creates a new job/revision. Partial inputs remain visible.
Index/futures/bank acquisition precedes the full option catalogue. `refreshGaps`
explicitly refetches sparse/empty/invalid/failed series into a new attempt;
`retryFailed` targets request failures. Data health includes shared historical
qualification, separate from `CanReplay`/`PreparationComplete`. See the
[readiness and recovery guide](../../docs/15-HISTORICAL-DATA-READINESS.md).
The `HistoricalAcquisitionRevision` module in Trading.Data owns frozen-input recovery,
pin reuse/refetch, essential-first ordering and attempt publication. This host supplies
the existing download adapter, schedules work and publishes progress; it does not
coordinate acquisition files. Shared readiness and replay policy remain separate.
Engine-backed playback uses Trading.Replay's `DurableReplaySession` at the existing
session seam. It owns pending batches, checkpoint/control encoding, commit/retry/history
release and checkpoint reconstruction. The host keeps pacing, user commands and
bounded display projections; it never releases an uncommitted decision batch.
`ReplayRunLifecycle` owns the surrounding run/preset transitions and manifest readiness,
including restart, comparison recovery, policy variants and report eligibility. The
host maps commands to that owner; it cannot advance a manifest-less run after a failed
Restart or preset transition. Failed manifest publication is visible and retried before
playback continues. Attempt health/refetch comes from `AcquisitionAttemptEvidence`,
independently of whichever dataset is still being played; this host formats its labels
and adds the separately computed historical qualification.
`setProfitExit` selects a validated fixed/trailing/baseline profit policy as a new
paused variant on the same dataset, persists it across recovery and protects open
positions' frozen settings. See [paper profit exits](../../docs/16-PAPER-PROFIT-EXITS.md).

API now references Replay directly; the CLI remains an independent host. Historical
UI contracts are separate from synthetic dashboard/journal records. Full date-valid
CE/PE catalogues are assessed in bounded batches with global ranking. Charts and bank
observations expose only the current simulated time; final reports publish behind
a completion marker. Daily master archival starts after first Replay use and
continues at 08:30 IST while the process runs. Historical snapshots, weights and fees
must be available; today's dump never substitutes for unavailable history.

Ownership: the single local .NET 10 composition host and public REST/SignalR boundary.
Feed, recorder, analytics, engine and local paper simulation share this process.

## Current public surface

- `GET /api/health` returns HTTP 200 with exactly these JSON properties:

  ```json
  {"status":"ok","application":"AutoTrader","mode":"SYNTHETIC TEST","paperAutoEnabled":false}
  ```

  This reports application connectivity only. It does not indicate live feed health,
  quote freshness, recording completeness or trading readiness. Synthetic mode and
  paused paper-auto are fixed values, not configurable trading controls.
- `/hubs/dashboard` has no client-callable hub methods. The live host publishes
  bounded `livePaperChanged` notifications independently of its protection timers.

- `GET /api/dashboard/scenarios` returns exactly `normal`, `missing-inputs`,
  `stale-quotes`, `feed-loss`, `open-position` with labels/descriptions.
- `GET /api/dashboard/indices` returns the configured `IndexDefinition[]`: `id`,
  `displayName`, `exchange`, `timeZone`, `constituentLabel`, `definitionVersion`,
  `effectiveFromUtc`, nullable `effectiveToUtc`, `isSynthetic`, and `capabilities`
  (`futuresChart`, `optionComparison`, `weightedConstituents`). Identities are extensible
  case-sensitive strings, initially BANKNIFTY/NIFTY/SENSEX; venue metadata is illustrative.
- `GET /api/dashboard/snapshot?indexId=NIFTY&scenarioId=normal` returns the shared
  `DashboardSnapshot`; omitted index defaults to BANKNIFTY and scenario to normal.
  Unknown index or scenario is 404 Problem Details.
- `POST /api/journal/plans/preview` accepts `PlanSelection` and returns `PlanPreview`:
  derived whole-lot units, fresh-only reference ask, quote state and planning availability.
- `POST /api/journal/plans` accepts `SavePlanRequest`, returns 201 `JournalEntry`
  and a Location of `/api/journal/plans/{id}` only after the atomic save succeeds.
- Preview/save require `indexId` along with scenario/snapshot/contract/whole lots;
  valid individual IDs from different workspaces are rejected with 400.
- `GET /api/journal/plans` returns all indices' `JournalEntry[]`, newest first;
  `?indexId=NIFTY` filters independently. Unknown filters return 400 Problem Details.
- `GET /api/journal/plans/{id}` returns the original stored entry or 404.

All record fields follow the exact shared contract in
[`10-MULTI-INDEX-AND-OPENAPI-INCREMENT.md`](../../docs/10-MULTI-INDEX-AND-OPENAPI-INCREMENT.md),
extending [`09`](../../docs/09-SYNTHETIC-DASHBOARD-INCREMENT.md):
camelCase, JSON numeric decimals, ISO-8601 UTC timestamps, and explicit null fields.
Required request fields must be present. Positive whole lots must be JSON integer
numbers; fractions, numeric strings and 32-bit derived-unit overflow are rejected.
Validation/body failures return 400 Problem Details with an explanation; unknown
scenarios/read IDs return 404 Problem Details. Storage failures return an error,
never a saved response. Extra client properties are ignored, including supplied
`snapshot`/`units`; market evidence and quantities are always server-resolved.

### Development OpenAPI and Swagger

Open **http://localhost:5080/swagger** for Swagger UI and
**http://localhost:5080/openapi/v1.json** for the .NET 10 generated document.
Both are mapped only in Development; Production returns 404 for either URL.
Typed REST results and the named `HealthResponse` preserve existing health JSON.
Every REST operation has an ID/tag/summary/description, concrete success schemas,
Problem Details schemas and request validation descriptions. POST operations include
400/415/500 errors; unknown snapshot/plan reads include 404. Required selection and
plain-text fields, plan-status/scenario values and checked whole-lot semantics are
documented. Response schemas also enumerate quote/constituent states, option types,
illustrative actions, snapshot freshness, preset position states, journal statuses,
scenario IDs, synthetic source/mode, and the fixed health values (including false
paperAutoEnabled). Catalog IDs and contract underlyings remain extensible strings.
Synthetic endpoints have no execution operations; live-paper controls are documented above.

Exact packages: `Microsoft.AspNetCore.OpenApi [10.0.12]` and
`Swashbuckle.AspNetCore.SwaggerUI [10.3.0]`; no deprecated `WithOpenApi` usage.

### Deterministic fixture semantics

Current schema version is `2`; fixture version is
`synthetic-dashboard-v2:{indexId}:{definitionVersion}`. Snapshot IDs append
`:{scenarioId}`, all current snapshots have `indexId`/`constituents`, and sequence is 1.
`banks` is a deprecated constituent alias on Bank Nifty and empty on other indices.
As-of is fixed at
`2026-10-12T04:45:05+00:00` (10:15:05 Asia/Kolkata). Ages are relative to that as-of,
not the host wall clock. With the default enabled capabilities, every scenario has the same 24 option contracts (12 strikes,
CE/PE pairs), stable within an index across all scenarios, with distinct index-scoped
contract/position identities. All contracts explicitly use exchange `SYNTHETIC`.

| Fixture | Cash value | Futures base | First strike / step | Lot | Tick | Expiry |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| BANKNIFTY | 52140 | 52000 | 51500 / 100 | 25 | 0.05 | 2026-10-29 |
| NIFTY | 25140 | 25000 | 24500 / 50 | 75 | 0.10 | 2026-10-27 |
| SENSEX | 85140 | 85000 | 84000 / 100 | 20 | 0.25 | 2026-10-30 |

These are configured illustrative fixtures, not current exchange facts or strategies.
Lot/tick/validity are properties of each generated contract, not portfolio-wide rules.

With the default enabled capabilities, there are 60 completed one-minute futures bars from 09:15 to 10:15 Asia/Kolkata,
09:15–09:30 opening-range levels, and approximate candle-derived futures VWAP.
Candle/VWAP chart coordinates both label period start; bar end and availability
remain distinct, and only completed, available bars contribute.
Cash-index context has no VWAP. Four fixture constituents use illustrative weights 40/30/20/10;
missing-inputs discloses 30% missing weight without renormalization. Stale/disconnected
history remains visible with explicit state and observation timestamps. Candidate
decisions/reasons are illustrative, with no real selection policy or probability.

Open-position uses a preset one-lot synthetic long with indicative bid-side gross P&L;
stale/feed-loss use the same preset as unpriced, with null current bid/gross P&L.
Position net P&L and all planning fee/cash/net-risk estimates remain null.
Only journal `savedAtUtc` uses injected `TimeProvider.System` in the host.

### Optional feature capabilities

`SyntheticDashboard:Indices[*]:Definition:Capabilities` controls three independent
boolean feature flags. BANKNIFTY, NIFTY and SENSEX ship with all three set to true.
Setting a flag false changes every scenario's actual supplied evidence:

| Flag set false | Snapshot behavior |
| --- | --- |
| `FuturesChart` | `chart.bars`/`chart.vwap` are empty; opening-range high/low and `market.futuresValue` are null. `chart.vwapLabel` and `freshness.message` explain chart unavailability. Symbols remain metadata and cash-index context stays available. |
| `OptionComparison` | `options` is empty. `position.state` is `none`, all position evidence is null, and its message explains unavailability. Preview/save reject contract selection with 400; no plan is persisted. |
| `WeightedConstituents` | Constituent observations remain illustrative and unweighted: every `weightPercent` is null, aggregate above/below weights are null, missing weight is 100, and `coverageLabel` explains weighted participation unavailability. |

Clients should disable the corresponding widgets using catalog flags and accept empty
options/chart arrays and unavailable aggregates. Flags do not enable strategies or
execution. Complete fixture configuration is still required; disabling a feature
suppresses its evidence rather than fabricating substitutes.

### Preserved saved evidence

Existing schema-1 `synthetic-dashboard-v1` Bank Nifty journal records remain readable.
Portfolio projects only their entry `indexId` to BANKNIFTY; snapshots retain schema 1,
original version/as-of/contracts/quotes/banks, with null new snapshot fields. No JSON
UPDATE, migration rewrite, reset or deletion occurs. Unknown versions/underlyings are
not inferred as Bank Nifty; inconsistent saved evidence returns an error.

The synthetic endpoints still have no live feed/recorder or broker execution.
Historical Replay has its own background acquisition/playback worker and explicitly
hypothetical candle fills; it does not enable real orders or live paper-auto.

## Local start and backend checks

From the repository root in PowerShell (use the installed user-local SDK):

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& "$env:DOTNET_ROOT\dotnet.exe" restore tests/Trading.Tests/Trading.Tests.csproj --locked-mode
& "$env:DOTNET_ROOT\dotnet.exe" build src/Trading.Api/Trading.Api.csproj --no-restore --configuration Release
& "$env:DOTNET_ROOT\dotnet.exe" test tests/Trading.Tests/Trading.Tests.csproj --no-restore --configuration Release
& "$env:DOTNET_ROOT\dotnet.exe" run --project src/Trading.Api --configuration Release --no-build --launch-profile http
```

The `http` launch profile binds `http://localhost:5080`, sets Development and does
not launch a browser. Local frontend development should proxy `/api` and `/hubs`
to this origin. There is no cross-origin CORS policy configured.

## Configuration and storage

Application setting precedence, lowest to highest:

1. `appsettings.json`
2. Optional `appsettings.{Environment}.json`
3. Optional, ignored `appsettings.Local.json` in the API content root
4. User secrets in Development (`UserSecretsId`: `AutoTrader-Trading.Api-local`)
5. Environment variables (use `__` for nested keys)
6. Command-line arguments

ASP.NET Core supplies the base configuration; startup adds the local file and
reapplies secrets/environment/arguments so the local file cannot override them.
Keep provider secrets/tokens server-side. Kite credentials are deliberately excluded
from this merged configuration precedence: only `Kite.ApiKey` / `Kite.AccessToken`
inside `kite.credentials.Local.json` are bound to the provider. Missing file/keys do
not fall back to environment variables or user-secrets. `HistoricalReplay` options
still follow normal application precedence. See the daily setup guide above.

| Setting | Default | Consumer |
| --- | --- | --- |
| `SyntheticDashboard:Indices` | Three fixture definitions and seeds | Validated `SyntheticIndexCatalog` / shared generator |
| `Storage:SqliteJournalPath` | `../../data/autotrader.db` | Trading.Data immutable synthetic journal |
| `Storage:MarketEventRoot` | `../../data/raw` | Reserved legacy setting; live received events use SQLite |
| `LivePaper:RootDirectory` | `../../data/live` | Live market and paper SQLite databases/reference cache |
| `Storage:RunsRoot` | `../../runs` | Run/replay provenance and artifacts |
| `HistoricalReplay:RootDirectory` | `../../data/replay` | Dated catalogues, calendars, source revisions and jobs |
| `HistoricalReplay:ReplayConfigurationPath` | `../Trading.Replay/replay.example.json` | Frozen replay/Engine profiles |

These paths are relative to **API content root**, normally `src/Trading.Api`, not
the current working directory. The host resolves the journal path against
`IHostEnvironment.ContentRootPath`; absolute overrides are also suitable.
Journal schema/files are initialized lazily on journal save/list/read. Startup,
health, dashboard snapshots and preview do not create a database. MarketEventRoot
and RunsRoot remain future settings without storage consumers.

Catalog definitions, futures symbols, contract prefixes, price/strike/lot/tick/expiry
seeds and constituent rows live in API `appsettings.json`. `IIndexCatalog` exposes
validated definitions/identity lookup, hiding options from consumers. Adding an
identity means supplying another complete configuration entry; no symbol switch or
frontend closed enum is required. HTTP tests add MIDCAP through
`PostConfigure<SyntheticDashboardOptions>` and obtain its catalog/snapshot/preview.

Options are resolved from DI when the journal is first used, after configuration
composition. Test hosts can use `ConfigureServices` with
`PostConfigure<JournalStorageOptions>` to reliably override SqliteJournalPath;
the recovery tests create a fresh host against the same temporary file.

An optional local override can contain, for example:

```json
{
  "Storage": {
    "SqliteJournalPath": "C:/AutoTraderLocal/autotrader.db",
    "MarketEventRoot": "C:/AutoTraderLocal/raw",
    "RunsRoot": "C:/AutoTraderLocal/runs"
  }
}
```

The environment equivalent is `Storage__SqliteJournalPath`. Credentials belong in
the gitignored `kite.credentials.Local.json`; no credential values are shipped.

## Direct project-reference graph

Arrows mean "references"; this graph is acyclic. Transitive references avoid
duplicating lower-level references in each project. Replay is reused by API and CLI.

```text
Trading.Tests     -> Trading.Api, Trading.Replay, Trading.Replay.Cli (build only)
Trading.Api       -> Trading.Engine, Trading.Recorder, Trading.Portfolio, Trading.Replay
Trading.Replay.Cli -> Trading.Replay
Trading.Replay    -> Trading.Engine, Trading.Portfolio
Trading.Engine    -> Trading.Analytics, Trading.Risk
Trading.Recorder  -> Trading.Data
Trading.Portfolio -> Trading.Data, Trading.Risk
Trading.Analytics -> Trading.Domain
Trading.Risk      -> Trading.Domain
Trading.Data      -> Trading.Domain
Trading.Domain    -> (none)
```

All projects inherit the root .NET 10 build policy. Exact direct package versions
and generated `packages.lock.json` files are included; use locked restore for
verification. SignalR comes from the ASP.NET Core shared framework, not an
additional server package.
