# Live paper session runbook and operating modes

Applies from 2 November 2026. Paper only: no broker order, GTT, modify or cancel endpoint is ever called.
Background: [22 live quote paper trading](22-LIVE-QUOTE-PAPER-TRADING.md). This document adds what changed after the
21 and 22 October sessions (zero trades) and how to run the day.

## What the October recordings showed

- The feed went half-open for ~28 s before the host noticed (09:20 and 15:13 IST on 22 Oct). The host timer kept ticking throughout,
  so the stall was upstream, not host starvation.
- On 21 Oct, with the current code, bar coverage stays intact from the open until 15:29. No feed gap made that day void.
  The strict "futures last trade within 2 s" entry gate did: **the futures last-trade timestamp is older than 2 s on about two thirds of
  ticks** (median ~3 s, 90th percentile 8-15 s, 99th 17-34 s; exchange timestamp 99th percentile ~1.2 s). Auto was therefore
  entry-eligible on roughly 8% of evaluations. This is **not changed yet**; it needs a decision (see "Open decisions").
- On 22 Oct a real day high/low moved during the ~35 s outage at 09:20, so no honest recovery of that opening range exists
  without a historical-candle backfill.

## Operating modes

`AutoTrader:Mode` (env `AutoTrader__Mode`), default `Full` (everything in one process, as before).

| Mode | Registers | Notes |
|---|---|---|
| `Live` | feed, recorder, paper writer, session, archiver, `/api/live/*`, SignalR | No cockpit, no historical replay. Quote replays and the rehearsal are refused (`REPLAYS_RUN_IN_THE_RESEARCH_INSTANCE`). |
| `Research` | cockpit, historical replay, `/api/live/recordings`, `/replays`, `/status`, `/readiness` | Read-only on the live run: every control is refused (`LIVE_FEED_RUNS_IN_THE_LIVE_INSTANCE`); no archiving. |
| `Full` | all of the above | Unchanged default; keep for development and tests. |

Run Live and Research as two processes. The live process runs a **frozen publish** (`C:\AutoTrader-live\bin`) so research rebuilds
cannot lock or change it, and `CodeVersion` stays fixed for the day. Configuration, credentials and data paths still come from
`src/Trading.Api` (the content root).

Scripts (PowerShell, from the repo root):

- `scripts\live\publish-live.ps1` builds Release and publishes the frozen copy (refuses while a live process runs from it).
- `scripts\live\start-live.ps1` starts the Live instance on :5080 with `ConnectOnStartup=true`.
- `scripts\live\start-research.ps1` starts the Research instance on :5081.
- `scripts\live\morning-check.ps1 [-Watch]` prints the readiness checklist; exit code 1 until ready.

UI against two processes: `$env:AUTOTRADER_RESEARCH_PORT = 5081; npm --prefix ui/trading-dashboard run dev` (live status, commands and
SignalR go to :5080; recordings, replays and the rest of the API go to :5081). Without that variable all traffic goes to :5080.

## Session policy v2

Recorded in each new run's manifest (`SessionPolicy`), so older recordings replay exactly as before and new ones replay from their own
events. Under v2:

1. **Short feed gaps can be healed, disclosed.** Per instrument, on the first tick after a gap of at most 60 s, coverage is restored only if
   the packet's exchange day high and low are unchanged from before the gap (no swing was missed). From the gap's minute on, VWAP for that
   instrument comes from the exchange's cumulative average traded price and the feature calculator version carries
   `+exchange-atp-gap-recovered`. Otherwise coverage stays void with an issue naming the reason
   (`FEED_GAP_NOT_RECOVERABLE:GAP_TOO_LONG | DAY_EXTREME_MOVED_DURING_GAP | EXCHANGE_FIELDS_MISSING`). Storage-side loss
   (recorder capacity, paper persistence) never heals. The post-gap bar still carries the outage's volume; the recovered VWAP does not use it.
