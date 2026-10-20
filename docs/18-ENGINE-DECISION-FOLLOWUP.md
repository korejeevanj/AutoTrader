# Engine decision follow-up — 19 October 2026

## Compact code-to-requirement audit (before edits)

The existing working tree, including the earlier correctness increment in document
17, is the baseline. This follow-up changes decision logic, not the application.

| Requirement | Existing implementation / gap | Planned decision fix |
| --- | --- | --- |
| Setup before selection; per-index policy | `TradingDecisionEngine` implements an armed, completed-minute Bank Nifty ORB/VWAP crossing, 65% absolute constituent confirmation, full coverage, expiry exclusion and deterministic ranking. Other indices require explicit policy. | Preserve this baseline, including its existing configurable favorable-scenario hurdle; do not copy bank inputs to other indices. |
| Required monitoring data; safe HOLD | `PositionPolicy` checks only newly processed post-entry bars. Empty bars or missing entry-period constituents can leave HOLD with fresh quotes. Previously processed features can disappear without revalidation. | Require current, complete monitoring-bar/feature/constituent evidence, including at entry and repeated timer evaluations. |
| Bounded WATCH_EXIT | Wrong-side VWAP closes with non-adverse banks can warn indefinitely. Decisions do not expose a confirmation deadline. | Versioned, configurable soft-thesis timeout and structured next-event/deadline evidence; preserve immediate two-close-plus-banks hard failure. |
| Cash stops independent of bars | Gross stop and exit latching exist. `MaximumNetRisk` is entry feasibility, not a net stop. | Add a separately optional, frozen net cash-stop trigger; leave the entry budget distinct. |
| Bid/ask costs/slippage | Fresh bid-side gross/net reconciliation exists, but position evidence has no frozen exit-slippage assumption. Entry only checks assumed entry >= ask and trusts supplied spread. | Shared Risk executable-price arithmetic, adverse tick rounding, frozen exit slippage and quote-consistent entry checks. No second spread deduction. |
| Profit protection | Fixed net booking and monotonic net-P&L trail already exist; default disabled. | Preserve and regression-test priority against new hard stops. Underlying three-bar trailing remains a separate unimplemented hypothesis. |
| Duplicate/shared capacity | Deterministic allocation and replay accepted-intent reservations exist. Allocate returns proposals, not atomic acceptance. | Preserve existing host boundary and replay duplicate/recovery tests. Do not create an execution system. |
| Research evidence | Existing Replay has chronological candle fills, fees, stop overruns, ablations/stress reports and durable recovery. No quote-qualified live evaluation dataset supplied. | Reuse replay fixtures and benchmark harness; distinguish fictional behavior evidence from out-of-sample performance. |

Baseline verification: **69 Engine tests passed** (Debug). Existing Release benchmark,
200 warm-ups / 1,000 samples: one index × 35 p50 **0.4685 ms**, p95 **0.6708 ms**;
three × 35 p50 **0.7195 ms**, p95 **1.7780 ms**. Synthetic steady-state
`EvaluateIndex` + `Allocate`, excluding features, feeds, persistence and UI.

Public test seams are the existing documented Engine interfaces: `EvaluateIndex`,
`Allocate`, `EvaluatePosition`, and explicit candle-research evaluation. Shared Risk
is touched only for price/financial consistency; no dashboard wire contracts change.

## Implemented fixes and before/after decisions

Regression cases are in `tests/Trading.Tests/Engine/DecisionFollowupTests.cs` and
`ReplayDecisionFollowupTests.cs`. The defects were reproduced before their fixes.

