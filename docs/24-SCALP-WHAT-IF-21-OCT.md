# Scalp-style what-if on the 21 October 2026 recording

Research note, 1 November 2026. Labelled **counterfactual**: recorded quotes only, no real fills, one day, three signals. It establishes
nothing about profitability. Source: run `live-20261021-276c8e4b...` (session policy v2 applied to the recorded events, auto force-armed).

## 1. Engine replays: no scalp-style configuration can trade

Variants run through `POST /api/live/replays` (original and changed engine JSON): 5-minute opening range, entries to 14:30, 10 attempts,
risk 100/200/200, quick exits, and with the risk gates lifted. **Every valid variant produced 0 attempts.** The engine rejected three
variants itself (`ConfirmationWeightPercent` must stay above 50, `SetupLifetime` at most one bar).

Setups do form on this day: bullish 09:32, bearish 10:01 and 10:04 (09:29 with a 5-minute range). Every candidate then fails the
economic gates (sampled every 2 s):

| Gate | Limit | Observed on candidates |
|---|---|---|
| `NET_RISK_BUDGET` (loss to the futures structural stop, whole lot) | 400 | 1,891 to 5,811, median 3,533 |
| `SCENARIO_NET_OPPORTUNITY` (worst of IV x0.8/1.0/1.2 at +1R after 20 min) | 500 | -2,605 to -214 (always negative) |
| `FRICTION_TO_STOP` | 150 | 150 to 682 |
| `MAXIMUM_SPREAD` | 1% | 1.0% to 2.0% on some |

`MinimumScenarioNetProfit` must be above zero (configuration validation) and the modelled worst case is negative, so no allowed setting
passes. The structural stop is about 100-200 index points; one lot is 30 units, so that is thousands of rupees against a 400 budget.
The risk model is built for a wider-stop, longer-hold trade than a scalp.

## 2. Signal-only simulation (outside the engine gates)

Method: take the strategy's own setups; buy the at-the-money CE (bullish) or PE (bearish) at the recorded ask plus one tick 250 ms after the
setup, sell at the bid minus one tick 250 ms after the exit trigger; fees from the paper fee schedule (about Rs 75 per round trip);
one lot (30 units).

Market that day: a 15-minute range 55,685-55,889, a failed upside break at 09:31-09:34, then a trend down about 950 points. The recorded
spread on at-the-money options is tight (median about 1.2 points, about Rs 40 round trip plus fees), so the cost is not the spread.

| Exit rule (one lot) | 09:32 call (false break) | 10:01 put | 10:04 put | Total net |
|---|---|---|---|---|
| stop 200 / target 200 / 5 min | -306 | -341 | -371 | -1,018 |
| stop 100 / target 150 / 3 min | -306 | -194 | -208 | -708 |
| stop 500 / target 500 | -584 | -649 | +493 | -740 |
| stop 1,000 / target 1,000 | -998 | -1,085 | +937 | -1,146 |
| stop 1,000 / trail 500, giveback 300 | +131 | -1,085 | +466 | -487 |
| stop 2,000 / trail 1,000, giveback 500 | -2,098 | +639 | +340 | -1,119 |
| no stop, 20-minute time exit | -2,533 | +2,672 | +2,154 | +2,293 |

- A Rs 200 stop is about 13 futures points (30 units x delta about 0.5). Futures move 15-20 points within seconds, so tight stops are
  hit by noise: all three scalp-sized trades stopped out within 4-27 seconds.
- The put signals had a best favourable excursion of about Rs 4,000 on a trending day; the call signal was a false break that lost
  under every rule. Results depend almost entirely on how much room the stop allows, and three signals on one day cannot separate edge
  from luck.

## 3. Making the replay loop usable: research risk and diagnostics

Section 1 means the replay mechanism could never improve anything: under the live gates every rerun ends with zero trades. Two replay-only
additions (never used by the live host, default off):

