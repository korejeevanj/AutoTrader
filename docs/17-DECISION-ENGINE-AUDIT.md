# Decision-engine correctness increment — 19 October 2026

## Audit baseline and scope

Inspected the actual source before changing behavior. The checkout contains extensive
uncommitted/untracked application work; it is the baseline for this increment, not
a replacement/scaffolding opportunity. No account credentials or private datasets
are needed for this audit. The root README's older implementation summary is behind
the Engine/Replay source and the newer guides (13–16).

Baseline commands (user-local .NET 10.0.401 on PATH):

- `dotnet build AutoTrader.slnx --no-restore --configuration Release`: passed, zero warnings/errors.
- `dotnet test AutoTrader.slnx --no-build --no-restore --configuration Release`: 255 passed.
- `npm --prefix ui/trading-dashboard run build`: TypeScript and Vite passed.

This increment hardens the existing public Engine evaluation seams, with shared
Risk arithmetic where needed. These are the policy seams already specified in
the engine handoff and R18: `EvaluateIndex`, `Allocate`, `EvaluatePosition`, and
the separately labelled candle-research evaluations. Replay/API/browser regression
checks exercise their existing integration. They do not establish live-feed readiness.

## Requirement-to-code gap table (before this increment)

Paths below are relative to `src/` unless labelled otherwise.

| Requirements | Status and implementation evidence | Change / verification |
| --- | --- | --- |
| R01, R09 | Existing: `Trading.Engine/TradingDecisionEngine.cs:EvaluateIndex` gates before `Rank`; `PortfolioAllocation.cs:Allocate` alone recommends ENTER; Replay records separate intents/fills. Incorrect: allocation does not compare evidence-version identity; manual setup bypass also bypasses the configured entry window. | Harden same-snapshot allocation and enforce entry window for manual diagnostics; public evaluation regressions. |
| R02 | Partial: synthetic catalog covers BANKNIFTY/NIFTY/SENSEX; Engine is index-agnostic and returns STRATEGY_NOT_CONFIGURED. Only Bank Nifty default strategy and historical acquisition path are configured. | Preserve explicit unavailability; use independent synthetic per-index inputs for load measurement only. |
| R03 | Partial: canonical effective-dated Engine contracts, importer quality checks, availability/receipt/observation times. Incorrect: impossible observation-after-receipt chronology can pass freshness. | Fail closed on impossible timing; quote and underlying entry/position regressions. Live normalization/streaming remains missing. |
| R04–R05 | Existing baseline: `Trading.Analytics/HistoricalSessionAnalytics.cs`; `EvidenceChecks.Bars/Feature/Confirmation`; absolute weights, full coverage, approximate futures/stock candle VWAP. Broader context/features partial. | Preserve baseline and future isolation. No new indicator assumptions. |
| R06–R07 | Partial: quote/depth evidence exists in strict Engine; historical candles cannot supply it. IV/Greeks, scoped PCR/MaxPain, trade-flow/event enrichment missing. | Keep unavailable; do not fabricate enrichment. |
| R08 | Partial: `EvaluateIndex` bounds candidates; `AllCandidateEvaluation.cs:EvaluateAllCandidates` deliberately scans full historical catalogues in batches and globally ranks. This is a documented existing research choice, different from a bounded live universe. | Preserve historical comparison; live subscription/universe host remains missing. |
| R10–R11 | Existing: dated shared fees, lot/cash gates, separate gross/net budgets and provisional session pause. Incorrect: position policy trusts unreconciled supplied net marks and supplied progress values. | Reconcile in shared Risk before using marks/progress; ensure malformed evidence cannot ratchet a profit trail or suppress a time exit. |
| R11, R14 | Partial: deterministic allocation recommendations; `Trading.Replay/ResearchLifecycle.cs` serial reservations, three accepted-intent attempts, prefill checks; `DurableReplaySession` + `Trading.Data/ReplayEvidenceJournal.cs` durable batch recovery. Live atomic cross-index coordinator missing. | Preserve replay durability; do not present allocation recommendations as atomic accepted reservations. |
| R12 | Existing: independent quote cash stops, latched mandatory exits, timer/session checks, fixed/trailing net exits with frozen strategy. Incorrect: prior strategy state lacks evaluation-time guard; position state is bound only to position ID. Optional underlying three-bar trail absent. | Add chronological/configuration-bound state protection; regression-test hard-stop priority and unchanged baseline decisions. |
| R13 | Missing active multi-leg/short/margin/partial-fill capability in inspected Engine/Replay. | Leave unsupported; no implied protection or qualified structure. |
| R14 | Partial: delayed later-opening candle research fills, fees, persistent ledger, restart-paused controls. `KiteHistoricalClient` exposes only fixed-origin GET history/master calls. Quote-qualified local broker and live feed missing. | Retain explicit candle-research labels; verify existing zero-order HTTP boundary. |
| R15 | Partial: incremental features, bounded display queues, durable event batches independent of browser polling. Streaming recorder, relevant-quote risk cadence and live queue-pressure instrumentation missing. | Measure pure Engine 35-candidate and three-index evaluation separately from live latency. |
| R16 | Existing: `ReplayRunner`, `CandleReplaySession`, durable recovery, manifests/hashes, dated metadata, stressed presets and reports. Historical readiness checks distinguish playback from qualification. Untouched profitable-policy evidence missing. | Run deterministic/future-isolation/recovery regressions; no account coverage/profitability inference. |
| R17 | Existing: candidate IDs, versions/fingerprints, gates, financial evidence, stable ranks; Replay reason/gate journal and React replay/position views. Incorrect: `AssessCandleResearch` constructs a dictionary with duplicate candidate IDs even though strict evaluation rejected their uniqueness. | Return auditable rejected duplicate alternatives instead of an exception. Keep existing API/UI wire contracts. |
| R18 | Existing 255 backend tests plus browser suite. No live feed-to-quote-qualified-fill E2E. | Add regression slices for audited failures, run builds/backend/browser checks and record actual results below. |

