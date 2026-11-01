# Research protocol: is there a tradable edge for a retail, one-second automated system?

Written 1 November 2026, before any multi-year data was analysed. The definitions below are frozen. Changing one after seeing results is a new
hypothesis: add it here first and count it in the multiple-testing correction. Paper only; no broker order endpoint is ever called.

## Why this exists

Two recorded sessions (21 and 22 October) cannot answer whether any rule beats costs, and the engine's own gates are built for a different
trade (see [24](24-SCALP-WHAT-IF-21-OCT.md)). An independent review (see "Sources of the plan" below) concluded that the realistic edges for a
retail system with about one snapshot per second are slow ones (regime selection, minute-to-hours horizons, execution discipline), not
short-term prediction, and that they must be tested on years of history, because paper recording alone needs 4-10 months for one hypothesis.
Base rate: SEBI reports about 93% of individual F&O traders lost money in FY22-24. The best realistic outcome is a low-frequency filtered
strategy with a net edge of a few hundred rupees per trade. "No edge found" is an acceptable and likely outcome.

## Data

| Source | Status | Use |
|---|---|---|
| Recorded sessions 21, 22 Oct 2026 | exist; re-decoded with the unused packet fields (`scripts/research/extract_features.py`) | plumbing, depth/OI exploration only |
| Live recording from the next session | now also records India VIX and Nifty 50, total buy/sell quantity, open interest and day open/previous close | H4/H5 and later |
| NIFTY BANK / NIFTY 50 / INDIA VIX minute history | needs a valid Kite token: `POST /api/research/archive/index-history` or `scripts/research/fetch-history.ps1`; depth available is **unverified** until the first fetch | H1-H3 |
| Option minute candles with open interest | `POST /api/research/archive/options` and a nightly job (Research instance, 15:50 IST, retries to 20:00); expired contracts cannot be fetched later, so this builds our own history | option-based tests, much later |
| NSE daily bhavcopy, participant-wise OI, India VIX history | not fetched yet (free) | daily regime features |

## Pre-registered hypotheses (code: `scripts/research/hypotheses.py`)

Cost model: 9.5 index points per round trip (about Rs 112 at about Rs 15 per point); stress at 2x. The structural break at 20 Nov 2024 (end of
Bank Nifty weekly expiries) splits every sample. Three hypotheses, so a two-sided |t| of at least 2.40 is required (Bonferroni, alpha 0.05).
All statistics are over **days**, not trades.