| Evidence / rule | Before | After |
| --- | --- | --- |
| Fresh option/futures quotes; monitoring bars empty, stale, or entry-period banks/features absent | HOLD | WATCH_EXIT with the missing required evidence named; no safe HOLD inferred from quote freshness. |
| Previously processed monitoring feature disappears, or an older prefix omits a processed adverse close | Could preserve HOLD or erase the soft warning deadline | WATCH_EXIT; the older prefix cannot reset the existing deadline. |
| One completed VWAP-adverse close, banks still favorable | WATCH_EXIT without an explicit bound | WATCH_EXIT with resolving event and frozen deadline. `soft-confirmation-v1` expires after the configured two minutes unless complete supportive evidence arrives first. |
| Supportive candle ends before the deadline but its feature/required bank evidence is published after it | New timeout implementation initially allowed retrospective clearing; caught during review | EXIT / SOFT_THESIS_TIMEOUT. Clearing uses actual evidence availability, not candle-end time. |
| Soft timeout configured to 90 seconds in replay | Would only be checked at subsequent scheduled market events | Existing replay timer schedules exact 09:33:30 EXIT; a 09:34 opening later supplies the hypothetical fill. No invented timeout-time fill. |
| Entry 200 × 25; bid 192.05, ask 193.05; frozen exit slippage 0.06; tick 0.05; fees 20 each | Gross −198.75, no gross stop; slippage-adjusted supplied net rejected | Rounded exit mark 191.95; gross −201.25, net −241.25, EXIT / GROSS_CASH_STOP. Spread/slippage charged once. |
| Gross −175, paid entry fees 20, projected exit fees 30; explicitly configured net stop 220 | No net-trigger capability | Net −225, EXIT / NET_CASH_STOP, even without candle/fee-independent gross stop. Missing fee evidence cannot manufacture this trigger. |
| Ask 200, Q=25; asserted reservation ₹1 | Could qualify/ENTER | SKIP / PREMIUM_CASH_FLOOR; observed purchase premium alone is ₹5,000. |
| Two cross-index long purchases of ₹5,000 each; shared reservation assessor asserts ₹8,500 combined cash | Could recommend both despite an impossible purchase debit | Second proposal fails PORTFOLIO_PREMIUM_CASH_FLOOR: at least ₹10,000 premium before fees. Separate long purchases cannot receive invented hedge/margin credit. |
| 199/200 quote; asserted spread/friction zero, ATM distance zero despite 100 strike vs 105 future, or off-tick assumed fill 200.03 with zero declared slippage | Could qualify/ENTER | Unranked SKIP: quote-cost reconciliation / execution assumptions fail. Conservative *additional* friction/cash remains allowed. |
| Signal close 105, stop 96.4, asserted initial R 0.01 | Could qualify | INSUFFICIENT_DATA / STRUCTURAL_RISK_UNKNOWN. The stop must be exactly −1 underlying R using shared Risk. Correct R is 8.6, not option rupees. |
| Net 500 activates trailing floor 300; later net 800 raises it to 600 | HOLD while protection changed | TIGHTEN_STOP / NET_PROFIT_TRAIL_TIGHTENED. A subsequent net 500 requests EXIT at the preserved 600 floor. |
| 35 alternatives: liquid wider strike 130; ATM strike 105; superficially best contract not available until one second later | Existing bounded selection supported wider strikes, but financial assertions needed hardening | Wider strike wins on observed liquidity/cost before moneyness; future contract remains rejected/unranked. Reversing input order gives identical decisions. |

Known hard exits retain precedence over warning/protection updates. All simultaneous
exit reasons remain recorded, and EXIT stays latched through missing quotes or a
later recovery. Existing replay/durable tests still distinguish decisions, intents,
fills and open exposure. The only replay source edit adds the soft-deadline timer.

## Effective configuration and integration

`src/Trading.Engine/engine.paper.example.json` still exports the complete typed
default profile, and its equality/round-trip test passes. Its configuration content
fingerprint is now `14EA64D069FF1243F08D27C073FDD4DCADA492EB304D8AD1941168F834A85761`.
The base version remains `paper-policy-v1`; the new soft-confirmation subpolicy has
its own explicit version and the content fingerprint prevents old-state reuse.

| Setting | Effective default / status |
| --- | --- |
| Enabled strategy | BANKNIFTY only; canonical `NIFTY` is Nifty / Nifty 50, not another bank universe. NIFTY/SENSEX require explicit independent profiles and inputs. |
| OR / entry / session exit | 09:15–09:30 / 09:30–11:30 exclusive end / 15:15 Asia/Kolkata. |
| Entry setup | Armed inside-to-outside completed-minute crossing, futures VWAP agreement, 65% absolute verified constituent weight, full required coverage. |
| Quantity / capital | One lot; actual point-in-time lot/tick; ₹500,000 example capital, actual supplied cash net of reservations. |
| Entry model budgets | ₹200 gross trigger, ₹400 maximum modeled net entry risk, existing ₹500 credible favorable-scenario hurdle. These are separate experimental settings, not ₹200 fees or promised income. |
| Net position cash stop | `Risk.NetCashStop = null` (disabled). Must be explicitly selected; not silently inferred from the ₹400 entry ceiling. The ₹220 regression setting is a fixture example, not a proposed approval. |
| Soft weakening | **New experimental** `Position.SoftThesisPolicyVersion = soft-confirmation-v1`, `MaximumSoftWeakeningDuration = 00:02:00`. Positive and at most one day. Further ticks never postpone the deadline. |
| Hard thesis / no-progress exit | Two consecutive VWAP-adverse closes plus adverse constituent confirmation; 20 minutes without 0.5R maximum observed underlying progress. |
| Profit | Informational milestone ₹500; `ProfitExit.Mode = Disabled` preserves default. Optional fixed ₹500 booking or net-P&L trailing activation ₹500 / giveback ₹200 / initial floor ₹300 remain existing experimental alternatives. |
| Portfolio | One active/pending **portfolio-wide**, also one per index; three accepted strategy-intent attempts per session, one manual diagnostic allowance. Rejected recommendations consume none; canceled accepted intents still count. |
| Session loss | Provisional paper-only net pause ₹1,200; no approved live daily-loss limit inferred. |
| Quote / scan assumptions | Two-second observation/receipt ages, one-second skew, 1% maximum spread of ask, whole-lot best-level depth on both sides; cap 35 per strict bounded evaluation. |
| Slippage / delay | Example entry/exit adverse slippage zero; declared paper delay 250 ms, pending-entry timeout five seconds. These remain assumptions, not measured broker behavior. |
| Data degradation | Existing warning-only WATCH_EXIT policy; risk/P&L unavailable when unpriced. Restore named evidence on the next relevant event/timer. Existing hard exits and active soft deadlines continue to apply. A separate automatic data-loss liquidation grace is unresolved. |

