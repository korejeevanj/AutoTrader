# 14 October replay loss audit

Investigated 20 October 2026 after the reported approximately ₹691 loss.
Outcome: the saved **−₹691.88** is reproduced exactly. Fill, fee and realized-cash
accounting reconcile. The strongest identified policy issue is disabled profit
protection on a trade that first accumulated a substantial favorable modeled mark.
The candle option-ranking assumptions and cost/stop fit also need evaluation.

## Evidence and reproducibility

- Job: `8a1afb7dec334f668dd75e294930624e`, date `2026-10-14`.
- Authoritative committed run: `8a1afb7dec334f668dd75e294930624e:run-2:base` in
  `data/replay/paper-replay.sqlite`. Read-only inspection uses its checkpoint,
  frozen run manifest and compressed evaluation batches.
- The run's report publication was left staged under `reports/run-2.publishing-...`;
  CSVs there are not a completed publication. The audit uses committed ledger
  evidence and a separate replay, not an assumption that those staged files finished.
- Frozen input hash: `B6AF1B44C8420A77DDF381D75178C5AD5A1F124A54791D9B19BCA54CC600EABC`.
- Dataset: `data/replay/jobs/8a1afb7dec334f668dd75e294930624e/attempt-1/dataset.json`.
  Manifest hash: `1CFBA0BD4D13AD2A298B22835CBC2BB0B4CF1909CF29E8B4499AD9C5D8372799`.
- The audit replay's Replay/Engine/Risk/Analytics assembly MVIDs match those in
  the frozen original run. It reproduced both contracts, setup IDs, units, entry
  and exit times/prices, trade net results and aggregate net before comparisons.
- New diagnostic artifacts: `runs/loss-audit-20261014/audit.json` and five variant
  files. They preserve effective settings, fingerprints, signals, modeled position
  marks, top-ranked candidates and all actual hypothetical trade costs/exits.

## Exact loss reconciliation

Both trades bought one **30-unit** lot of `NFO:BANKNIFTY26NOV55500CE`.
All times in this table are Asia/Kolkata; candle stop execution has an interval,
with a later known-at timestamp, rather than a precise intraminute fill time.

| Trade | Entry | Exit known at | Entry price | Modeled exit price | Gross ₹ | Entry + exit fees ₹ | Net ₹ |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 09:36 | 09:37 (09:36–09:37 interval) | 1,360.80 | 1,354.00 | −204.00 | 143.43 | −347.43 |
| 2 | 09:43 | 09:54 (09:53–09:54 interval) | 1,321.60 | 1,314.80 | −204.00 | 140.45 | −344.45 |
| Total | | | | | **−408.00** | **283.88** | **−691.88** |

The ₹200 setting is a **gross cash-stop trigger**, not a total net-loss cap. Each
modeled fill lost ₹204 after adverse slippage/tick rounding, a ₹4 gross overrun.
Fees were then deducted once. Final cash was ₹499,308.12 from ₹500,000, with no
pending intents, open positions or reserved cash.

The frozen ₹400 modeled entry-risk ceiling is separate from a net stop;
`NetCashStop` was null. Pre-entry modeled risk was ₹397.43 and ₹394.45, including
the declared ₹50 gap stress, so those entries passed the configured research gate.
At 30 units a ₹200 gross trigger allows approximately **6.67 option premium points**
of adverse movement. Initial round-trip friction was already ₹179.51 / ₹176.52,
including spread/slippage and fees. Simply setting a ₹200 net stop would leave
very little headroom; whole-lot cost/risk feasibility must be assessed first.

## Finding 1 — a profitable second trade had no profit exit

The frozen `Position.ProfitExit.Mode` was `Disabled`. The ₹500 milestone was
informational, consistently with [the profit-exit specification](16-PAPER-PROFIT-EXITS.md).
Recorded net liquidation marks for the second trade were:

| Time / IST | Modeled net mark ₹ | Baseline policy |
| --- | ---: | --- |
| 09:43 | −176.52 | Entry / WATCH_EXIT (underlying monitoring warm-up) |
| 09:44 | 335.26 | HOLD |
| 09:45 | 647.63 | HOLD; ₹500 milestone is not a target exit |
| 09:49–09:50 | **1,208.89** | HOLD; profit exits disabled |
| 09:52 | 999.98 | HOLD |
| 09:53 | 429.72 | HOLD |
| 09:54 | **−344.45 realized** | Hypothetical intrabar gross cash stop |

These are candle-model liquidation marks, not verified executable quote profits.
The protection capability existed but was not selected in this run. This explains
why a positive intermediate mark was allowed to reverse into a full loss.

## Finding 2 — modeled spread percentage biased option ranking

The configured first ranking criterion was `SpreadPercentage`, followed by total
friction, strike distance and observed depth. Candle research has no observed
bid/ask/depth. Its preset imposes the same **20-tick full spread** on each contract;
for the selected contracts this is ₹1 per option unit. Therefore spread percentage
is approximately `1 / premium × 100`: a higher premium looks better even though
no superior liquidity was observed.

At 09:36 the selected November 55,500 call ranked first at modeled spread **0.07352%**,
₹179.51 round-trip friction and 249.8 underlying points from its strike. Several
alternatives had lower modeled rupee friction but a worse percentage and therefore
lost the first comparison. The selected plan's risk was close to the ₹400 ceiling.