2. **Manual lifecycle waiver.** A completed `MANUAL_TEST` lifecycle on an earlier run with the same code build and the same engine
   configuration lets paper-auto be armed without repeating it (`MANUAL_LIFECYCLE_WAIVED_PRIOR_RUN_SAME_BUILD:<run>` in the run's reference issues).
   Every new publish needs one manual lifecycle again, which is another reason not to republish casually.
3. **Auto re-arms after a clean recovery**: 10 s of continuously healthy entries after a feed or recorder loss. Never after an operator
   pause, the session loss pause, a restart or lost recording (`AUTO_REARMED_AFTER_RECOVERY` is recorded as an issue).
4. **Paper persistence stall tolerance**: the same 2 s as the market recorder; a latched write failure still voids the session.

Feed watchdog: 5 s without any message (heartbeats included) between 09:15 and 15:30 IST forces a reconnect and resubscribe
(`LivePaper:FeedWatchdogSeconds`, 0 disables). `/api/live/status` reports the last disconnect reason (`FEED_WATCHDOG` when it fired),
measured reconnect seconds, watchdog count, session-step timing and timer slip.

Storage: decision rows are written to `data/live/decisions-YYYY-MM-DD.sqlite` (no fsync per commit); `paper.sqlite` holds only run state
and checkpoints. Earlier runs keep their rows in `paper.sqlite` and are still read from there. Checkpoint and decision serialization runs on
the paper writer thread, not under the host lock. Decision files are about 1 GB per day and are not archived; prune them when
disk is short (the market recording is the source of truth).

## Pre-market checklist

One-off, any time before the session, and after any code change (never during market hours):

1. Stop old processes. `git status` should be clean and on the intended commit.
2. `.\scripts\live\publish-live.ps1`. Note the commit in `C:\AutoTrader-live\bin\BUILD.txt`.
3. Keep the machine awake all day (`powercfg /change standby-timeout-ac 0`) and avoid Windows updates restarting it.

Each trading morning:

| Time (IST) | Step |
|---|---|
| ~08:00 | Renew the Kite token ([12 daily setup](12-DAILY-KITE-SETUP.md)); it goes in `src/Trading.Api/kite.credentials.Local.json`. |
| 08:30-08:40 | `.\scripts\live\start-live.ps1`. It prepares the run (master, bank membership, weights, fees) and connects. Starting before 08:30 is fine; references refresh at 08:30. |
| 08:45 | `.\scripts\live\morning-check.ps1 -Watch`. Before the open everything should be `ok` except `MANUAL_LIFECYCLE_OR_WAIVER` (on a new build) and `AUTO_ARMED`; quote freshness and the option universe are not required until 09:15-09:16. From 08:45 the same alert shows on the Live page, in the process's stderr and in `data/live/readiness-DATE.log`; from 09:10 it is CRITICAL. |
| 09:15-09:20 | `UNIVERSE_SELECTED` turns ok after the first fresh futures tick (30 options). |
| by 09:25 | If `MANUAL_LIFECYCLE_OR_WAIVER` is a FAIL (new build): submit **MANUAL_TEST** on a frozen-universe option and wait for its exit (intent, fill, exit request, exit fill). Then **Enable paper-auto**. If it shows `waived`, just **Enable paper-auto**. Auto must be armed before 09:30. |
| 09:30 | Entries open until 11:30. Position monitoring continues to 15:15 session exit. Do not start replays or rebuilds. |
| 15:45+ | Research may start (`start-research.ps1`); the archiver compresses closed day files (never the open run). |
| After 15:30 | **Stop recording** on the Live page (refused while a paper position is open), then stop the process. |

During the day, watch: `Feed watchdog reconnects`, last disconnect, reconnect seconds, `session step max`, `timer slip max`, recorder lag and queue.
A reconnect disarms auto, then auto re-arms by itself after 10 s of healthy entries. `FEED_GAP_NOT_RECOVERABLE` means coverage is void for the day
and no strategy entry can form; that is the honest outcome, not a fault to work around. Zero trades remains valid.

If the frozen build misbehaves: stop it, check out the previous commit, `publish-live.ps1`, restart. Do not run Live and Full on the same data
directory at the same time.

## Research data capture (from 2 November 2026)

- The live run now also subscribes **INDIA VIX** and **NIFTY 50** (`ContextIndex`, research only: no gate reads them; a missing row is skipped and
  shows up as `CONTEXT_INDEX_NOT_IN_INSTRUMENT_MASTER:<symbol>` in the run's issues) and decodes total buy/sell quantity, open interest with its day
  range, and day open/previous close. Recorded packets already held these fields; days before this change can be re-decoded with
  `python -I scripts/research/extract_features.py <market-day.sqlite> <out.sqlite>`.
- The Research instance (`start-research.ps1`) archives the day's Bank Nifty option minute candles with open interest automatically after 15:50 IST
  (retrying every 15 minutes until 20:00) if it is running and the Kite token is valid: `GET /api/research/archive/status`, or trigger it with
  `POST /api/research/archive/options`. Keep the Research instance running through the evening, or start it before 20:00.
- Index history and the pre-registered tests: [25 research protocol](25-RESEARCH-PROTOCOL.md).

## Open decisions

1. **Futures freshness gate: decided, option (a), implemented under session policy v2.** The futures quote is fresh by its exchange and
   receipt timestamps (limits unchanged at 2 s); the engine receives the exchange timestamp as the underlying's event time
   (`SourceVersion` carries `+exchange-timestamp-event-time`), and last-trade age remains displayed evidence. No Engine edit. Replay what-ifs
   (v2, auto force-armed, original settings, entry window 09:30-11:30) made auto eligible on 80% of evaluations on 21 Oct (about 8% before) and
   93% on 22 Oct. Remaining ineligibility is mostly exchange age above 2 s between one-per-second snapshots; widening that limit also loosens
   option-quote freshness for fills, so it is a separate decision, not made here. On 21 Oct there were still no trades: no opening-range/VWAP
   setup formed (`NO_ACTIVE_SETUP`).
2. **Opening-range gaps that moved a day extreme** (22 Oct case). Only a historical minute-candle backfill (new recorded event kind) can restore
   them. The watchdog shortens future outages from ~35 s to ~8 s, which makes this rarer; decide whether to build the backfill.
3. **Scalping parameters** (entry window, attempts, target and cost gates) are Engine configuration and untested for scalping; run labelled
   what-if replays on clean recorded days before changing anything.