`PaperPositionEvidence.ExitAdverseSlippage` is a frozen nonnegative option-price
amount, default zero. Quote-qualified hosts must copy their accepted profile's
assumption into the position and calculate the supplied net mark from the same
adjusted sell price. The shared Risk calculator applies adverse tick rounding;
there is no additional spread subtraction. Candle research keeps its separate
declared spread/slippage calculations and does not masquerade as observed quotes.

Position decisions now expose `WatchResolutions` and explicit cash/structural/current
adverse-weight/progress thresholds in gates. `TIGHTEN_STOP` is appended to the enum,
preserving the existing numeric HOLD/WATCH_EXIT/EXIT values. Existing Replay already
uses EXIT alone to request closure and records other actions as strings.

Frozen position fingerprint encoding is `frozen-position-v2`. Old policy state must
be reconstructed while paused, preserving exit/trail history; it must not be cleared
to bypass the binding. Durable Replay reconstructs from ordered evidence under its
existing code/input compatibility rules; its storage schema is unchanged. Allocation
still returns proposals with an expected portfolio version: atomic acceptance is the
existing coordinator's responsibility, not a side effect added to Engine.

## Verification

With the README's user-local SDK on PATH:

```powershell
dotnet build AutoTrader.slnx --no-restore --configuration Debug
dotnet test AutoTrader.slnx --no-build --no-restore --configuration Debug
npm --prefix ui/trading-dashboard run build
dotnet run --file tests/benchmarks/engine-evaluation.cs --configuration Release -p:RestoreLockedMode=true
dotnet run --project tests/benchmarks/replay/ReplayDecisionEvaluation.csproj --configuration Release -p:RestoreLockedMode=true -- --out runs/your-new-immutable-run-directory
```

- Final backend build: **0 warnings/errors**. Full suite: **294 passed**, no skipped
  or failed tests (270 pre-existing + 24 added cases). This includes paper/replay
  cash/fee reconciliation, chronological/future isolation, pending exits, durable
  restart/duplicate recovery and the existing no-real-order request-boundary checks.
- Strict TypeScript and Vite build: passed. No frontend behavior changed; browser
  E2E was not rerun for this engine-only increment.
- The standalone research harness has its own dependency lockfile and reuses the
  existing importer fixture, `ReplayRunner` and `ReplayReportWriter`, rather than
  implementing another fill model. It is tooling, not another application service.

### Internal benchmark

Windows 10.0.26200 x64, .NET 10.0.12, 20 logical processors; 200 warm-ups and 1,000
samples per case. Values below are measured, not cadence/fill guarantees.

| Load | Before p95 ms | After p50 ms | After p95 ms | After max ms | Bytes/sample |
| --- | ---: | ---: | ---: | ---: | ---: |
| One index × 35, scan + Allocate | 0.6708 | 0.4685 | 0.6832 | 1.9060 | 301,339 |
| Three indices × 35, scan + Allocate | 1.7780 | 0.6091 | 2.0913 | 2.8664 | 886,116 |
| One independent position check before one-index scan | not measured | 0.1661 | 0.3434 | 1.1071 | 323,270 |
| One independent position check before three-index scan | not measured | 0.4122 | 0.6992 | 1.3987 | 904,080 |