## Configuration provenance and unresolved choices

The current `Trading.Engine/engine.paper.example.json` is preserved: ₹500,000 paper
capital; one lot; one active/pending position **across the portfolio** (stricter than
one per index); three strategy attempts/session, one manual diagnostic allowance;
₹200 gross cash stop; ₹400 modeled net entry-risk ceiling; provisional ₹1,200 net
session pause; 250 ms declared delay, 5 s entry timeout, zero declared adverse
slippage. These are experimental paper values, not approved live risk limits.
Accepted intents count even when canceled/unfilled; recommendations and gate
rejections do not consume attempts. A new crossing after rearming is a new setup;
the same accepted setup/manual request cannot be retried by changing run/config.

Material difference from the review: the existing strict entry profile requires a
credible favorable scenario of at least ₹500 net (`MinimumScenarioNetProfit`). This
is an existing **entry feasibility hurdle**, separate from the informational ₹500
milestone and optional fixed/trailing exit. It remains versioned for comparison;
this increment does not silently reinterpret it as a target forecast or compulsory
realized outcome. The ₹400 net entry-risk ceiling is not currently a separately
configured net stop trigger; the ₹200 gross stop remains the protective trigger.
The later SENSEX spread example does not replace either setting.

Nifty/Sensex entry policies, verified constituents, cross-index exposure caps,
general live risk limits and credible production option-risk/scenario estimators
remain unresolved. Live streaming/history entitlement and expired-option/September
coverage require authenticated evidence; documentation and fixture passes do not
establish them. SQLite and existing immutable replay files are retained.

## Implemented changes and before/after evidence

All failures below were reproduced through the public policy interfaces before
their fixes. Tests live in `tests/Trading.Tests/Engine/EvidenceIntegrityTests.cs`.

