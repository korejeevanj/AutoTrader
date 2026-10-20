# Trading.Engine

Ownership: pure setup/entry policy, hard gates before ranking, deterministic
portfolio allocation and independent position policy. References Analytics/Risk;
Domain is available transitively. Hosts provide time, coherent evidence, valid
definitions, shared financial assessments and portfolio state.

## Configure here

**`engine.paper.example.json` is the complete editable paper profile.**
`EngineConfiguration.cs` defines typed defaults and validation for the same fields.
All rupee thresholds are experimental paper settings, not live-money approvals or
guaranteed fill outcomes. The example supplies no market facts or fee schedule.

| Group | Settings / units |
| --- | --- |
| Strategies | Explicit index ID, enable/policy/confirmation, local session times, setup lifetime, lots, candidate cap, expiry policy, absolute confirmation weight % |
| Evidence | Observation/receipt age and skew as TimeSpan strings; spread percent of ask; minimum best-level depth in whole entry-lot multiples on both sides |
| Risk | Gross cash trigger, optional separate net cash trigger (`NetCashStop`, default null), modeled entry net risk and scenario hurdle in rupees; preceding true-range lookback, structural buffer multiplier, fee-profile reference |
| Position | Consecutive thesis closes, bounded soft confirmation (`SoftThesisPolicyVersion`, `MaximumSoftWeakeningDuration`), elapsed-time exit, maximum favorable progress in initial underlying R, informational net milestone and versioned net-profit exit policy |
| Ranking | Ordered criteria; depth descending, other metrics ascending; stable contract ID final tie-break |
| Portfolio | Active/pending capacity and attempts, rupee session pause, explicit cross-index priority |
| PaperSimulation | Capital, delay/timeout, adverse price slippage and fill-model version; consumed by the separate simulator |

Host-side loading, **outside evaluation**:

```csharp
var settings = EngineConfiguration.FromJson(File.ReadAllText(profilePath));
var engine = new TradingDecisionEngine(settings);
```

The [19 October correctness audit](../../docs/17-DECISION-ENGINE-AUDIT.md) maps the
review requirements to actual source, configuration conflicts, regression evidence
and measured 35-candidate/three-index internal benchmarks.

Historical Replay's API/CLI hosts load this profile explicitly before evaluation.
`EvaluateAllCandidates` scans a full eligible catalogue in bounded assessment batches
and globally ranks survivors; `MaximumCandidates` is the per-batch bound.
`FromJson` rejects unknown/misspelled keys, numeric enum values and invalid limits.
Missing keys use named defaults. `ToJson()` exports a complete profile. Collections
are frozen when the engine is constructed. The content fingerprint is included in
strategy state and candidate decisions; changed settings cannot reuse old state
even if someone forgets to change `Version`. Change the version/run deliberately
before activation. Existing positions carry their frozen strategy independently.
Consumed setup/manual-request identities are separate from configuration/run
fingerprints: changing settings or restarting a run cannot regenerate an accepted
crossing or retry the same manual request within the session.
The first policy supports one-minute bars; unsupported intervals are rejected.
Profit exits are configured under `Position.ProfitExit`: `Disabled` preserves the
baseline; `FixedNetTarget` requests exit at the configured known net target (₹500
default); `TrailingNetProfit` arms at ₹500, trails the highest known net liquidation
by ₹200 with a ₹300 initial floor, and can only tighten. All values are configurable.
See [paper profit exits](../../docs/16-PAPER-PROFIT-EXITS.md) for the approved contract.

Defaults include zero declared adverse slippage and a five-second pending-entry
timeout. These are explicit simulator assumptions to configure/stress, not observed
broker behavior. Initial capital is not inferred from a portfolio snapshot.

## Public evaluations

- `EvaluateIndex(input, state)` returns auditable candidate assessments and next
  per-index/session/configuration state, plus as-of `DirectionalEvidence`. It never emits ENTER itself.
- `Allocate(identity, time, session, portfolio, indices, state, reservationAssessor)`
  returns ENTER/WATCH/SKIP/INSUFFICIENT_DATA and the expected portfolio version.
  Selection is a recommendation; only the coordinator may accept an intent and
  reserve cash/capacity atomically. Repeated recommendations do not consume attempts.
- `EvaluatePosition(input, state)` returns HOLD/WATCH_EXIT/TIGHTEN_STOP/EXIT and explicit state.
  Fresh sell-side bid cash stops and timers work without new completed bars.
  Arming/raising net-profit protection now returns **TIGHTEN_STOP** when no exit or
  monitoring warning takes precedence. This is a protection update, not a fill.

The default profile configures Bank Nifty only. Index IDs are the existing Domain
catalog IDs. An index definition/capability does not enable a strategy. Additional
indices require explicit approved profiles; there is no symbol-name strategy switch.

