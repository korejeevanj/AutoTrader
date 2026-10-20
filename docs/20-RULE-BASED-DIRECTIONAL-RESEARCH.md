# Rule-based directional research through Replay

20 October 2026. Implements the rules-first increment agreed after
[the prediction research brief](19-PREDICTION-RESEARCH-BRIEF.md).

## Scope and existing policy

Use explicit `if/else` policy, not a trained predictor or probability score. The
completed-minute opening-range/VWAP crossing and absolute weighted constituent
confirmation remain the benchmark. This increment adds an auditable directional
snapshot, one optional breadth-count rule, offline underlying-path outcomes, and
paired replay comparison. Option validity, cash/cost/risk gates and independent
position exits retain their distinct responsibilities. The existing ₹500 favorable
scenario entry hurdle is separate from the milestone and profit-exit configuration;
no expected option profit is inferred from direction.

## Directional evidence

`EvaluateIndex` / `EvaluateAllCandidates` return `DirectionalEvidence` alongside
candidate assessments. It records available completed-bar identity/prefix, clock,
configuration fingerprint and evaluation identity; opening-range/VWAP distances
in underlying points; past one/five-minute percentage returns; absolute above,
below and missing constituent weight; and same-period per-member observations.

For an active direction it records confirming, opposing and neutral member counts
and the **absolute index weight** of the largest three confirming members. Missing
weight is not renormalized. Incomplete constituent coverage leaves directional
counts/concentration unavailable. States are UP, DOWN, NO_SETUP,
INSUFFICIENT_DATA or NOT_EVALUATED (manual setup bypass). Passing directional
rules does not mean an option qualifies, an intent was accepted or a fill occurred.

The API projects a dedicated UI view instead of exposing Engine evidence as
client-authorized inputs. In **Replay → Overview**, inspect **Directional
assessment** while stepping/playing. Future-dated evidence is rejected by the UI.
Price-only playback has no directional assessment. Full evidence is retained in
replay feature traces and durable batches, separately from later labels.

## The optional rule

In `src/Trading.Engine/engine.paper.example.json`, each strategy has:

```json
"BreadthPolicyVersion": "constituent-count-v1",
"MinimumConfirmingConstituents": null
```

Null disables the extra rule. A positive integer requires that many verified,
same-period members strictly on the direction's side of their own approximate
VWAP, **in addition to** the existing absolute weighted threshold. Equality is
neutral. Missing observations do not pass the count rule. Failure/unknown appears
as `CONSTITUENT_BREADTH` in strict and candle-research gates before ranking.
Manual diagnostics retain setup-only bypass; entry filters cannot slow cash stops.

Choose a count as an explicit research hypothesis, not an approved market threshold.
Policy content fingerprints include the new settings. Existing durable runs and
reports belong to their frozen code/configuration; start a new compatible run after
an implementation/profile change instead of rewriting prior evidence.

## Offline outcome conventions

`src/Trading.Replay/replay.example.json` exposes:

```json
"DirectionalResearch": {
  "Version": "directional-labels-v1",
  "HorizonMinutes": 5,
  "BarrierPercent": 0.05,
  "FlatThresholdPercent": 0.01
}
```

These defaults are research assumptions, not empirically selected trading rules.
Percent means percent: `0.05` is 0.05%, not a 5% move. Horizon is a positive whole
number of minutes, at most 120; both thresholds must be positive and below 100%.

- Export every scheduled entry decision, including no setup, rejection and missing
  evidence. The reference is the canonical as-of futures close, never an option
  mark or subsequently available price.
- The label window starts at the next whole-minute boundary at/after decision time;
  its end is that start plus the horizon. When publication lag makes the current
  minute partial, exclude that minute's OHLC. The reference remains the decision-time
  completed close. This deliberately delayed window is explicit in each row and
  does not claim to observe the excluded interval.
- Fixed horizon: UP above the positive flat threshold, DOWN below its negative,
  otherwise FLAT, including equality. Return is percentage change from the reference.
- First touch: symmetric barriers frozen from reference × configured percent.
  Opening across a barrier establishes the first observed touch at that opening;
  otherwise a touch has a candle interval, not an invented intraminute timestamp.
  A candle touching both while opening between them is AMBIGUOUS. A covered path
  without a touch is TIMEOUT; a missing/conflicting minute is UNRESOLVED.
- An uncovered session/contract tail is CENSORED. An unfinished replay yields PENDING
  where observations have not yet become available, even if the captured dataset
  contains future values. Unusable reference evidence is UNAVAILABLE. A known first
  touch remains resolved even if the later horizon cannot complete.
- Touch/terminal availability follows completed-candle availability; even an opening
  touch conservatively uses completion availability in this report.
- Actual policy exit reason/net result and whether it exited before the horizon
  are separate columns. Cash stops, invalidation, trailing and session exits need
  not match these barriers. No option net expectancy is manufactured.

Labels are calculated by the report evaluator, never attached to playback steps
or fed into policy. Overlapping windows and execution presets are not independent
training observations.

## Test through the application

1. Prepare a historical day with verified strategy inputs through the existing daily
   setup/replay workflow. Price-only data cannot evaluate a strategy.
