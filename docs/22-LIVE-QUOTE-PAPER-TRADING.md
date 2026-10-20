# Live quote paper trading — agreed 20 October 2026

Target: 21 October 2026, Asia/Kolkata. Implementation and measured live readiness
are separate. This agreement supersedes the raw-event file storage choice and the
deferral of experimental option modeling in document 08 for this increment.

Status: experimental implementation in the live API/UI and shared quote-session
path. Automated software checks use controlled data. An authenticated market
session, actual machine recording throughput and the model's empirical accuracy
have not been established by those checks.

## Accepted policy

- BANKNIFTY only; existing futures ORB/VWAP and weighted-bank strategy. Complete
  09:15 opening/session evidence is mandatory for auto. No retrospective entries.
- SQLite retains every received WebSocket message, including duplicates, full raw
  payloads, timestamps, connection/subscription controls and decode failures. Frozen
  definitions, weights, fees, configuration and code provenance identify a run.
- Connect before open; record 09:15–15:30 independently of browser/auto. Retain initial
  sessions without deletion. Bounded batching targets <=250 ms while healthy;
  crashes may lose an uncommitted tail. Mark recovery incomplete, never silently repair.
- Index, selected non-expiry-day future, required banks, and 30 same-expiry options:
  15 actual listed strikes centered on the first fresh in-session futures price,
  CE/PE pairs; freeze the ladder. No silent recentering or unrecorded replay coverage.
- Paper capital ₹500,000; one lot, one shared pending/open slot; three automatic
  attempts and one separate manual diagnostic. Gross stop ₹200; modeled net entry
  risk ceiling ₹400; modeled favorable net hurdle ₹500; session pause loss ₹1,200.
  Entries 09:30–11:30, session exit request 15:15. Fixed-profit exits disabled.
- Fills require a new fresh quote received after intent +250 ms, whole-position
  observed depth, adverse tick rounding, buy ask+one tick / sell bid-one tick.
  Entry timeout five seconds; unexecutable exits remain pending. Reassess cash/risk
  before entry fill. Crossing bid/ask already charges spread.
- Explicit experimental Black-76 model, matching future expiry, observed midpoint IV
  calibration, zero discount rate, IV factors .8/1/1.2. Modeled prices/IV never become
  observed quote/Greek evidence. Failed calibration or missing inputs blocks entry.
  Risk and opportunity are estimates, not loss bounds or probability forecasts.
- Verified official month-end bank weights may be carried forward for <=45 calendar
  days; disclose convention, verify current membership, never renormalize missing
  weight. Use a versioned effective-date sourced paper fee reference.
- Block entries immediately on feed/recording failure. After five unhealthy seconds
  request exit; wait for a qualifying later quote. Resume auto explicitly after
  reconnect/restart. Durable/idempotent state recovery precedes entry eligibility.
- Demonstrate manual full lifecycle before auto. Manual bypasses setup only.
- Original and changed-policy replay use the same ordered quote/timer/execution
  path and supplied clock, separate results, immutable source recording. Synthetic
  rehearsal has its own mode/run; no live clock override.

## Ownership and verification

Preserve current engine source and dedicated tests. Integrate through its existing
public contracts; shared financial calculations remain in Risk/Analytics. Live host,
recording/persistence and portfolio/replay additions are owned by this increment.

Public verification seams agreed with the user: feed → durable events; intent →
delayed fill → position → exit → recovered journal; original events → deterministic
replay. Cover future isolation/completed bars, reconciliation, depth/delay, stale
feed, recorder pressure, duplicate controls, restart, pending exits and no broker
order requests. Browser E2E exercises the controlled quote lifecycle separately
from authenticated live evidence. Zero qualifying strategy trades is valid.

## Session runbook

1. Use the pinned SDK/tooling and start the API and React UI as in the root README.
   Renew the **21 October token** through the existing
   [daily Kite guide](12-DAILY-KITE-SETUP.md). Credentials stay in the ignored backend
   file; there is no browser token form.
   Before the market, **Run synthetic quote rehearsal** exercises actual backend
   packet decoding → SQLite → bars/risk/engine → delayed fill → exit → browser
   journal under a fixed virtual clock. Its separate run is labelled SYNTHETIC TEST;
   contracts, weights and fees are fictional. It never connects or changes live state.