`MinimumConfirmingConstituents` is a separately versioned optional entry breadth rule
(`BreadthPolicyVersion = constituent-count-v1`); null disables it. A positive count
supplements absolute weighted confirmation, requires complete same-period evidence,
and excludes neutral members. Directional evidence includes price distances, past
returns, member rows/counts, absolute missing weight and top-three confirming weight.
It supplies no probabilities or option expectancy. See [rule-based research through
Replay](../../docs/20-RULE-BASED-DIRECTIONAL-RESEARCH.md) for paired comparison and labels.

The crossing must be from a previous inside/on-range close. Return inside rearms;
continued outside closes do not create new setups. Setups expire at the earlier of
the next available completed bar, scheduled next boundary, and exclusive entry end.
Full opening/session coverage, completed availability and session-scoped VWAP
lineage are mandatory. Bank confirmation matches completed periods, discloses
missing weight and does not renormalize. Equality confirms neither direction.

Manual tests bypass setup-only history/confirmation requirements. They still pass
contract, quote, depth, cash, cost, risk, recovery, recording and shared portfolio
gates. A stable manual request cannot be accepted twice. Manual attempts have their
own allowance; automatic pause does not itself prohibit a manual diagnostic.
Both origins respect the configured half-open entry window; a manual setup bypass
does not authorize an out-of-window entry.

Known hard failures take SKIP precedence over unknown required evidence, preserving
both sets of reasons. Missing setup-dependent financial calculations are
NOT_EVALUATED when no setup exists. Scores/probabilities are not produced.

## Shared-calculator and host handoff

`EngineContracts.cs` currently owns these internal evaluation records so this tab
does not modify the shared dashboard/journal wire contract. Coordinate their eventual
canonical Domain placement and adapters with the owning tab; do not send them as
client-authorized evidence or replace illustrative dashboard records with them.

| Boundary | Required host/shared-module evidence |
| --- | --- |
| Futures features | Shared Analytics session candle VWAP at decision precision, exact available input-bar prefix and calculator version |
| Structural risk | Shared Analytics/Risk preceding-14-bar mean true range, opposite signal-candle extreme plus/minus configured buffer, positive underlying R |
| Constituents | Verified effective/available membership and weights; per-member same-period completed close and approximate session VWAP |
| Entry Risk | Credible structural-to-option cash risk, effective fees, adverse tick-aligned ask assumptions, whole-lot cash, explicit favorable scenario, spread %/friction/strike-distance metrics |
| Portfolio | Coherent version, cash already net of reservations, net liquidation session P&L with fees once, accepted intent identities, positions/pending entries, recovered/paused/recording state |
| Multiple reservations | `IPortfolioReservationAssessor` adapter to shared Risk for aggregate required cash; without it additional allocations are insufficient data |
| Position marks/progress | Actual simulated entry/fill/fees, current projected exit fees/net liquidation, and progress from entry underlying reference using frozen initial R |

Risk assessments bind contract definition, option quote, underlying observation,
structural feature, configuration, signal, units and effective fee/fill assumptions.
Missing/unknown assessments block entry. Engine reuses shared whole-lot conversion,
opening-range/participation calculations, quote age and bid-side gross P&L. It does
not duplicate fee, VWAP precision, option valuation or monetary allocation arithmetic.
The credible option-risk/opportunity estimators and adapters are **not implemented
by this module**; no profitable or executable live strategy is implied.

The [decision follow-up](../../docs/18-ENGINE-DECISION-FOLLOWUP.md) documents quote-cost
reconciliation, monitoring coverage, bounded warnings and reproducible research tooling.
Entry assumptions must equal the shared adverse ask/tick calculation. Supplied spread
and moneyness must match observed prices; friction cannot understate crossing/slippage
plus supplied fees. Cash cannot understate purchase premium; initial modeled risk cannot
understate immediate liquidation loss. Conservative additional cash/friction remains
permitted. Structural evidence must place its stop exactly -1 underlying R from the
signal close. These checks do not replace a credible option-risk estimator.
Aggregate reservation assessments must also cover the combined purchase premiums
of every proposed long option. Correlated indices do not create a hedge/margin
credit that reduces these cash debits.

Hosts supply UTC and the exchange-local midnight/calendar, canonical available
snapshots and a persisted total event order. Do not sort live events by a timestamp
that becomes known later. Evaluate every relevant protective quote and timer before
entry allocation; incorporate same-boundary available features coherently. Preserve
corrections as later evidence without rewriting recorded decisions. UI/coalescing
must not suppress recording or protective monitoring.