| Requirements | Reproduced before | Verified after |
| --- | --- | --- |
| R10, R12 | Flat gross ₹0, entry/exit fees ₹20 each, supplied net ₹500 caused a false fixed-profit EXIT; supplied ₹800 could arm a trail. | Unreconciled net is null, WATCH_EXIT/NET_LIQUIDATION_UNKNOWN; no peak/trail ratchet. The fresh gross cash stop still runs independently of fees. Fractional fees reconcile exactly; one-paisa discrepancies are rejected. |
| R10, R12 | Flat underlying supplied as 10R progress was retained and could suppress the 20-minute no-progress exit. | Shared Risk reconciles progress to frozen entry/R and the exact fresh price; inconsistent progress is unavailable and cannot suppress the timer. Repeating decimal progress remains valid. |
| R03, R09 | Observation/event/last-trade time after its own receipt passed freshness. | Entry is unranked and fails OPTION_QUOTE_FRESH or has unknown FUTURES_PRICE_FRESHNESS. Existing stale/heartbeat rules remain effective. |
| R12, R16 | Strategy/portfolio state from a later evaluation could affect an earlier decision. | Timestamped state rejects backward evaluation; equal-time deterministic reevaluation remains supported. |
| R12, R16 | Position state could be reused with a changed fill or frozen risk policy under the same ID. | A versioned canonical position fingerprint binds fill, contract, strategy and entry references; genuine changes reject reuse. Equal decimal values with different scale and equivalent timestamp offsets retain the same fingerprint and protective exits. |
| R01, R09, R16 | Allocation accepted decisions from a different EvidenceVersion at the same timestamp. | Allocation requires complete EvaluationIdentity equality, alongside existing time/configuration/portfolio bindings. |
| R17 | Conflicting duplicate candidates threw during candle-research lookup construction. | Both alternatives remain inspectable, rejected and unranked with UNIQUE_CANDIDATES. |
| R09, R14 | Manual diagnostics could skip the configured entry window. | MANUAL_TEST bypasses setup only; ENTRY_WINDOW applies to both origins, including the exclusive entry end. |

Shared monetary/progress kernels are in `Trading.Risk/IndicativePlanning.cs` and
are reused by `HistoricalResearchCalculations.Net/Progress`; no financial formula
was added to React or duplicated into Engine. Existing fee rates and frozen profiles
are unchanged.

### State and recovery compatibility

`PositionPolicyState.ForPosition(frozenPosition)` is the host seam for binding any
entry-time seeded peak/state. `Trading.Replay/ResearchLifecycle.cs` now uses it.
Ordinary callers may still start with null/default state and let evaluation bind it.
Nonempty **unbound legacy policy state is rejected** with an explicit reconstruction
message; do not reset just a live position's trail/exit flags to work around this.
Reconstruct from the frozen entry and ordered market/decision evidence while paused.
The literal legacy-state regression verifies this rule.

Existing durable replay checkpoints do not serialize Engine policy state:
`DurableReplaySession.Restore` reconstructs it by replaying frozen inputs. The paper
checkpoint JSON and saved synthetic plan schemas are unchanged. Existing restart,
latched-profit-trail and duplicate-fill recovery tests passed. As before, a changed
code/input hash requires a new replay run rather than reinterpreting old evidence.

### Final verification

