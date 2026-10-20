# Prediction research — corrected implementation brief

Prepared: 20 October 2026. Status: proposed research increment; no predictor,
trained model, live recorder or new trading policy is delivered by this document.

## 1. Purpose and precedence

Preserve the current rule-based engine as a versioned benchmark. Build an auditable
prediction dataset alongside it, compare small models chronologically, and evaluate
option feasibility separately. A predictor supplies evidence, not permission to
bypass contract, liquidity, cash, cost or risk gates or delay a protective exit.

This brief incorporates the supplied report, **Prediction Component Research:
Short-Term Direction for Nifty, Bank Nifty and Sensex Options** (20 October 2026),
and the user's accompanying corrections. The original report contains research
candidates and qualified outside-market evidence, not established trading rules.
This brief governs interpretation of that report for prediction research. It does
not change the implemented engine profile or the 12 October sprint's ML deferral.

Read [the sprint](08-MONDAY-LIVE-PAPER-SPRINT.md),
[data/replay rules](03-DATA-AND-REPLAY.md),
[engine ownership](11-ENGINE-SESSION-HANDOFF.md),
[the correctness audit](17-DECISION-ENGINE-AUDIT.md) and
[the current follow-up](18-ENGINE-DECISION-FOLLOWUP.md) with current source.

### Current implementation anchors

| Existing seam | Research implication |
| --- | --- |
| `Trading.Engine/TradingDecisionEngine.cs:EvaluateIndex` and `PortfolioAllocation.cs:Allocate` | Freeze configuration, code and input provenance as benchmark v0. Evaluation and allocation recommendations are not accepted intents or fills. |
| `Trading.Engine/PositionPolicy.cs:EvaluatePosition` | Preserve independent quote/timer evaluation, hard-exit precedence, latched exits and frozen position policy. |
| `Trading.Engine/EngineContracts.cs` | Existing evidence distinguishes observation/event, receipt and availability times. Coordinate new canonical contracts; fixture dashboard records are not prediction inputs. |
| `Trading.Replay` | Reuse deterministic replay, manifests, accounting and explicitly hypothetical candle fills. Candle results cannot qualify subsecond quote execution. |
| `Trading.Recorder` | Currently an empty library; live feed/cache/recording remains an implementation dependency. |
| `Trading.Research` | Currently a README placeholder, not a build project. First offline dataset/evaluation tooling may live in Replay; choose placement before adding a project. |

Bank Nifty is the only configured default strategy. Nifty/Sensex identities do not
authorize strategies, verified constituents or pooling of training observations.
Keep index-specific results and contracts; cross-index pooling is a later experiment.

The current profile's **₹500 credible favorable-scenario entry hurdle** is separate
from its **₹500 informational milestone** and optional **profit exits**. Preserve
all three meanings in v0. A prediction experiment must not silently remove the
entry hurdle, turn it into a forecast, or replace the existing exit policy.

## 2. Corrections to carry into implementation

| Report statement | Required interpretation |
| --- | --- |
| Two independent up-first/down-first logistic models | Start with one regularized multinomial logistic model for three mutually exclusive outcomes. Independent probabilities do not supply a valid timeout by subtraction. |
| Exchange-time alignment instead of arrival-time alignment | Keep both; availability determines decision visibility. Exchange time can describe the observed market interval, never backdate knowledge. |
| Edge must exist at 5–30 minutes because the feed is sampled | Unsupported inference. Horizons are preregistered experiments, not consequences of sampling frequency. |
| Depth is an execution filter rather than directional evidence | Retain execution gates and test sampled depth as an optional directional feature family. It is not complete exchange order flow. |
| Fold-ensemble spread is an uncertainty band | Call it **model disagreement**. Do not label it a confidence interval or use it as a net-expectancy bound without a separate justified method. |
| First-touch probabilities determine option expectancy | They do not specify touch time, timeout returns, IV or the actual exit path. Missing valuation inputs leave expectancy unavailable. |
| Fixed-horizon prediction is diagnostics only | Keep fixed-horizon up/down/flat as a full competing benchmark. Neither target wins in advance. |
| Four weeks of collection is sufficient to begin reliable fitting | Four weeks is a recording checkpoint. Overlapping rows and a few setups per day do not establish independent sample adequacy. |
| Forecast every 30–60 seconds | Optional forecast cadence only. Protective quote checks and time exits remain independent. |

## 3. Dataset and availability contract

Record append-only raw events plus normalized observations, immutable daily
instrument definitions, effective membership/weights, fee schedules, universe
versions, engine decisions, accepted intents and paper fills. Use UTC internally
and Asia/Kolkata for session/calendar interpretation. Retain source/schema,
normalization, feature, configuration, code and model versions with hashes.

