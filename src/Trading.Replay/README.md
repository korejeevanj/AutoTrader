# Trading.Replay

## Received-quote sessions — experimental

`QuoteTradingSession` supplies one deterministic event/clock seam for live hosting
and recorded-quote replay. It combines quote-derived completed bars, shared Analytics
and the explicit experimental option-scenario calculator with the existing Engine
and `QuotePaperBroker`. It accepts no broker/client-asserted risk approvals.
Frozen source instruments, settings, fees, weights and code identity are preserved;
changed-policy replay retains the source universe and writes separate results.
Recovery reconstructs a committed market tail without inventing unacknowledged
historical fills; the durable paper checkpoint remains the execution authority.
Restart begins paused and marks possible uncommitted recording-tail loss.

`POST /api/live/replays` is the quote-replay host; it does not use candle openings or
infer subsecond prices/depth. The current CLI below remains the candle-research CLI.
See [live policy and runbook](../../docs/22-LIVE-QUOTE-PAPER-TRADING.md).

## Historical JSON replay — implemented

The reusable library imports local Kite minute-candle datasets through Trading.Data,
supplies deterministic simulated time and shared Analytics/Risk calculations, records
strict Engine diagnostics, and optionally runs a **hypothetical candle research** model.
The command-line host is `src/Trading.Replay.Cli`. The API now also hosts acquisition
jobs and a Historical Replay page; see [daily setup](../../docs/12-DAILY-KITE-SETUP.md).
The [historical readiness contract](../../docs/15-HISTORICAL-DATA-READINESS.md)
defines qualification independently of playback/download completion, including
unknown option evidence and dated reference-data provenance.

### Durable replay evidence

`ReplayRunLifecycle` owns run allocation/recovery, preset scope/progression, frozen
manifest publication and report eligibility. It exposes the same session interface
while retaining the existing durable adapter internally. A failed run-manifest write
leaves the session paused with visible pending-manifest diagnostics; Step/Play retry
that manifest before any evaluation or control write. The same run identity is kept.
Existing manifests and completed reports remain immutable. Manual and automatic
progression share preset completion; an active comparison installs its next preset
paused after a manual final step. Results cannot publish before the requested scope
is complete. The host supplies frozen inputs/code provenance and retains command
mapping, pacing, serialization, bounded display projections and report-file publication.
Run identity and frozen opening bytes are staged atomically in SQLite before attaching
the durable session. The stage survives manifest failure and the interval before the
host checkpoint; recovery uses compatible latest-run/preset openings without adopting
orphaned variants from different frozen inputs. A successful host checkpoint acknowledges
the opening. This preserves the original manifest timestamp across process restart.
Acknowledgement is cleanup after both durable pieces exist: failure retains the safe
stage for a later checkpoint and does not stop playback or the acquisition worker.
Prepared datasets remain accessible when their first manifest is pending, with playback
still gated. Preparation retry recovers a compatible current run, including already
published reports, instead of implicitly starting another experiment.
Explicit refetch retains a persisted new-run intent, even when bytes/failures are
unchanged. Recovery can adopt that intent's compatible staged new run, but never the
preceding run or an unnecessary additional replacement.

`DurableReplaySession` is the host's durable adapter at the existing
`IHistoricalReplaySession` seam. It owns run/batch/event identities, checkpoint encoding,
entry-control history and deterministic checkpoint reconstruction. `Step` retains a
failed transaction's complete serialized envelope and retries it before advancing;
decision history is released only after commitment. `HasPendingCommit` also remains
true when an uncommitted step has reached the end of the session. Entry controls are
rejected while a step is pending, and a failed control commit restores the previous
setting and pauses playback.

`ReplayEvidenceRun` names a job/run/preset and its supplied frozen-input fingerprint.
Existing journal identities and checkpoint/evaluation JSON remain compatible.
The SQLite journal still owns transactions, conflict detection and snapshot-consistent
exports. The host retains pacing, command handling, recent-display projections and
report publication; report readers stream committed decisions through
`DurableReplaySession.ReadDecisions`. Library/CLI callers can continue to use ordinary
in-memory sessions without opting into durable storage.

The API additionally offers `PriceOnlyReplaySession` when historical strategy
metadata is incomplete. It shares causal time/step/pause controls, but evaluates no
strategy or fills, emits no cash-index VWAP and reports financial results unavailable.
The selected day's official NSE contract archive supplies historical definitions;
current broker rows may only map an exact historical identity to a provider token.

### Files to configure

- `src/Trading.Replay/dataset.example.json`: copy into your raw dataset directory as
  `dataset.json`, beside the downloaded JSON files. It contains the six tokens supplied
  by the user. Extend `Instruments` and `Weights[].Members` to **all 14 verified banks**.
  The algorithm uses the declared member list, not a fixed count.
- `src/Trading.Replay/replay.example.json`: publication lag, date range, base/adverse
   spread/slippage ticks, gap-stress rupees, execution delay/timeout, fee schedules
   and the default 45-calendar-day weight snapshot limit.