The existing local API (PID 7100 at verification) held Release DLL copies open.
The subsequent Release test build was blocked by MSB3027/MSB3021 file-copy errors;
that process was left running. Final backend verification used Debug instead:

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet build AutoTrader.slnx --no-restore --configuration Debug
dotnet test AutoTrader.slnx --no-restore --configuration Debug
npm --prefix ui/trading-dashboard run build
$env:AUTOTRADER_E2E_API_PORT = '5380'
$env:AUTOTRADER_E2E_UI_PORT = '5373'
npm --prefix ui/trading-dashboard run test:e2e
```

- Backend Debug build: passed, zero warnings/errors. Full suite: **270 passed**
  (255 baseline + 15 new regression cases), zero skipped/failed.
- Final `dotnet restore AutoTrader.slnx --locked-mode` and `git diff --check`: passed.
- Frontend strict TypeScript + Vite build: passed.
- Full desktop/mobile Playwright suite: **133 passed, 1 skipped** (the desktop
  instance of a mobile-only layout test), 2.4 minutes. Isolated journal and ports.
- `PaperTradingTests.Engine_paper_lifecycle_is_visible_durable_and_recovers_paused_without_duplicate_fills`
  exercised actual HTTP acquisition-fixture → Engine/candle intent → fill → monitor
  → restart-paused recovery → exit → SQLite journal. It still reconciles one trade,
  net **−₹34**, cash **₹499,966**, and exactly one entry/exit fill.
- `HistoricalApiTests.Date_only_job_downloads_validates_and_replays_with_no_future_candles_or_real_orders`
  and the Kite request-boundary tests passed: intercepted requests are GET history
  paths, not broker order requests. No authenticated provider calls were made.
- Browser replay tests use controlled HTTP replay responses; the genuine synthetic
  cockpit/journal browser flows use the test API. These are **not** a live or
  quote-qualified feed-to-paper-fill browser E2E. That requested path remains missing.

### Reproducible internal benchmark and policy traces

The .NET 10 file-based harness has no additional package dependencies; its lockfile
is `tests/benchmarks/packages.lock.json`. Run from the repository root:

```powershell
dotnet run --file tests/benchmarks/engine-evaluation.cs --configuration Release -p:RestoreLockedMode=true
dotnet run --file tests/benchmarks/engine-evaluation.cs --configuration Release -p:RestoreLockedMode=true -- --trace
```

Measured on Windows 10.0.26200 x64, .NET 10.0.12, 20 logical processors. Each case
uses 200 warm-up and 1,000 measured samples, synthetic complete evidence and 35
eligible contracts **per index**. Each sample includes steady-state `EvaluateIndex`
and shared `Allocate`; it checks there is only one ENTER under the frozen portfolio
cap. The three independent fictional constituent sets are not real-index policies.

| Load | p50 | p95 | Maximum | Allocated bytes/sample |
| --- | ---: | ---: | ---: | ---: |
| One index × 35 | 0.4598 ms | 0.7685 ms | 1.5532 ms | 286,067 |
| Three indices × 35 | 0.7875 ms | 2.1860 ms | 3.8411 ms | 832,538 |

Both met the 50 ms internal target in this run (browser testing also ran on the
machine). This simple sequential microbenchmark excludes feature construction,
recording, feed latency, queue pressure, database, UI and position-monitoring load;
allocations are material and these numbers do not establish all-session capacity.
There is no pre-change performance measurement or claimed speed improvement.

`--trace` prints real structured policy output with IDs, gates, ranks, versions and
financial evidence for reproducible synthetic scenarios. Key excerpts:

```text
2026-10-12T04:01:02Z  SYNTHETIC_TEST  BANKNIFTY
recommendation: ENTER  BANKNIFTY:synthetic-option-05  units=25  rank=1
reason: ENTER_QUALIFIED; requiredCash=5040; modeledNetRisk=350
position (flat bid, reconciled fees): HOLD  gross=0  net=-40
position (same bid, inconsistent supplied net=500): WATCH_EXIT  gross=0  net=null
reason: NET_LIQUIDATION_UNKNOWN; peak=null; trail=null
```

These traces supply fictional frozen position/fill evidence directly to policy;
they demonstrate neither a simulator fill nor an expected trading return.

### Paper/replay operation and remaining delivery boundary

Use [the daily setup guide](12-DAILY-KITE-SETUP.md), then start the existing API/UI
as in the root README. In Replay, prepare a completed day, inspect historical
readiness, select a frozen exit policy while flat/paused, and Step/Run Full Day.
Inspect paper positions, decision gates, trades and Run evidence; restart recovery
remains paused. Candle results retain their research label and data-qualification
gaps. [The replay module guide](../src/Trading.Replay/README.md) supplies the
dataset manifest and independent `Trading.Replay.Cli run --dataset ... --config ...
--out ...` workflow. The CLI integration test exercises that actual executable and
checks the same −₹34 fixture report. No private dataset was used for this increment.

Still missing: live streaming/recorder and degradation instrumentation; production
quote/cost/risk adapters; atomic durable quote-qualified paper coordinator, delayed
fresh bid/ask/depth fills and their live E2E; Nifty/Sensex qualified inputs/policies;
multi-leg/partial-fill capability; authenticated historical-coverage evidence and
untouched chronological strategy evaluation/feature ablations. Profitability and
real-money readiness are not established by this increment.

### Review

**Standards:** no documented-standard violations found. Two heuristic findings
(representation-sensitive fingerprints, duplicate Risk arithmetic) were addressed
with canonical decimal/UTC encoding and shared calculators.

**Spec:** the same fingerprint correctness issue and a legacy-state verification
gap were addressed with equivalent-value and literal-legacy regressions plus bound
Replay initialization. Fractional-fee/repeating-ratio coverage was added. Full
backend/browser checks above ran after the production fixes.
Both independent review axes re-inspected the fixes and reported no remaining
concrete findings in this increment. Browser-owned test ports 5380/5373 were free
after shutdown; the pre-existing local API was left running.