Each observation must preserve, where supplied:

- Provider event/exchange time, local receipt time, bid/ask observation time and
  last-trade time separately; missing provider fields remain null.
- A host-assigned availability time and persisted sequence/tie-break order. Derived
  feature availability is no earlier than its latest input availability and actual
  publication. A completed bar also requires its end time to have passed.
- Data quality: gaps, reconnects, stale values, clock anomalies, field coverage,
  recorder loss/pressure and contract/reference-data validity.

At decision time `t`, consume only evidence available at/before `t`, including its
persisted order relative to that decision. An exchange event timestamped before
`t` but received afterward cannot affect that decision. Late corrections are later
evidence and must not rewrite the decision. Historical candles without receipt
evidence use a declared availability assumption and retain that limitation.

Align feature windows on common boundaries using **as-of available** observations.
If a 5–10 second grid is tested, carry-forward is bounded by declared age limits and
is feature-only: never turn it into an executable quote, fill or invented bar.
Record constituent missing weight explicitly; do not renormalize observed members
to hide missing inputs. Use point-in-time effective and available weights. Any
intraday weight-drift approximation needs its own reference-price/corporate-action
convention and version; it is not automatically an official index reconstruction.

Separate decision inputs from later labels/counterfactuals. Record every scheduled
research decision point, including no setup, rejected gates and abstention. Distinguish
these rows from portfolio-admissible trades. Training may consume an outcome only
after its path and label have become available, not merely after the nominal horizon.
Recorder incompleteness raises degraded state and inhibits new entries.

## 4. Targets and probability outputs

Freeze target definitions before evaluating a holdout. Use the selected point-in-time
futures contract with an explicit roll rule; do not splice contracts mid-label without
a declared method. Cash-index targets are a separately versioned alternative.

### A. Fixed-horizon benchmark

At `t`, freeze the reference price, horizon `H` and positive flat threshold `epsilon`
from available evidence. Classify the horizon return as UP when greater than
`epsilon`, DOWN when less than `-epsilon`, otherwise FLAT. Specify price sampling,
boundary equality, units and endpoint missing-data behavior. A volatility-scaled
threshold is legitimate if volatility is estimated only from the available prefix.

### B. First-touch experiment

At `t`, freeze reference `S0`, positive upper/lower distances `bUp`/`bDown` in
underlying points, and horizon `H`. Distances may use past-only volatility; their
scaling and any cost-aware conditioning must be versioned. Initially prefer a
common underlying target across options, with option costs assessed afterward.

- **UP_FIRST:** `S >= S0 + bUp` before the lower barrier, at/before `t + H`.
- **DOWN_FIRST:** `S <= S0 - bDown` before the upper barrier, at/before `t + H`.
- **TIMEOUT:** neither barrier touched through `t + H`, with sufficient path coverage.
- Record touch/timeout time, observed overshoot, terminal return and coverage.
  Define an endpoint touch as taking precedence over timeout.

Specify whether labels describe the **observed sampled path** or a finer licensed
path. A sampled path does not prove that no intervening touch occurred. Do not
interpolate missing events. A gap that prevents ordering, a candle touching both
barriers, or indistinguishable simultaneous touches is **AMBIGUOUS/UNRESOLVED**
quality, not a fourth modeled outcome or a timeout. Exclude such labels from the
initial three-class fit, disclose exclusion rates, and report conservative/adverse
sensitivity separately. A gap across a barrier records the next observed price/time,
not a fictitious exact-barrier fill. Truncated session/contract paths are censored
unless a shorter-horizon target was explicitly defined at prediction time.

These are underlying-path labels, not position outcomes. Strategy invalidation,
cash stop, trailing and session exits may occur earlier. Evaluate option results by
replaying the frozen actual exit policy; do not substitute barrier labels for it.

### Coherent model and output validity

Start with regularized multinomial logistic regression for each target. Fit scaling,
imputation, feature selection and regularization only within the training workflow.
Optional calibration must use a later, training-independent calibration period and
preserve a jointly valid probability vector. Do not independently calibrate classes
and assume they still sum to one.

Every valid forecast contains target/index/contract identity, decision and publication
times, reference/barrier/horizon definition, model/training-cutoff/feature versions,
quality flags, and three finite probabilities in `[0, 1]` summing to one within a
declared numerical tolerance. First-touch uses `pUpFirst/pDownFirst/pTimeout`;
fixed-horizon uses `pUp/pDown/pFlat`. Invalid/missing evidence produces an unavailable
forecast with reasons, not zeros or a fabricated neutral probability. Any bias or
abstention threshold is versioned research configuration. Optional ensemble spread
is reported as model disagreement only.