- `src/Trading.Engine/engine.paper.example.json`: opening range, confirmation, entry/exit
  windows, gross/net model budgets, quantity/ranking and shared portfolio limits.

`EngineProfile` resolves relative to the replay configuration file. Candle `File`
paths resolve relative to the dataset manifest. Unknown JSON settings are rejected.
Each instrument requires a unique token and source file: a raw response cannot prove
identity and must never be relabelled as another bank/option.

The dataset example is a **metadata template**, not verified contract history. Replace
UNVERIFIED versions, placeholder validity/publication dates, null lot/tick/expiry,
weights and source references with facts applicable to the historical date before
setting `Verified=true`. Do not copy current membership/lot sizes into past periods.
Declare actual trading dates/calendar and the universe rule before inspecting outcomes.
The example now supplies a labelled sourced paper-fee reference from April 2026;
earlier/uncovered dates still have unavailable fees. Instrument metadata, weights and schedules cannot affect decisions before
their declared `AvailableAtUtc`/effective interval.

### First real-data run

Expected example layout (add the other twelve bank files):

```text
data/raw/replay/2026-10-09/
  dataset.json
  banknifty-index.json
  banknifty-oct-futures.json
  hdfc-bank.json
  icici-bank.json
  banknifty-oct-55000-ce.json
  banknifty-oct-55000-pe.json
```

From the repository root, with the pinned user-local SDK on PATH:

```powershell
dotnet restore AutoTrader.slnx --locked-mode
dotnet run --project src/Trading.Replay.Cli --configuration Release -- run --dataset data/raw/replay/2026-10-09/dataset.json --config src/Trading.Replay/replay.example.json --out runs/replay/oct09-run01
```

Use a new output directory for every run. Existing output is not overwritten.
For interactive stepping use `step` in place of `run`: Enter advances one event-time
batch, `r` resumes, `p` pauses automatic stepping, `q` stops and saves an incomplete
report. Optional `--speed 60` paces playback at 60×; it never changes simulated time or
decisions. Without speed, `run` executes as fast as possible.

### Candle import and availability

Accepts the standard `{"status":"success","data":{"candles":[...]}}` response or a
bare candle array. Rows have six fields (timestamp, OHLC, volume) or seven (plus OI).
`+0530` is normalized to UTC; offsets are mandatory. Missing OI remains null, zero
volume remains zero, invalid rows are reported, identical duplicates collapse and
conflicting duplicate minutes are quarantined. Gaps are not forward-filled.

Timestamps label period start. A 09:15 candle becomes complete at 09:16 plus the
configured publication lag (default zero, an assumption). All same-time completions
are admitted before policy evaluation. Opening events expose **only** instrument,
time and open; later high/low/close/volume never leak into opening decisions.
Cash-index volume never provides VWAP. Shared Analytics produces unrounded approximate
futures/bank candle VWAP with complete-prefix lineage and preceding-bar true ranges.
Missing session history/required constituent weight prevents setup qualification.

### Two distinct result lanes

1. `STRICT_ENGINE`: normal Engine evaluation with actual candle features and **null**
   observed quotes/depth/option-risk estimates. It cannot produce execution-qualified
   ENTER from candle-only files.
2. `CANDLE_RESEARCH`: explicit Engine research adapter reuses setup, confirmation,
   contract/cash/portfolio gates and position policy. It records hypothetical intents
   and fills, preserving unverified liquidity, structural-to-option mapping and
   predicted opportunity. Production gates are not weakened.

Research does not fabricate QuoteEvidence, best-level depth or live last-trade age.
Observed-depth ranking is unavailable; other configured ranking criteria and stable
IDs decide among model-feasible options. The narrow initial call/put universe is an
integration experiment, not evidence that those strikes are optimal.

### Declared execution and accounting model

- Base: full spread 20 ticks, extra slippage 2 ticks per side. Adverse: 40/10 ticks.
- Buy at next eligible option open plus half-spread/slippage, rounded upward; sell
  minus them, rounded downward. No separate spread charge is deducted afterward.
- Default research delay is zero: a signal completed at 09:31 may fill from the
  09:31 **next candle's opening event**, processed after the close decision. This is
  an idealized ordering assumption, not a claim about broker latency. Nonzero lag or
  delay waits for a genuinely later eligible opening; never backdate a fill.
- Pending entries reserve cash/capacity, count as attempts even if canceled, expire
  after the configured two-minute default or entry-window end, and revalidate cash,
  fees, contract/futures validity and structural invalidation at fill. Repeated scans
  do not reuse a consumed setup.
- Modelled net risk uses the same adverse rounded stop fill (including immediate
  spread loss), entry/exit fees and ₹50 configurable gap stress. Reject infeasible
  whole lots; this is not a guaranteed net-loss bound.
- With zero delay, a completed option low may impute a cash stop at the declared stop
  model, or an adverse open if already breached. The exit reports its **possible
  interval** and `ExitKnownAtUtc` when the full candle became available. No precise
  intraminute timestamp is claimed. Cash stops take adverse priority.
