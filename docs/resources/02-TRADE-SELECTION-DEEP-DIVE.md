# Trade Selection — Research Summary

Compiled: 9 October 2026 | Related discussions: 28 September, 1 October and 6 October

## Core problem

Fast computation reduces the time required to evaluate opportunities. It does not establish a market edge. The engine must separately decide whether there is a setup, whether an option expression is feasible, whether it survives costs/risk, and whether holding it remains justified.

Jeevan wants 20–35 eligible candidates examined, including useful liquid strikes away from ATM. These are alternatives for one position per index, not 35 independent bets. Candidate coverage must be configurable and recorded.

## Decision sequence

1. Validate timestamps, required fields, contract validity and bank coverage.
2. Describe context and the explicit setup; allow mixed/no setup.
3. Generate point-in-time eligible option alternatives.
4. Check whole lots, cash, spread/depth, fees and requested loss feasibility.
5. Compare favourable/flat/adverse scenarios with stated assumptions.
6. Rank surviving alternatives; preserve every rejected reason.
7. Record the decision and later monitor thesis/risk under the same rules.

Gates precede scores. A strong trend score cannot override stale data or unaffordable lot risk. A score is not a probability unless independently calibrated to a defined outcome/horizon.

## Criteria worth testing

| Supports investigation | Weak or misleading basis |
| --- | --- |
| Completed opening-range break, futures VWAP alignment and synchronized bank confirmation | An unfinished candle treated as complete |
| Defined invalidation, feasible stop plan and cost-adjusted opportunity | Selecting the highest theoretical expiry payoff |
| Fresh bid/ask and enough depth for intended quantity | Cheap LTP mistaken for liquidity or easy profit |
| Agreement across independent inputs with measured coverage | Counting several versions of the same momentum signal as independent evidence |
| Unseen results after costs, including no-trade periods | Picking historical winning strikes after seeing outcomes |
| Performance by regime and sensitivity to delay/spread | Win rate alone or profitable illustrative examples |

These are research design criteria, not a current recommendation to enter a trade.

## First policy hypothesis

See [01-SCOPE-AND-DECISIONS.md](../01-SCOPE-AND-DECISIONS.md) for the exact experimental parameters. Opening range, confirmation weight, entry window and trailing rules are unvalidated settings. Track underlying structural risk separately from actual option cash risk. Return WATCH/SKIP when the user's constraints cannot be satisfied.

The dashboard comes first and exposes these facts for manual decisions. Engine-generated selection labels arrive after replay research. Useful first-screen language includes “banks mixed,” “spread consumes planned target,” and “required data missing,” rather than an unsupported probability.

## Experiments

Compare baseline versus bank confirmation, ATM-only versus predefined wider liquid universe, fixed versus conditional holding, and base versus adverse costs/delays. Log all trials and freeze the final policy before untouched evaluation. Test whether the selected option adds value over simpler alternatives with identical signal times and costs.