## 5. Option screening, costs and risk

For a favorable underlying move, a first-order screening approximation is:

```text
abs(required underlying move) ~= (T + C_round_trip + L_time) / (Q * abs(delta))
```

- `T`: desired **net** profit, total rupees; zero for break-even.
- `C_round_trip`: total rupee modeled fees/taxes and execution friction, with a
  declared price reference and each component counted once.
- `L_time`: assumed total rupee time-decay loss over the holding period.
- `Q`: total option units, whole lots × point-in-time lot size.
- `delta`: option premium points per one underlying point, per option unit,
  snapshot/model-versioned. Thus `Q * abs(delta)` is rupees per underlying point.

Example, purely arithmetic: `T=500`, `C=120`, `L=0`, `Q=25`, `abs(delta)=0.5`
requires approximately `49.6` underlying points; break-even requires `9.6` points.
These are invented inputs, not current lot sizes, costs or a market forecast.
Missing/zero/unreliable delta or missing cost assumptions makes this estimate
unavailable. Gamma, IV changes, varying delta and touch time can invalidate it.

If scenario P&L already uses ask entry and bid exit plus adverse slippage/tick
rounding, execution friction is embedded: deduct only remaining fees, not spread
again. Likewise do not separately subtract theta when scenario repricing already
models time decay. Keep the approximation's reference-price decomposition explicit.

Three first-touch probabilities alone cannot produce option net expectancy. A
credible estimate additionally requires conditional touch times, timeout returns,
IV/time scenarios or observed option paths, executable prices/depth, delayed fills,
fees and the complete exit policy. Without these, expose `netExpectancy = unavailable`
with reasons; the approximation cannot certify the existing favorable-scenario or
risk gate. A lower confidence bound needs a specified statistical method and data,
not fold disagreement. Shared financial calculations belong in .NET Analytics/Risk.

Keep existing hard validity/freshness/liquidity/whole-lot/cash/risk/cost gates before
ranking. Model refresh, failure or recalibration cannot postpone quote-driven cash
checks, timer/session exits or pending-exit recovery. No predictor may widen a risk
budget or clear a latched exit. Real broker execution remains deferred.

## 6. Experimental evaluation

1. **Freeze v0 and dataset conventions.** Capture the current rule decisions and
   complete profile; retain losers, unavailable evidence and zero-trade sessions.
2. **Create futures-only features first.** Small past-only return/volatility,
   futures VWAP and opening-range families; candle VWAP is labelled approximate.
   Cash indices do not have traded volume for VWAP. Bound actual encoded feature
   count; an aggregate family can contain several model coefficients.
3. **Compare targets and feature families.** Fixed-horizon versus first-touch;
   futures-only versus a few verified constituent aggregates (contribution,
   breadth, concentration/divergence); optional sampled-depth family; placebo
   constituents. Treat correlated trend features as one family in ablations.
4. **Use chronological train → calibration → validation → untouched test periods.**
   Purge overlapping label intervals across boundaries, specify any embargo and
   overlapping-sample weighting, and tune only inside development windows. Prevent
   calibration/training labels from extending into later evaluation periods.
5. **Make comparisons paired.** Use identical sessions, decision times, target
   definitions and matched coverage for feature ablations. Report coverage-only
   differences separately. Tune targets/horizons on development data, then compare
   their frozen downstream policies on common untouched periods. A valid no-constituent
   rules benchmark needs its own explicit research policy; do not delete v0's required
   confirmation gate or pretend that ablation already exists.
6. **Report forecasts and decisions separately.** Multiclass Brier/log-loss,
   reliability, class/base-rate and regime results; policy net/trade and net/day,
   fees, turnover, abstentions, no-trade share, worst day, drawdown, loss streaks,
   pending/unpriced exits and stop overruns. Include no-trade and seeded random
   policy controls with identical applicable gates, capacity and exits. Raw losses
   across different target definitions are not directly equivalent tasks.
7. **Stress costs and delay.** Version base/adverse fees, spreads, slippage and
   delays. The report's +2 ticks and +5 seconds are candidates to preregister, not
   universal robustness laws. Minute-opening candle fills cannot measure a genuine
   five-second quote response. Keep candle and quote-qualified results separate.
8. **Quantify uncertainty honestly.** Paired day-block bootstrap can assess incremental
   held-out forecast loss and policy P&L; declare the estimand, block assumptions,
   sample count and limitations. Few sessions or dependent regimes can leave bounds
   inconclusive. Track every model, target, threshold and feature variant tried.