2. Before 09:15 IST, open `http://localhost:5173/#live` and choose **Start / reconnect
   recording**. The backend retrieves/archives the current master, official current
   bank membership and published month-end weight archive. Inspect error/reference
   reasons rather than treating an HTTP health response as a feed check.
3. Optional unattended startup is `LivePaper:ConnectOnStartup=true` in the existing
   backend-local override (or `LivePaper__ConnectOnStartup=true`). Defaults remain
   false. The process must stay running; closing the browser does not stop recording.
   Runs prepared before 08:30 refresh references at 08:30 before collecting the open.
4. At the first fresh in-session futures observation, verify the frozen 30-option
   universe and server-derived freshness. Index packets have no traded-volume VWAP.
   Futures/bank bars are built from the **received sampled quote stream** and
   differenced cumulative volume; candle-derived VWAP is approximate. Coverage means
   our observed session prefix, not every exchange transaction. Gaps, late/out-of-order
   observations and missing minutes must remain visible, not forward-filled.
5. During 09:15–09:30 collect the opening range. After 09:30, inspect completed bars,
   weighted evidence, fees and candidate rejection reasons. A late start, missing
   required bank, unverified references, failed IV calibration, excessive modeled
   risk or insufficient favorable scenario can leave **both manual and auto blocked**.
6. Select a frozen-universe option and submit **MANUAL_TEST** when its independent
   gates pass. Confirm intent → later fill → position → requested exit → later exit
   fill → fee/net journal. It consumes the separate one-manual-attempt allowance even
   if canceled/unfilled. Auto arming requires a completed manual lifecycle in this run.
7. Explicitly enable paper-auto. Strategy attempts are limited to three; no minimum
   trade count. At 11:30 pending entries cancel and new entries stop. Position
   monitoring continues; 15:15 requests session exit. Keep the process and data feed
   available through exit and recording. Missing depth/data never becomes a fill.
8. Inspect received versus committed sequences, oldest pending lag, recording issues,
   pending exits, fees/net P&L and stop overrun. The 250 ms buffer is a target, not
   measured durability latency. Recording failure blocks entry but must not disable
   protective monitoring. The one-second paper checkpoint cadence is supplemented
   by immediately queued commits for every intent/fill/exit-request/control mutation.
   The dedicated paper writer performs storage I/O outside ingestion/protection;
   commands and the account display acknowledge committed state only. A pending
   acknowledgement is explicit. The filling quote's real receipt time is captured
   before host locking and retained separately from processing availability.
9. Afterward, **Load recordings**, select the source, and **Replay recorded quotes**.
   Empty configuration means frozen original settings; a complete changed Engine JSON
   creates a comparison result. Current v1 supports one Bank Nifty strategy, one lot
   and disabled profit exits. Execution delay can be stressed (e.g. one second), but
   adverse slippage remains one actual contract tick. The source universe is fixed;
   unrecorded contracts/prices/depth are never manufactured.

## Storage and recovery

Default API-relative `LivePaper:RootDirectory=../../data/live` contains:

- `market-YYYY-MM-DD.sqlite` (one file per IST trading date): immutable run manifests and
  ordered original market/control BLOBs, master CSV, membership CSV and monthly weight
  ZIP. Duplicate receptions are retained; identical observations are not reapplied as
  newly executable quotes. The earlier single `market.sqlite` stays listed and readable;
  nothing is migrated and it is never archived.
- `market-YYYY-MM-DD.sqlite.br` (+ `.br.runs.json` run listing): a fully closed day older
  than the two newest day files, Brotli-compressed once per IST date outside 08:00–15:45.
  The raw file is removed only after the archive decompresses to the identical SHA-256;
  a day holding an unclosed run is never archived. Replay of an archived run decompresses
  that one day into `cache/` on demand. `LivePaper:ArchiveClosedDays=false` disables it.
- `paper.sqlite`: acknowledged account/intents/positions/attempts, lifecycle events,
  model assessment inputs/scenario sidecars, checkpoints and compressed append-only
  candidate/position decision batches. The paged JSON link reads committed history.
- `quote-replays.sqlite`: separate replay results, isolated from the live paper writer.
- Private reference-download caches. SQLite retains the frozen source evidence too.

WAL plus explicit `synchronous=FULL` transactions protect committed writes. Market
batching and paper-state persistence use different database files. No automatic
deletion is implemented. Do not copy only the main SQLite file while it is open;
stop cleanly or use a SQLite-aware backup that includes committed WAL state.