2. Step/Play and inspect the directional assessment, constituent evidence and decision
   gates. Use Run Full Day to finish the current execution preset.
3. In **Run evidence → Completed run reports**, download `directional-evidence.csv`
   for as-of context, and `directional-outcomes.json` / `.csv` for later outcomes
   alongside actual policy exits. Settings/window convention are in JSON. Existing
   trades, fees, decisions, quality and provenance reports remain available.

The UI compares execution presets through its existing controls. The new **rule**
comparison is available through CLI/library; editing the example engine profile
affects new preparations, not already frozen jobs.

## Paired rule comparison

With the README's pinned SDK on PATH, from the repository root:

```powershell
dotnet run --project src/Trading.Replay.Cli --configuration Debug -- compare-rules --dataset data/raw/replay/2026-10-09/dataset.json --config src/Trading.Replay/replay.example.json --minimum-confirming-constituents 3 --out runs/replay/breadth-comparison-01
```

Replace dataset/output paths and select the count **before** examining evaluation
results. The example count is illustrative. The command preserves the supplied
engine policy as baseline, changes only enabled profiles' count and policy version
for the variant, and replays identical sessions/presets/costs/exits.

Outputs are immutable `baseline/` and `variant/` full reports plus `comparison.json`
and `comparison.md`. Each session/preset pair records trade counts, diagnostic net,
completion issues and a net difference **only if both sessions qualify**. Incomplete
pairs do not contribute to aggregate differences. Alternative execution presets are
summarized separately, never added together as portfolio performance. Zero trades is valid. Library:
`ReplayRuleComparison.Run` and `ReplayReportWriter.WriteComparison`. No auto-tuning.

For a credentials-free demonstration using the existing three-session synthetic
harness (winner, loser and no setup):

```powershell
dotnet run --project tests/benchmarks/replay/ReplayDecisionEvaluation.csproj --configuration Debug -p:RestoreLockedMode=true -- --compare-breadth --out runs/rule-direction-synthetic-your-new-run
```

The fixture uses count **11** against **10** confirming members purely to exercise
rejection and zero trades. It is not a proposed Bank Nifty threshold. Use a new output
directory each time. Synthetic outcomes prove software behavior; untouched empirical
evaluation, production option valuation, live recording and quote-qualified paper
fills remain separate dependencies.

## Verification scope

Public Engine checks cover literal metrics, absolute missing weight, future
isolation, both directions, neutral members, optional filter behavior and independent
cash stops. Replay/report checks cover fixed versus path outcomes, both-touch
ambiguity, gaps, partial-minute exclusion, pending labels, actual policy exits,
paired CLI execution and API downloads. Browser checks cover presentation and
rejection of future directional evidence on desktop/mobile.

### Verified results for this increment

- Debug solution build: zero warnings/errors. Full backend suite: **332 passed**,
  including **21 new** Engine/Replay/API cases. Existing fee/cash, delayed exit,
  durable restart/duplicate prevention and no-real-order boundary checks passed.
- Frontend strict TypeScript + Vite build: passed.
- Replay Playwright suite: **50 passed** across desktop/mobile on isolated ports
  5380/5373; the servers shut down afterward. Browser replay uses controlled HTTP
  responses; the new backend API test independently exercises actual fixture
  acquisition → replay → report downloads. Neither is an authenticated live test.
- Existing internal Release benchmark, 200 warm-ups / 1,000 samples: scan + Allocate
  p95 **3.7925 ms** for one index × 35 and **1.7559 ms** for three × 35. Independent
  position-check-first cases p95 **0.7973 / 1.6215 ms**. These exclude feed, feature
  construction, storage and UI. Concurrent verification and tiered-JIT/order effects
  prevent before/after speed or additive position-cost claims; all met the existing
  50 ms internal target in this run.
- Whitespace checks cover the tracked diff and the explicit session-owned untracked
  files. No dependency, provider credential or real-order interface was added.

Final synthetic artifacts: `runs/rule-direction-synthetic-20261020-final/`, with
three constructed sessions × three execution presets. Baseline totals were ₹157
(base), ₹67 (worse spread/slippage), and −₹418 (one-second-delay candle model).
The deliberately overrestrictive count-11 variant made zero trades in every case.
It skipped winners as well as losers; these results establish neither an optimal
count nor a profitable rule. Presets are separate alternative assumptions. Minute
opening-only fills with a one-second delay can wait until a later minute opening,
so those results do not measure real one-second quote execution.

### Standards review

No documented-standard violations found. The initial duplicated confirmation-policy
suggestion was addressed with a shared gate helper and reuse of the opening chart.
Read-only reinspection found the consolidation sufficient.

### Spec review

Three concrete findings were reproduced before fixing: loss of a last-valid-minute
touch at contract expiry, capture-tail censoring, and unknown cash masquerading as
no directional crossing. Public regressions now pass; entry remains cash-blocked
while the local explanatory detector can expose the crossing. Read-only reinspection
reported all three addressed. A final reporting check separately verified per-preset
aggregation and exclusion of incomplete pairs. No remaining concrete findings were
reported in the reviewed increment.