Record actual feed interarrival/receipt-delay distributions and field coverage by
instrument/session. A historical forum tick-rate description is not a current SLA.
Measure independent sessions, class counts and overlap; do not count correlated
30-second rows as independent samples or promote a model after four calendar weeks.
Keep more complex models deferred until untouched evidence justifies a separate
experiment. External deep-learning degradation is cautionary evidence, not a proof
that every deep model is unsuitable.

## 7. Delivery slices and acceptance evidence

| Slice | Deliverable | Completion evidence |
| --- | --- | --- |
| 0 — freeze and audit | Versioned benchmark/profile and dataset/label manifest; inventory missing observed fields | Reproducible v0 decisions and explicit candle-versus-quote qualification gaps |
| 1 — offline dataset | As-of feature rows, quality flags, later labels and coverage report using existing replay inputs | Future isolation, late-receipt exclusion, completed-bar timing, gap/both-touch cases, deterministic ordering and replay hashes |
| 2 — offline model comparison | Small target models, coherent calibration, paired ablations and frozen chronological report | Valid joint probabilities, train/calibration isolation, unchanged holdout, retained losses/abstentions and reproducible artifacts |
| 3 — quote-qualified research | Live recorder/feed plus separate simulator integration when those dependencies are implemented | Delayed post-intent bid/ask/depth fills, fee/cash reconciliation, duplicate prevention, stale/restart recovery, independent stops/timers and zero real-order requests |
| 4 — shadow evidence integration | Optional model evidence beside v0 decisions; activation remains separately specified | Predictor unavailable/stalled never blocks protection; immutable model/decision provenance and explicit research labels |

The next implementable slice is **0–1, offline dataset and label tooling**, with
historical coverage limitations made explicit. Recorder integration is a separate
dependency. Choose training runtime/artifact format, exact horizons/barriers/flat
thresholds, feature definitions, split dates and data-adequacy/promotion criteria
before model implementation; the supplied report does not settle them. A proposed
15-minute horizon or 30–60-second refresh is an experimental input, not a default
to insert into the current engine profile. Do not add an automatic model-driven
entry switch as a side effect of dataset work.

Before editing, coordinate shared Domain/Analytics/Risk/API/Replay contracts with
the engine owner under document 11. Keep provider access and storage outside pure
evaluation, reuse shared calculations and existing replay accounting, and preserve
existing run evidence. Run backend build/tests for implementation changes and the
frontend build for integration changes, following the repository guides. Synthetic
checks establish software behavior, not independent predictive edge or profitability.

## 8. Factual qualifications and reference trail

These links accompany the supplied correction review; they are not a claim of new
authenticated coverage checks or comprehensive independent source validation in
this documentation increment. Record retrieval/effective dates when importing
contract, fee or provider facts for an actual run.

- **Bank Nifty:** weekly options were discontinued; NSE lists monthly **and
  quarterly** options. Do not hardcode “monthly only” or infer live expiry calendars
  from the report: [NSE product specification](https://www.nseindia.com/static/products-services/equity-derivatives-banknifty).
- **STT:** the review cites NSE's published 0.15% option-sale rate effective
  1 April 2026. Use effective-dated transaction-side rules, not a timeless blanket
  cost: [NSE STT schedule](https://www.nseindia.com/static/products-services/equity-derivatives-securities-transaction-tax).
- **Kite history:** documented continuous expired-futures data is daily. This is a
  provider/route qualification, not proof that no source has expired intraday data:
  [historical API](https://kite.trade/docs/connect/v3/historical/).
- **Feed:** historical sampled-update guidance is not guaranteed current cadence:
  [forum discussion](https://kite.trade/forum/discussion/3225/how-websocket-api-works),
  [packet/field documentation](https://kite.trade/docs/connect/v3/websocket/).
- **Static IP:** Zerodha's cited restriction concerns API order placement; its FAQ
  says market-data endpoints remain accessible from other IPs. It does not block
  prediction/paper data collection: [Kite API FAQs](https://support.zerodha.com/category/trading-and-markets/general-kite/kite-api/articles/kite-connect-api-faqs).
- **Probability semantics:** multiclass versus independent classifications must be
  explicit regardless of the chosen training runtime:
  [scikit-learn OneVsRestClassifier documentation](https://scikit-learn.org/stable/modules/generated/sklearn.multiclass.OneVsRestClassifier.html).
- **Generalization caution:** [LOBCAST](https://arxiv.org/abs/2308.01915) concerns
  its evaluated models/datasets, not a universal verdict on deep learning or
  evidence of an Indian index-option edge.

The original report's other literature claims remain context, not verified local
performance. In particular, this brief adopts no expected accuracy, profitable
holding horizon, production threshold or minimum daily trade count from them.