Restart restores acknowledged paper state, reconstructs a durable market tail
without inventing historical fills, marks possible uncommitted tail loss and pauses
entries. A data gap invalidates complete strategy history. Accepted pending entries
can cancel during recovery; their attempt remains consumed. Reconnect does not arm
auto. Changed-code recovery and replay disclose the source/current code identities.
Recorded recovery boundaries explicitly discard unacknowledged fills during original
replay too. Changed-policy recovery remains counterfactual and discloses unobserved
durability timing. Empty prior sessions close only after verified writer drainage and
roll to fresh references. Unresolved prior-day positions block a new session; their
held-option feed continues after verifying the current master mapping, rather than
erasing the position or filling a possibly reused token.

Original quote replay uses source arrival/control/timer order. A replay of an active
recording uses its committed prefix and discloses that it is still open. Crash/gap
recordings remain qualified as incomplete; they cannot establish exact reproduction
of events never committed. Replay preserves the declared delay and later-receipt
requirements at maximum processing speed, using simulated time rather than sleeping.

### Expected size (synthetic estimate, not yet measured live)

About 44 instruments in `full` mode (184-byte depth packets; the index is 32 bytes).
At ~1 update per instrument per second over 09:15–15:30, a synthetic stream of that shape
measured roughly **225 MB per day** in SQLite (about 1 KB of row overhead per message
beyond the raw payload) and **~55 MB per day** Brotli-compressed. Faster real ticking can
multiply this several times. The live view reports the open file size (database plus WAL)
and free disk, and raises `LOW_DISK_SPACE` below 5 GiB. Replace this estimate with the
measured figure after the first full session.

## Experimental option assessment

Per-contract midpoint IV calibration uses the matching-expiry observed future,
Black-76, ACT/365 time to 15:30 IST expiry, zero discount rate, and bounded 1%–300%
annualized IV. Calibration outside these bounds fails. Stress horizons are 250 ms,
one second and twenty minutes; IV multipliers are .8/1/1.2; futures moves are flat,
+1R favorable, -1R/-2R adverse **and the exact frozen structural invalidation**.
The latter remains included if price has moved far from the signal. Selling-side
scenarios subtract current half-spread and one tick, round adversely and may recover
zero. All scenario fees, inputs and assumptions are preserved as MODEL_ESTIMATE.

Net risk takes the conservative maximum across adverse/flat scenarios, immediate
liquidation friction and the gross-stop/cost estimate. Favorable opportunity is the
minimum twenty-minute +1R net result across IV stresses. Neither is a forecast,
calibrated confidence interval or maximum realized loss. Numerical validity and
assumption consistency are tested; empirical model accuracy is still unverified.
The ₹400 ceiling and ₹500 opportunity hurdle can legitimately reject all candidates.

## Verification commands

From the repository root with the pinned SDK on PATH:

```powershell
dotnet build AutoTrader.slnx --no-restore --configuration Release
dotnet test AutoTrader.slnx --no-build --no-restore --configuration Release
npm --prefix ui/trading-dashboard run build
```

Targeted backend checks: filter `FullyQualifiedName~Trading.Tests.Live`. Browser
checks: `npm --prefix ui/trading-dashboard run test:e2e -- live.spec.ts`; use the UI
README's isolated-port settings when manual servers are running. Browser lifecycle
checks include both controlled HTTP display transitions and an unmocked browser/API
synthetic packet → candidate → fill → exit → SQLite/replay journal path. Backend
checks cover decoder timestamps, model reference prices, fills, recovery, protection
under storage pressure and durable publication. None substitutes for a real-session run.

### Local verification — 20 October 2026

- Release solution build: passed, zero warnings/errors.
- Full backend suite: **347 passed**, including **15 live-quote tests**.
- Frontend strict TypeScript/production build: passed.
- Replay display-order checks: **12 passed**.
- Full desktop/mobile browser suite: **143 passed, one expected desktop skip** for
  the mobile-only layout case. Includes six live-workspace checks, with real-backend
  synthetic packet rehearsals on both viewport sizes.
- Scoped standards/spec review findings were corrected; final checks confirmed the
  dropped-ingress gate and acknowledged-recovery replay boundary fixes.

No authenticated live connection or full-session recorder benchmark was performed by
these software checks. Tomorrow still requires a valid daily token, data entitlement,
available reference evidence, pre-open startup and observed recording health.