This is an identifiable **research ranking weakness**, not evidence that November
options were genuinely more liquid. Reprioritizing strike distance alone reduced
fees in the diagnostic comparison below but still lost both trades. A useful
selection increment must evaluate expiry/moneyness, costs and credible opportunity
with available evidence rather than assume either ordering is profitable.

## Finding 3 — extra breadth would not have prevented these entries

Both signals passed the actual ORB/VWAP/weighted-confirmation rules:

| Entry / IST | Futures completed close | Opening high | Approx. futures VWAP | Bullish weight | Confirming banks |
| --- | ---: | ---: | ---: | ---: | ---: |
| 09:36 | 55,749.8 | 55,702 | 55,588.75 | 79.74% | 12 / 14 |
| 09:43 | 55,735.0 | 55,702 | 55,608.80 | 65.83% | 11 / 14 |

There was zero missing weight in these decision snapshots. The newly implemented
optional count rule was disabled; setting the documentation's illustrative count
of 3 produced **identical trades and −₹691.88**. Raising a count after observing
these losses would be hindsight tuning, not evidence of a general improvement.

The exported five-minute underlying labels were DOWN (approximately −0.188%) for
the first signal and UP (+0.0341%) for the second. Both were labelled UP_FIRST
under the configured ±0.05% barrier/window convention. This actual example shows
why an underlying first-touch label does not establish option profit or replace
the position policy.

## Controlled diagnostic comparisons

Each comparison changed one policy group from the exact frozen baseline. Existing
₹500 fixed/trailing settings and illustrative breadth count 3 were declared in
the diagnostic harness before evaluation; there was no parameter search. All
other prices, fees, gross stops, capacity and base execution assumptions were reused.

| Configuration | Trade 1 net ₹ | Trade 2 net ₹ | Day net ₹ | Paid fees ₹ |
| --- | ---: | ---: | ---: | ---: |
| Frozen baseline, profit exits disabled | −347.43 | −344.45 | **−691.88** | 283.88 |
| Existing fixed net target ₹500 | −347.43 | +647.63 | **+300.20** | 286.30 |
| Existing trailing activation ₹500 / giveback ₹200 / initial floor ₹300 | −347.43 | +999.98 | **+652.55** | 286.45 |
| Optional minimum confirming count 3 | −347.43 | −344.45 | **−691.88** | 283.88 |
| Strike distance first, then total friction | −297.66 | −296.24 | **−593.90** | 185.90 |

Fixed profit requested/filled in this model at 09:45. Trailing requested/filled at
09:52 after the net mark fell below the preserved ₹1,008.89 floor; the modeled
realized ₹999.98 was ₹8.91 below that floor. The first losing trade was not rescued
by either profit policy. No new trades appeared in these comparisons.

## Qualification and interpretation

All five results are **post-hoc, single-day candle diagnostics**, not qualified
strategy performance or an untouched policy evaluation. The original checkpoint
said playback complete but `Qualified=false`; the independently reconstructed
session has these completion issues:

- `CALENDAR_UNVERIFIED` and `CALENDAR_PUBLICATION_UNQUALIFIED`.
- `WEIGHTS_PUBLICATION_UNQUALIFIED`; the frozen weights are a 30 September snapshot
  carried forward, not a claimed daily weight capture.
- `SELECTION_EVIDENCE_INCOMPLETE`: 495 option series unavailable and 259 sparse.

The strict Engine lane recorded `INSUFFICIENT_DATA` for these entries (missing
observed option quotes, futures-price freshness and credible risk assessments).
Trades were explicitly in the hypothetical **CANDLE_RESEARCH** lane. Its base
preset used zero declared delay, 20 spread ticks and 2 adverse ticks per side;
the Engine example's 250 ms paper delay did not replace that Replay preset.
Neither the loss nor the positive comparison results establish real quote fills.

## Recommended next evaluation

1. Compare the already implemented fixed and trailing profit policies across the
   same collection of sessions and cost/delay stresses, then use untouched dates
   for a frozen choice. The trailing result here is a reason to test, not a reason
   to optimize parameters around 14 October.
2. Evaluate a separately versioned cost/expiry/moneyness selection policy that does
   not interpret uniform assumed spread percentages as measured liquidity.
3. Assess whether a whole-lot position can support the intended net loss budget
   after friction; keep gross trigger, modeled entry risk and net stop distinct.
4. Retain failed breakouts/losers and obtain the missing qualification evidence.

## Re-run the audit

With the pinned SDK on PATH, from the repository root (use a fresh output path):

```powershell
node tests/benchmarks/replay/loss-audit.mjs data/replay/paper-replay.sqlite 2026-10-14 --details
dotnet run --project tests/benchmarks/replay/ReplayDecisionEvaluation.csproj --configuration Debug -p:RestoreLockedMode=true -- --audit-loss --database data/replay/paper-replay.sqlite --run 8a1afb7dec334f668dd75e294930624e:run-2:base --dataset data/replay/jobs/8a1afb7dec334f668dd75e294930624e/attempt-1/dataset.json --out runs/loss-audit-your-new-run
```

The Node inspector asserts per-trade gross/net and aggregate realized-ledger
reconciliation using paise, and reads only selected committed batches. Add
`--constituents` to inspect member rows. The .NET harness opens SQLite read-only,
uses the real importer/ReplayRunner/shared calculators and rejects a baseline that
does not match the checkpoint before attributing comparisons. It releases inspected
full-universe decision history each step rather than retaining the entire candidate
journal. Generated outputs preserve the original saved run and contain no provider
credentials or network/order requests.