All cases meet the initial 50 ms p95 target in this run. This is a sequential
microbenchmark with tiered JIT/order effects: later mixed-workload cases being faster
does **not** mean that monitoring reduces scan latency. Do not subtract these timings
to estimate position cost or claim a speed improvement. The position and flat-entry
inputs are independent workloads, not a simulated coherent portfolio/execution loop.
Feeds, feature construction, queue pressure, persistence, rendering and actual timer
cadence are excluded. Allocations remain material.

### Frozen synthetic replay comparisons

Final full reports are under `runs/engine-followup-synthetic-20261019-final/` (generated,
ignored). Six variant directories contain immutable run manifests, normalized candle
data, configs/code provenance, quality, gates, decisions and trades. Parameters and
three fictional sessions (winner, gap loser, no setup) were fixed in harness source
before evaluation. They use an explicitly shortened 09:35 exit and ₹2 synthetic
brokerage per side, not a real broker fee schedule. Each row below has two closed
trades, one no-trade session, ₹8 total charges, 50% win rate and no unresolved positions.

| Exit policy | Execution assumption | Net total ₹ | Net/trade ₹ | Win ₹ | Loss ₹ | Max session drawdown ₹ | Max gross-stop overrun ₹ | Realized net ≥500 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Disabled baseline | base | 157 | 78.50 | 441 | −284 | 284 | 80 | 0/2 |
| Fixed booking | base | 282 | 141 | 566 | −284 | 284 | 80 | 1/2 |
| Conditional net trail | base | 182 | 91 | 466 | −284 | 284 | 80 | 0/2 |
| Disabled baseline | worse spread/slippage | 67 | 33.50 | 396 | −329 | 329 | 125 | 0/2 |
| Fixed booking | worse spread/slippage | 192 | 96 | 521 | −329 | 329 | 125 | 1/2 |
| Conditional net trail | worse spread/slippage | 92 | 46 | 421 | −329 | 329 | 125 | 0/2 |
| Any of these three | one-second delay | −418 | −209 | 216 | −634 | 700 | 430 | 0/2 |

Base declares 20 full-spread ticks / 2 adverse-slippage ticks; stress 40/10;
tick 0.05. A one-second delay in a **minute-opening-only** model can wait almost a
minute, rather than measuring a one-second market response. The loser and stop
overruns are retained. Final stress results demonstrate sensitivity, not estimates
of real fill probability or profitability.

ATM-only and the wider 35-call catalogue produce identical results on these cloned
synthetic price paths; both select strike 55,020. This demonstrates deterministic
mechanics, not a strike-selection edge. The separate strict-quote regression gives
a wider liquid contract a genuine observed-spread advantage and verifies it can win
before ATM, while a future-defined alternative cannot. No later outcome defines
either universe.

Constituent-versus-no-constituent **performance** ablation is not implemented: the
only existing policy requires constituent confirmation. A new explicit research
policy and empirical inputs are needed; deleting its required gate would not be a
valid baseline comparison. Missing-constituent no-entry behavior remains tested.
There is no untouched empirical chronological holdout here and no profitability
inference from these constructed winners/losers. No parameters were selected from
the comparison to target daily income.

## Remaining policy/data limitations

- Nifty 50 and Sensex mechanics are index-agnostic, but strategy profiles and verified
  independent membership/contracts are still required. No readiness claim or copied
  bank universe was added.
- Credible production option repricing/risk and favorable/flat/adverse time/IV
  scenario adapters are absent. Strict entry still requires trusted, quote-bound
  credible risk/opportunity evidence; the new arithmetic floors are necessary, not
  sufficient proof of that model. Missing Greeks remain optional/unavailable, not
  invented. Broader 5-minute/context/flow/event inputs are not added as accidental
  required gates.
- The preserved ₹500 favorable-scenario entry hurdle and optional profit exits are
  different experiments. Final profit-booking/trailing settings, any net stop, a
  no-constituent comparison policy and live daily-loss limits remain unresolved.
- The optional three-bar **underlying** trail after 1R, partial exits and correctly
  protected multi-leg/short structures remain unsupported. The implemented trail
  explicitly uses **net liquidation P&L**, not underlying R or a guaranteed fill.
- Frozen entry references/strategy and replay decision history exist. A complete
  typed entry-versus-current constituent/value-delta snapshot is still partial;
  current adverse weight and structural/progress thresholds are now explicit.
- Live host freshness/tick scheduling, recorder completeness and atomic quote-qualified
  paper acceptance/fills remain separate pending integration. Evaluation has no
  database/network/LLM/system-clock calls and adds no real order system.