- Intrabar fees follow the half-open execution interval, not the later booking time.
  If more than one effective regime covers that interval, use the largest applicable
  exit-fee estimate and record conservative attribution. Uncovered intervals cannot
  invent fees/fills; they leave an exit request pending.
- With positive execution delay, cash stops request pending exits and wait for a
  later eligible opening; intrabar fills are not imputed. Missing price/fees keeps
  exits pending. Gaps and stop overruns remain visible.
- Structural invalidation uses completed futures closes; thesis failure uses actual
  consecutive closes and matching bank evidence. Favorable progress uses available
  highs/lows since entry, with frozen signal R and entry-time futures opening reference.
  Timers/session close run even when a file has gaps. Profit milestone does not force exit.
- Shared Risk charges entry/exit fees exactly once and reconciles cash, realized net,
  indicative open liquidation and a latched aggregate session-loss pause. Unknown marks
  remain unknown; no hidden forced-close at dataset end.

Each day/preset starts independently at the configured paper capital; results are not
compounded. Open/pending positions and dataset gaps mark sessions incomplete and exclude
them from aggregate performance. Market session/calendar is host-supplied. The first
ORB policy requires its opening start to match market open, and session exit to precede
market close. Library/CLI sessions are in-memory; the local host uses durable replay
evidence for paused checkpoint recovery. Arbitrary seek remains deferred.

### Fee profile

See [`13-REPLAY-PAPER-TRADER.md`](../../docs/13-REPLAY-PAPER-TRADER.md) for the current
reference rates, statutory rounding and qualifications. They are estimated paper costs,
not exact broker reconciliation. The example includes the 2026-04-01 STT revision.

Populate `FeeSchedules` in replay configuration with effective, sourced paper profiles:

```json
{
  "ProfileId": "paper-fees", "Version": "your-effective-schedule-version",
  "Source": "verified schedule reference", "IsIllustrative": false,
  "AvailableAtUtc": "2026-10-01T00:00:00Z",
  "EffectiveFromUtc": "2026-10-01T00:00:00Z", "EffectiveToUtc": null,
  "BrokeragePerOrder": 0,
  "ExchangeTurnoverRate": 0, "RegulatoryTurnoverRate": 0,
  "SellTransactionTaxRate": 0, "BuyStampDutyRate": 0, "GstRate": 0
}
```

**The zeroes above are placeholders, not a fee recommendation.** Rates are fractions
(0.18 means 18%); use actual dated assumptions. The declared model rounds each charge
to paise away from zero and applies GST to brokerage/exchange/regulatory components.
It estimates paper costs, not exact broker tax/order reconciliation. Overlapping
effective schedules are rejected. Illustrative schedules qualify synthetic fixtures
only; fees applied to a real dataset must be explicitly sourced/non-illustrative.
Every rate/charge and its availability/effective-start date must be explicitly present
in a fee JSON object. Omitted charges do not silently become zero.

### Reports and verification

Outputs: `run-manifest.json`, `quality.json`, `normalized-candles.json`, `results.json`,
`features.csv`, `decisions.csv`, `trades.csv`, `summary.md`, `directional-evidence.csv`
and `directional-outcomes.json` / `.csv`. They retain data hashes,
effective configurations/fingerprints, assembly code versions, fee breakdowns, complete
gate traces, assumptions, drawdown, stop overruns and unresolved state. Wall-clock pacing
does not enter decisions. Run-to-end and step/pause have identical decision results.

The [rule-based research guide](../../docs/20-RULE-BASED-DIRECTIONAL-RESEARCH.md)
defines the as-of directional snapshot, optional constituent-count rule, evaluator-only
fixed-horizon/first-touch labels and `compare-rules` CLI command. The current policy
and a count-rule variant run on identical frozen sessions/presets; only complete
pairs contribute to the net-difference summary. Directional labels never enter Engine
inputs and do not establish option expectancy. Target settings live under
`ReplayConfiguration.DirectionalResearch`, independent of entry/exit settings.

```powershell
dotnet test tests/Trading.Tests/Trading.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~Trading.Tests.Replay"
dotnet build AutoTrader.slnx --no-restore --configuration Release
dotnet test AutoTrader.slnx --no-build --no-restore --configuration Release
```

Tests use isolated, labelled fixtures, including 14 constituents. Real replay still
requires the actual JSON files and verified date-specific metadata. No provider calls,
credentials, real order requests or synthetic plan-journal writes are part of this runner.

Ownership: reusable playback, clock/event scheduling, research lifecycle and run
provenance. Direct references remain Engine and Portfolio; Data/Analytics/Risk/Domain
are available transitively. CLI references Replay; tests reference Replay and build
the CLI for an isolated process-level integration test. API references Replay for
historical jobs and playback; CLI output still uses explicit `--out`, independently
of API `Storage:RunsRoot` and the historical job storage root.