Position thesis checks run chronologically for every supplied new bar, latching an
exit even if a later bar recovers. The time rule uses maximum observed favorable
progress, not current progress. Missing progress reference is unknown. As agreed in
the engine interview, evidence loss yields an unpriced WATCH_EXIT warning (extending
the sprint's soft-thesis wording); observed hard exits stay latched. Profit milestone
does not force exit. The separately selected profit-exit policy can request an exit;
net marks include shared Risk's entry/projected-exit costs, stale marks never ratchet,
and mandatory exits retain priority. Peak/trail state belongs to the frozen position.
No decision claims a fill, stop execution or guaranteed loss cap.
Hard thesis failure combines the wrong-side-close count and adverse constituents
only for the same completed period. A missing current feature cannot combine old
close counts with newer banks to manufacture an invalidation.

Required monitoring bars/features/constituents are revalidated even at entry and on
repeated timers. A latest completed minute older than one bar interval plus configured
observation-age allowance is unavailable, as is a prefix that omits already processed
bars. Fresh instrument quotes cannot turn that condition into a safe HOLD.
Soft VWAP/constituent weakening starts a **new experimental two-minute maximum**
(`soft-confirmation-v1`), measured from evidence availability, no earlier than entry. This is configurable,
not a previously agreed strategy threshold. Further ticks never extend it. A complete
supportive bar before expiry clears it; missing confirmation cannot clear it. An
expiry at/before a later supportive bar remains an EXIT. `WatchResolutions` records
the resolving event and any active soft deadline. Data-loss warnings retain the existing
warning-only degraded policy; observed hard exits and soft deadlines remain outstanding.
Recovery requires a close **strictly** with futures VWAP and at least the configured
weight confirming the position direction. Neutral/mixed observations retain the
warning and its original deadline. A known VWAP or constituent warning starts its
deadline independently even when the other required monitoring input is missing.

`PaperPositionEvidence.ExitAdverseSlippage` freezes a nonnegative option-price amount
(default zero for compatibility). A quote host must copy its accepted entry profile's
exit assumption into that position. Shared Risk marks long liquidation at bid less this
amount, rounded down to contract tick; supplied net must reconcile that adjusted gross
less entry/exit fees. Candle research already supplies model-adjusted marks separately.
Observed bid depth must cover the whole position to qualify gross/net liquidation
marks. Missing/shallow depth returns unpriced WATCH_EXIT and cannot book a profit
or ratchet a trail. A fresh observed bid loss can still trigger a protective EXIT;
`GROSS_CASH_STOP_OBSERVED` records that observation without claiming a full-position
price or fill.
`NetCashStop` acts only on reconciled net marks and is not inferred from `MaximumNetRisk`.
Reason order is gross cash, net cash, structural, portfolio pause, session close, hard
thesis, no-progress time, soft timeout, fixed profit, profit trail. Every observed
simultaneous exit reason is retained. Neither EXIT nor TIGHTEN_STOP changes exposure.

Supplied net marks must exactly reconcile gross less actual entry and projected
exit fees; supplied progress must reconcile the frozen reference/R with the fresh
underlying price using shared Risk. Inconsistent results remain unavailable and
cannot arm a trail, trigger a profit exit or suppress a no-progress time exit.
Strategy/portfolio state records evaluation time and rejects backward reuse.
Position state also carries a canonical frozen fill/contract/policy fingerprint
(equal decimal scales/UTC offsets are equivalent). Hosts seeding entry-time state
use `PositionPolicyState.ForPosition(position)` before adding known initial marks.
Nonempty legacy state without that binding requires reconstruction from frozen
evidence; it is never silently rebound. Durable Replay reconstructs through its
existing ordered-input recovery, without changing its checkpoint wire schema.
The fingerprint encoding is now `frozen-position-v2` because frozen slippage and soft
policy settings are part of its meaning. Reconstruct older policy state while paused;
do not erase a live/paper exit latch or trail to bypass a fingerprint mismatch.

The separate paper coordinator/simulator owns idempotent intents, delayed later-quote
fills, fees, persistence/restart-paused recovery and pending exits during feed loss.
There are no provider, database, system-clock, filesystem or order calls during
Engine evaluations. Real broker execution remains deferred.

## Verification

Historical replay now uses `AssessCandleResearch` and `EvaluateCandlePosition` as
explicitly labelled research seams. They reuse this policy without fabricating
observed quotes/depth or converting unknown strict assessments to ENTER. Shared
historical features, structural and declared cost/accounting calculators are implemented;
credible production option valuation/observed-liquidity adapters remain pending.
See [`Trading.Replay/README.md`](../Trading.Replay/README.md) for configuration and runs.
The candle-policy seam independently expires completed price marks one interval
after source end, and opening marks one interval after their opening time; newly
wrapping an old price does not refresh it. Stale underlying excursions cannot seed
progress. Existing hard exit latches and timers still run when marks expire. Replay
also evaluates newly filled positions immediately, so initial spread/fees cannot
breach a configured net stop unnoticed until a later recovering candle.

Behavior tests live under `tests/Trading.Tests/Engine/`. From the repository root,
with the pinned SDK on PATH:

```powershell
dotnet test tests/Trading.Tests/Trading.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~Trading.Tests.Engine"
dotnet build AutoTrader.slnx --no-restore --configuration Release
dotnet test AutoTrader.slnx --no-build --no-restore --configuration Release
```

Fixtures prove software behavior only. Host integration must separately prove
recording/replay ordering, fee/cash reconciliation, delayed fills, duplicate intent
prevention, stop overruns, disconnect/restart recovery and zero real-order requests.