Final source review caught and fixed older-prefix warning erasure, pre-first-close
soft weakening, delayed-evidence retrospective clearing, unreconciled structural
R and aggregate reservation underfunding. The initial working tree is largely untracked, so a commit-range diff cannot
isolate this increment; the owned source/test paths and audit above define its scope.

## Requested second inspection — confirmed issues and fixes

Reinspected the current working implementation after the first delivery. Six
concrete decision/integration defects were reproduced through public interfaces:

| Severity | Finding before this second pass | Corrected behavior |
| --- | --- | --- |
| High | `ResearchLifecycle.ProcessEntryOpenings` checked the initial gross stop but did not run the position policy until a later batch. Initial net −₹34 breached an explicit ₹30 net stop, then recovered; the trade incorrectly remained open until SESSION_EXIT. | Evaluate the newly filled position immediately through the existing policy/monitor path, without reevaluating unrelated positions. Zero-delay fixture requests and executes the hypothetical NET_CASH_STOP at entry; net is −₹34. Existing gross-stop attribution remains intact. |
| High | `EvaluateCandlePosition` accepted an old completed close/opening packaged in newly available evidence. Stale values could fabricate NET_PROFIT_TARGET/GROSS_CASH_STOP, ratchet peaks, or supply progress that suppressed TIME_NO_PROGRESS. | Price marks expire by source time; stale candle/opening money becomes null. Stale underlying excursions do not seed progress. Unknown-data warnings, timers and previously latched exits remain active. Replay's current-candle selector applies the same source-end age bound. |
| High | `EvaluatePosition` accepted a profitable full-position mark with null/zero/24-unit bid depth for a 25-unit position. It could book profit or raise protection despite insufficient executable quantity. | POSITION_EXIT_DEPTH_UNAVAILABLE / WATCH_EXIT, null gross/net liquidation and no peak/profit ratchet. A known fresh bid loss still requests GROSS_CASH_STOP with its separate observed-value gate, while exposure/P&L remain unqualified until executable evidence arrives. |
| High | Latest bank weakness could be combined with retained wrong-side-close counts from an older period when the current futures feature was missing. A supportive latest close could incorrectly receive THESIS_FAILURE / EXIT. | The hard-failure recheck requires close-count state ending at the same completed period as the bank observation. Missing current futures features remain WATCH_EXIT; restoring a supportive feature resets the close count without inventing a hard exit. |
| Medium | The soft warning cleared when VWAP/banks were merely non-adverse. A close exactly on VWAP with neutral constituent weight incorrectly returned HOLD and erased the deadline. | Only a strict directional VWAP close **and** configured directional constituent weight clear an existing warning. Neutral/mixed evidence retains SOFT_THESIS_CONFIRMATION_PENDING and the original deadline. |
| Medium | Latest-entry-period weakening detection was nested under both feature and constituent availability. Known VWAP weakening with missing banks, or known adverse banks with missing futures features, had no deadline. | Detect either known weakness independently; missing required evidence still blocks safe HOLD/recovery. Both cases retain the bounded deadline and can time out on a timer without more ticks. |

No numerical configuration defaults were changed in this second pass. These fixes
enforce the existing meaning of supportive recovery, quote-qualified liquidation,
source freshness and immediately monitored cash risk.

New behavioral coverage: `DecisionReinspectionTests.cs` and the additional
entry-net-stop case in `ReplayDecisionFollowupTests.cs`, **17 cases**. The six
defects failed before their respective fixes. Additional controls cover fresh candle
marks, preserving known cash stops through missing depth, latched exposure and old
underlying progress. Public Engine and Replay seams are used; no private-method tests
or new execution model were added.

Latest verification after this pass:

- Debug solution build: **zero warnings/errors**.
- Full backend suite: **311 passed**, zero failed/skipped (294 prior + 17 new).
- Frontend strict TypeScript and Vite build: passed.
- Existing delayed exits, gross-stop attribution, fee/cash reconciliation, durable
  restart recovery and no-real-order request-boundary regressions passed.
- Benchmark under the same synthetic methodology: scan + Allocate p95 **0.7206 ms**
  for one × 35 and **2.0274 ms** for three × 35. Independent position-check-before-scan
  workloads p95 **0.2992 / 0.6822 ms** respectively. Same tiered-JIT/order caveats apply;
  these are not real feed/fill/cadence or profitability measurements.

The earlier generated comparison reports remain immutable evidence of their code
version; they were not rewritten as second-pass reports. Remaining empirical/data
and strategy limitations listed above still apply. Browser E2E was not rerun.