- **H1 regime forecast.** Walk-forward forecast of log(rest-of-day range, 09:30-15:30) from opening-range width, gap, prior-day range, prior-day
  absolute return (and India VIX when available). *Survives* only if out-of-sample RMSE is at least 10% lower than the **better** of
  (yesterday's range, trailing 20-day average). Then check that filtering H3 to forecast high-range days raises its net result.
- **H2 intraday momentum into the close** (one trade a day). Signal A: sign of (09:44 close minus previous close). Signal B: sign of (14:59
  close minus previous close). Trade the 15:00 to 15:29 move in that direction. *Survives* only if mean net at least 10 points per day, t at
  least 2.40, positive in both sub-periods, not more than half of the total from the best 5% of days, and still positive at 2x cost.
- **H3 breakout continuation.** A 1-minute close beyond the previous 10-minute extreme (both directions, 09:45 to 14:30 entries), enter the next
  open, exit after 20 minutes or at an 80-point disaster stop, one position at a time. *Survives* only if mean net at least 12 points per day
  (with the same t, sub-period, concentration and 2x-cost rules).

Not tested here by design: option-lead signals (contradicted by Muravyev, Pearson and Broussard 2013), intraday premium selling (Bhat, Pandey
and Rao 2024 find the premium accrues overnight), expiry-day pinning (about 12 Bank Nifty expiries a year), open-interest "build-up" labels (no
supporting evidence found).

## Checkpoints and what each decides

| Checkpoint | When | Decision |
|---|---|---|
| A. Index history | when the token is supplied; analysis takes minutes | If H1, H2 and H3 are all KILLED net of costs on at least 2 years of data, stop the directional-edge search and tell the owner. If one survives, run it forward. |
| B. Recording quality | after 20-30 recorded sessions with the new fields | H4: do total buy/sell quantity and depth imbalance reduce entry cost by at least 0.3 option point, or predict 30-60 s futures drift by at least 3 points in the top decile? H5: does bank breadth add out-of-sample lift over the surviving rule? |
| C. Forward paper test | 60-80 sessions of a frozen survivor | Success: net expectancy above the round-trip cost with t at least 2 and the 95% interval's lower bound above zero. Failure: the upper bound below zero, or all profit from a handful of days. |

## Results

### Checkpoint A, 1 November 2026 (run once, frozen code `7604e41`, definitions unchanged)

History obtained: 1,190 full sessions, 3 Jan 2022 to 30 Oct 2026, minute candles for NIFTY BANK, NIFTY 50 and INDIA VIX (Kite returned all that
was requested, so earlier history is probably available). It matches our own 21 Oct recording in shape (down day; futures traded 80-115 points
above the index). Net of 9.5 points per round trip:

| Hypothesis | Days | Net mean per day | t | Pre / post Nov 2024 | At 2x cost | Verdict |
|---|---|---|---|---|---|---|
| H2a first-30-minute signal, last 30 minutes | 1,189 | -13.2 pts | -4.02 | -10.6 / -17.0 | -22.7 | KILLED |
| H2b prev-close-to-14:59 signal, last 30 minutes | 1,189 | -9.3 pts | -2.82 | -7.4 / -12.0 | -18.8 | KILLED |
| H3 10-minute breakout, 20-minute hold | 1,190 | -126 pts (11.2 trades a day) | -18.5 | -132 / -117 | -233 | KILLED |
| H1 regime forecast (RMSE vs better baseline) | 793 forecast days | 4.9% improvement (needs 10%) | - | - | - | KILLED |

Before costs the effects are essentially zero, which is why costs decide: H2a -3.7 pts per day (t -1.1), H2b +0.2 (t 0.1), H3 -1.8 pts per
trade (t -2.9 on days). No day-concentration question arises because nothing is positive. The regime forecast does carry some information
(model log-RMSE 0.424 against 0.446 for the trailing 20-day average) but not enough under the frozen threshold.

**Stop rule triggered:** H1-H3 are all killed on more than two years of data. By the protocol the directional-edge search on index price
patterns stops here. Remaining open items are the recording-based checkpoints (B: execution filters and breadth) and non-directional ideas,
neither tested yet.

## Trend-following family (pre-registered 1 November 2026, before any daily-history result was seen)

Motivation: the owner's principle is "identify early, cut losers early, hold winners". Early identification is not supported by evidence at short
horizons (checkpoint A). The version with published support is trend following: do not predict, react to a move that has already proven itself,
accept many small losses and trail the winners (time-series momentum, Moskowitz, Ooi and Pedersen 2012; Hurst, Ooi and Pedersen on a century of
trend following; cited from memory, not re-verified this session, and weaker after 2009). Few trades mean costs matter far less than intraday.

Data: NIFTY BANK daily OHLC, 3 Jan 2005 to 30 Oct 2026 (5,403 bars), from Kite (`index-day`). Code: `scripts/research/trend.py`.

Execution model (identical for all three rules): a desired position `d_t` (+1 long, -1 short, 0 flat) is decided at the close of day `t` and
executed at the open of day `t+1`. Day `t+1` return = `d_(t-1)` x (open_(t+1)/close_t - 1) + `d_t` x (close_(t+1)/open_(t+1) - 1). Cost 0.05% of
notional per side on every position change (futures-like: brokerage, exchange charges, tax and slippage); 0.10% as the stress case. One unit of
notional, no leverage or volatility scaling.

- **T1 channel breakout with a volatility stop (long/short).** Flat and close above the previous 55 days' highest high: long. Flat and close below the
  previous 55 days' lowest low: short. Exit when the close crosses the chandelier stop: the highest close since entry minus 3 x ATR(20) for longs
  (lowest close plus 3 x ATR(20) for shorts). ATR(20) is the 20-day mean true range.
- **T2 12-month time-series momentum (long/short).** At each month-end set the position to the sign of the previous 252-day return; hold until the next
  month-end.
- **T3 trend filter (long/flat).** Long while the 50-day average close is above the 200-day average close, otherwise flat; re-evaluated daily.

Family: with H1-H3 that is six pre-registered tests, so |t| >= 2.64 (Bonferroni 0.05/6, two-sided). Alpha is the intercept of daily strategy return on
index return for T1 and T2 (heteroskedasticity and autocorrelation robust, Bartlett lag 10); for T3 it is the mean daily excess over exposure-matched
buy-and-hold (strategy return minus average exposure x index return). A rule is classed **EDGE** only if all hold: alpha t >= 2.64; alpha positive in
each of three equal date-thirds; still positive at 2x cost; no single calendar year supplies more than 40% of total log profit; and net Sharpe at
least that of buy-and-hold. It is classed **RISK-MANAGED** (not an edge) if it fails the Sharpe test but keeps at least 60% of buy-and-hold CAGR with
at most 60% of its maximum drawdown. Otherwise **KILLED**. Also reported (not criteria): trade count, win rate, average win over average loss, profit
factor, to check the "lose small, win big" payoff shape directly.

Caveat fixed in advance: Bank Nifty rose about 15-fold over the sample, so any long-biased rule can look good from drift alone; that is why alpha is
beta-adjusted and compared with buy-and-hold. The index is a proxy for a futures position (no basis, no roll cost); an options implementation would be
a separate test using the nightly option archive.

### Checkpoint A2, 1 November 2026: trend-following family (run once, code `39b3007`, rules as pre-registered)

5,399 daily bars, 3 Jan 2005 to 30 Oct 2026. Cost 0.05% per side. Buy-and-hold: 13.6% a year, Sharpe 0.60, maximum drawdown 68.8%.

| Rule | Trades | Win rate | Avg win / avg loss | Profit factor | Net CAGR | Sharpe | Max drawdown | Alpha (per year) | t | Class |
|---|---|---|---|---|---|---|---|---|---|---|
| T1 55-day breakout, 3 ATR stop (L/S) | 102 | 32% | 1.85 | 0.88 | -3.6% | -0.09 | 61.8% | -1.1% | -0.26 | KILLED |
| T2 12-month momentum, monthly (L/S) | 22 | 32% | 1.35 | 0.63 | -7.7% | -0.16 | 91.3% | -6.3% | -1.04 | KILLED |
| T3 50/200 trend filter (long/flat) | 16 | 56% | 3.20 | 4.12 | 6.6% | 0.43 | 44.9% | -3.2% (vs exposure-matched B&H) | -0.97 | KILLED |

The "lose small, win big" payoff shape is real for T1 (32% winners, winners 1.85 times the size of losers) but not large enough: 0.324 x 1.85 is below
the 0.676 loss frequency, so expectancy is slightly negative. T3 keeps roughly half of buy-and-hold's return for about two thirds of its drawdown, which
fails both the Sharpe test and the risk-managed test (needs at least 60% of the return with at most 60% of the drawdown). T2's alpha is worst in the
last third of the sample. Caveats: the index stands in for a futures position (no basis or roll), and one instrument is a thin base for trend following
(the published evidence uses many markets).

With checkpoint A, six of six pre-registered tests are killed. No variant of these rules has been tried; trying one now is a new hypothesis and
must be added above and counted in the correction (|t| required rises with every test).

### Correction to A2, 1 November 2026 (found by an independent audit, re-derived here)

The A2 comparison gave the strategy no interest while flat and measured Sharpe without subtracting a risk-free rate, which understates the long/flat
rule. With cash credited at 6.5% a year while flat, T3 earns 8.8% a year (65% of buy-and-hold's 13.6%) with a 41.0% maximum drawdown (60% of
buy-and-hold's 68.8%), which meets the pre-registered RISK-MANAGED test (at least 60% of the return, at most 60% of the drawdown) narrowly. Excess
Sharpe is 0.20 against 0.37 for buy-and-hold, and alpha is still negative, so T3 is a drawdown reducer, not an edge. **T3 is reclassified from
KILLED to RISK-MANAGED (borderline).** T1 and T2 remain killed (T1 loses even at zero cost). Also recorded: 21 years of data gives the trend tests
power only for an alpha Sharpe of about 0.57, so "killed" means "no large edge", not "no edge"; every point estimate was negative. Intraday costs in
the H2/H3 tests were, if anything, understated (the April 2026 tax change raises futures costs), so the gross-zero conclusion stands.

Verified from our data in the same review: India VIX exceeded the next 21 trading days' realised Nifty volatility 81.5% of the time (mean 18.5 against
15.4; 15.0 against 12.8 since 2022), and 76% of overnight Bank Nifty gaps since 2022 exceed 66 points (median 137), so a Rs 1,000 stop on a lot of 30
does not cap an overnight loss.

## Intraday long-option search (pre-registered 1 November 2026, before any result)

The owner's actual use case: the system finds an entry itself, buys one lot of a call or put, trails a stop, sells, intraday, long options only. We have
no historical option prices, so options are modelled from the index and India VIX minute history (1,190 sessions, Jan 2022 to Oct 2026). Calibration
on the two recorded days: Bank Nifty near-the-money option implied volatility divided by India VIX has median 1.00 on both days (10th to 90th percentile
0.79 to 1.20). The search below is ONE test (it counts as the seventh pre-registered test, so |t| >= 2.69, Bonferroni 0.05/7).
Code: `scripts/research/option_sim.py`, `scripts/research/signal_search.py`.

**Vehicle (frozen).** One lot (30 units). Strike: nearest 100 to the index at the signal minute close; call on an up signal, put on a down signal.
Price: Black-76, zero rate, forward = index, volatility = 1.0 x India VIX / 100 at the same minute, time to expiry 14 calendar days at entry and falling
with real elapsed time (sensitivities at 7 and 30 days, and volatility scale 0.85 and 1.15, are reported, not used for selection). Entry fills at the
next minute's open: ask = model + half spread, where half spread = 0.135% of the model price (recorded median spread was 0.27%), plus one tick (0.05)
of slippage; exit symmetric on the bid. Fees: the paper fee schedule in `replay.example.json` (brokerage Rs 20 an order, exchange 0.03553%, SEBI 0.0001%,
GST 18%, stamp 0.003% on the buy, securities transaction tax 0.15% on the sell premium, i.e. the April 2026 rate for every year, which is conservative for
earlier years). Stress case: spread and slippage doubled.

**Exit (frozen).** Trailing stop on the modelled bid: exit when the bid falls X below the running maximum since entry (the initial stop is entry less X),
checked at each minute close and filled at the next minute's open (so gaps through the stop are paid). Forced exit at 15:15. No overnight holding. At most
3 trades a day, one at a time, entries 09:30 to 14:30. X in {8%, 12%, 16%} of the premium (8% of a Rs 420 premium is about the owner's Rs 1,000 limit).

**Signals (frozen search space, 18 signals x 3 stops x 2 filters = 108 combinations).** All use only data up to the signal minute.
- Momentum M(N, theta, side): index return over the last N minutes (N in {15, 30, 60}) at least theta (1.5 or 2.0) times the trailing 120-minute one-minute
  volatility scaled by sqrt(N); direction with the move (continuation) or against it (reversal): 12 signals.
- Break B(N, side): the minute close breaks the prior N-minute high or low (N in {30, 60}) or yesterday's high or low; with the break or against it: 6 signals.
- Filter: none, or India VIX above its trailing 60-session median.

**Procedure (frozen).** Walk-forward: train on the previous 24 months, trade the next 6 months with the single combination that had the highest mean net
rupees per trade in training (at least 100 training trades), repeat. The first training window is Jan 2022 to Dec 2023, so the out-of-sample blocks are
2024 H1 through 2026 H1/H2. Only the chosen combination's out-of-sample trades are evaluated; nothing else is tuned.

**Success (all required).** Out-of-sample mean net rupees per trade above zero with day-clustered |t| >= 2.69; at least 4 of the 5 blocks positive;
still positive with doubled spread and slippage; no single block supplies more than 50% of the profit; and the result is not reversed by the
sensitivities (volatility scale 0.85/1.15, expiry 7/30 days). Otherwise **KILLED**. Reported regardless: trades, win rate, average win over average loss,
profit factor, average holding minutes, worst day and maximum drawdown in rupees per lot, and the average over all 108 combinations out of sample
(to show whether the selection step added anything).

**Amendment, made before any real-data run (commit following `6865732`).** The vehicle said time to expiry falls with real elapsed time. A plumbing test on
pure random-walk data showed this flatters a long option: gross result +Rs 61 per trade where a fairly priced option gives about zero, because variance
accrues only in the 375 trading minutes of a session while calendar-minute decay assumes it accrues all day. The convention is now trading-time decay:
remaining life = (calendar days x 252/365 - elapsed minutes/375) / 252. After the change pure noise nets about -Rs 100 per trade, matching the expected
costs of about Rs 110. The volatility calibration (1.00 x India VIX) used calendar time on six-day options and moves by about 3% under this convention,
inside the stated 0.85/1.15 sensitivity. Nothing else in the frozen design changed.

Limits stated in advance: modelled option prices are not market prices (skew, order-book depth and fast-market spreads are not modelled), so a pass here
would only justify validating on real option prices (the nightly archive or a purchased history); a failure is informative because the model is close to
the recorded prices on average and is not obviously biased against the buyer.

### Checkpoint A3, 1 November 2026: intraday long-option search (run once, code `10e99c6`, frozen design plus the pre-run decay amendment)

108 combinations searched walk-forward on 1,190 sessions (train 24 months, trade the next 6; 6 blocks because the last, Jul to Oct 2026, is partial).
Out-of-sample: 536 trades over 699 days.

| Out-of-sample result | Value |
|---|---|
| Mean net per trade | -Rs 288 (t on days: -2.64, i.e. significantly worse than zero) |
| Total net | -Rs 154,332 per lot |
| Win rate / winners over losers / profit factor | 32% / 1.54 / 0.74 |
| Average hold / average premium | 69 minutes / Rs 613 |
| Worst day / maximum drawdown per lot | -Rs 9,240 / -Rs 201,249 |
| Doubled spread and slippage | -Rs 341 per trade |
| Sensitivities (vol 0.85 / 1.15, expiry 7 / 30 days) | -262 / -371 / -265 / -397 per trade |
| Blocks positive | 2 of 6 (small: +19,853 and +2,230) |
| Average over all 108 combinations, per block | -Rs 114 to -Rs 371 per trade |

**KILLED** on every criterion. The selection step added nothing: training-window winners (training means up to +Rs 269 a trade) lost out of sample, and
the chosen combination was no better than the average combination. The payoff shape is the one the owner wanted (about a third winners, winners
1.5 times the losers) and still loses after spread, slippage, fees and time decay. Limits: modelled prices, not market prices; the volatility scale
was calibrated on two days.

Seven of seven pre-registered tests are now killed or non-edge (H1, H2a, H2b, H3, T1, T2, A3; T3 is a borderline drawdown reducer).

### Diagnostics after A3 (1 November 2026; not tests, no rule is selected from them)

Post-mortem of the 536 out-of-sample trades (`scripts/research/postmortem.py`). Entries: in the signal direction the index moved +0.2, +0.1, +1.2,
+3.2 and +1.6 points after 1, 5, 15, 30 and 60 minutes (all inside the 95% band) and moved our way 46-49% of the time, after having already moved
+113 points in the 30 minutes before the signal: the entries carried no information. Exits: 84% of trades were ahead by more than the fees at some
point and 61% of those ended as losses, which is what a random path does. The same entries with ten other exits (trailing 8-30%, take-profit
Rs 500/1,000/2,000, time exits of 5/15/60 minutes, hold to 15:15) all lost Rs 103-342 per trade on average while the win rate ranged from 31% to 74%:
exits change how often you win, not whether you make money.

## Bank breadth lead (pre-registered 1 November 2026, before the constituent history was fetched)

The engine uses the 14 banks only as a confirmation filter; whether breadth *predicts* Bank Nifty was never tested. Data: minute candles for the 14
current constituents and NIFTY BANK, Jan 2022 to Oct 2026, from Kite. Weights: the current published weights (HDFC Bank 18.61 ... Union Bank 2.86),
not point-in-time, and today's membership (a survivorship approximation, stated in advance). Code: `scripts/research/breadth.py`.

- Signal at minute i (09:30-15:00): divergence D = weighted 5-minute return of the 14 banks minus the index's 5-minute return (percent).
- Outcome: the index move over the next H minutes, H in {5, 15}, in points.
- **Test 1 (predictive):** slope of the outcome on D, sampled every H minutes (non-overlapping), t clustered by day; required t >= 2.69.
- **Test 2 (economic):** with decile thresholds of D fixed on 2022 only, the 2023-2026 difference between the mean next-15-minute move after the top
  decile and after the bottom decile must be at least 19 index points (about the round-trip cost of an at-the-money option at delta 0.5).
- SURVIVES only if both hold (it would then still need an option-level test); otherwise KILLED. This is the eighth pre-registered test.

### Result: bank breadth lead (run once, code `e919463`)

1,190 sessions, 14 banks. Five minutes ahead: slope +37.6 index points per 1% divergence, day-clustered t +3.07 (79,723 samples), so the banks do
lead the index measurably, but a typical (1 standard deviation) divergence predicts only +0.9 index points. Fifteen minutes ahead: t +0.42, nothing.
Economic test (2022 deciles applied to 2023-2026): top decile +0.9 points, bottom decile -1.5 points, difference +2.3 (+/- 8.0) against the 19 points
needed. **KILLED.** The effect is real but about one twentieth of the cost of trading it, and gone within 15 minutes. Part of it may be mechanical
(current weights, not point-in-time ones). Eight of eight pre-registered tests are now killed or non-edge.

## Multi-timeframe candle model over the index and 14 banks (pre-registered 1 November 2026, before any result)

The owner's idea: judge direction from 5-, 10- and 15-minute candles of Bank Nifty and all 14 banks together, rather than second by second.
Code: `scripts/research/candles.py`. This is the ninth pre-registered test: |t| >= 2.77 (Bonferroni 0.05/9).

- Decision points: every 15 minutes from 09:45 to 14:45 (21 a day). Outcome: the index move over the next 15 minutes, in points (non-overlapping).
- Features (all from data up to the decision minute), for each candle length s in {5, 10, 15} minutes: index return; where the index closed inside
  its last s-minute candle (0 = low, 1 = high); weighted bank return; bank-minus-index divergence; share of bank weight that rose; spread (weighted
  standard deviation) of bank returns; HDFC Bank plus ICICI Bank return; bank volume in the last s minutes relative to the session's average so far.
  Plus: the index move since the open, time of day, India VIX level and its 15-minute change. 28 features, each standardised on training data only.
- Model: ridge regression (penalty 10 on standardised features) predicting the 15-minute move in points. Walk-forward: train on all data before
  each 6-month block (first block 2024 H1), predict the block, repeat.
- Decision: go long (call) when the prediction is in the top decile of the *training* predictions, long put in the bottom decile; nothing otherwise.
- **SURVIVES only if** out of sample the mean index move in the trade direction is at least 19 points (about the round-trip option cost) with
  day-clustered t >= 2.77, it is positive in at least 4 of the 6 blocks, and positive in both 2024 and 2025-26. Otherwise **KILLED**. Reported:
  direction hit rate, mean move per call, calls per day, and the same for all decision points (the model's correlation with the outcome).

### Result: multi-timeframe candle model (run once, code `faa8a0d`)

24,989 decision points over 1,190 sessions, 28 features from 5-, 10- and 15-minute candles of the index and all 14 banks, plus India VIX. Out of sample
(2024 to Oct 2026, 14,679 points): correlation between prediction and the next 15-minute move **0.003**. Confident calls (top and bottom training
deciles): 2,407 on 653 days, direction right 51.9% of the time, mean move in the call direction -1.6 points (day-clustered t +1.0, both
indistinguishable from zero), positive in 3 of 6 blocks (+2.8, 0.0, -4.4, +3.4, -3.4, -8.6 points), against the 19 points needed. **KILLED.**
Nine of nine pre-registered tests are now killed or non-edge.

## Stop rules

Stop and report if: A kills H1-H3 on 2+ years; or any survivor depends on a handful of trend days or one structural period; or after 60 paper
sessions the upper bound of the 95% interval on net expectancy is below zero.

## Safeguards against fooling ourselves

Count days, not trades. Freeze code before looking. Record every variant tried. Judge gross and net separately. No tuning on 21 Oct (one clean
trend day). Do not use fills better than the recorded bid/ask. Keep the two-day scans in [24](24-SCALP-WHAT-IF-21-OCT.md) as exploratory only
(4 of 126 combinations positive is what chance produces).

## What the owner supplies

1. A fresh Kite access token for the history fetch (the file `src/Trading.Api/kite.credentials.Local.json`, as in [12](12-DAILY-KITE-SETUP.md))
   and confirmation that the Kite Historical API subscription is active.
2. The decisions listed in the hand-off message: the daily loss cap, whether Rs 1,000 is net or gross, single leg or defined-risk spread.

## Sources of the plan

Independent Opus reviews run on 1 Nov 2026 (findings summarised in the session hand-off; sources include SEBI studies, Gao et al. JFE 2018,
Baltussen et al. JFE 2021, Muravyev et al. JFE 2013, Bhat et al. J. Futures Markets 2024, and the Kite WebSocket and historical documentation).
Items marked unverified there remain unverified here until the first fetch.