- **Research risk** (`researchRisk` on `POST /api/live/replays`, labelled `WHAT_IF_RESEARCH_RISK_...NOT_LIVE_POLICY`): the gates receive risk sized to
  what the paper broker actually enforces (gross cash stop plus round-trip fees, or the immediate-liquidation friction if larger) and the
  +1R / 20-minute profit at *unchanged* volatility instead of the worst of three volatility scenarios. Everything else (fills, fees, other
  gates, exits) is unchanged. Implemented in the session's `Model(...)`; no Engine or Risk edit.
- **Diagnostics** (always returned, shown in the Live page under "Why it did or did not trade"): setups that formed, rejection reasons while a
  setup was live, observed-versus-limit statistics per gate, and the closest candidate's blockers.

First results on 21 Oct (v2 policy, auto force-armed; the diagnostics also replace the guessing in sections 1-2):

| Variant (research risk on) | Trades | Net |
|---|---|---|
| recorded settings (15-minute range, to 11:30, 3 attempts, Rs 200 cash stop) | 2 (10:01 put, 10:04 put) | -711.75 (both stopped out within 5-26 s) |
| 5-minute range, to 14:30, 10 attempts | 2 | -711.75 |
| same, Rs 400 cash stop (net-risk limit 700) | 2 | -228.95 (-549.68 stop, +320.73 trailing profit) |
| same, Rs 100 cash stop, quick exits | 1 | -226.31 |

The two engine trades match the independent signal simulation in section 2 (put -340.76 against -341, put -370.99 against -371).

**Constituent coverage was checked and is not the problem.** At every setup time (09:32, 10:01, 10:04, and later) all 14 banks and the futures
had every 1-minute bar since 09:15, none with zero volume, and coverage flags intact. A first diagnostic reported 86% of bank weight
"missing" for the 09:32 setup; that was a transient (the earliest sample after the signal bar closed, before every bank's bar had been
finalized) and the diagnostic now reports the state at the setup's last evaluation instead. By that measure the 09:32 bullish setup was
blocked only by `SCENARIO_NET_OPPORTUNITY` (modelled 449.91 against the 500 hurdle); the 10:01 and 10:04 setups traded.

## 4. Are the data or the counts wrong? (audit of the raw ticks)

- Our sampled futures bars match the exchange's own day high and low to within 1.2 points on both days (exactly on 21 Oct), at about one
  snapshot per second. The opening range, bar volumes and bank bars are consumed correctly.
- The market does move: swings of at least 50 points: 105 on 21 Oct and 126 on 22 Oct; at least 80 points: 29 and 45. The strategy found only
  2-3 setups because a setup is a *fresh crossing* of the opening range (a bar closes outside the range after one closed inside) that lives
  60 seconds; in a one-way trend it is disarmed after the first crossing.
- Scanning simple continuation rules over both days (a 1-minute close beyond the previous 5/10/15-minute extreme, with and without a VWAP
  filter, stops 10-40 points, targets 15-60 points, one lot at about Rs 15 per futures point, Rs 112 round-trip cost, 1 point slippage each
  side; break-even about 9.5 points): average **gross** result per trade was between -Rs 78 and +Rs 133 for every family, so costs decide the
  outcome; faded versions are no better; 4 of 126 parameter combinations were net positive over the two days (best +1,313 over 37 trades,
  with 22 Oct negative), which is what chance alone would produce when picking the best of 126 on two days.

## 5. What would be needed to test scalping properly
## 4. What would be needed to test scalping properly

1. More days of clean recordings, and a signal definition that is genuinely scalp-timed (the current one is a 1-minute-bar opening-range
   breakout with a 1-minute setup lifetime).
2. A decision on the risk construct: the engine sizes risk to the structural stop; a scalp would size it to a cash or points stop.
   That is a change to the Risk/Engine gates (separate owner), not to configuration.
3. A reproducible research tool for the simulation above (this note's numbers came from a throwaway script).
