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
