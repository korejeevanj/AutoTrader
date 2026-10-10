# AutoTrader agent guide

## Current state and source precedence
- This checkout contains planning/research Markdown only. No application, manifests, lockfiles, CI, or build/test commands exist yet. Paths in the architecture documents are proposed; inspect the checkout before scaffolding and verify installed tooling before pinning SDK/package versions.
- Read `docs/08-MONDAY-LIVE-PAPER-SPRINT.md` first for the 12 October 2026 milestone. It brings experimental selection and local paper execution forward, overriding the 9 October manual-dashboard-only ordering for that milestone. Real execution and actual-account monitoring remain deferred.
- `docs/00-START-HERE.md` and `docs/01-SCOPE-AND-DECISIONS.md` describe the wider baseline. `docs/resources/` preserves dated research, not current scope. The custom dashboard is already decided; D00 is inspiration work, not a build-versus-buy gate.
- Consult `docs/02-ARCHITECTURE-1.md` and `docs/05-SYSTEM-COMPONENTS-AND-PROJECTS.md` for contracts/ownership, `docs/06-DASHBOARD-SPECIFICATION.md` for UI, and `docs/03-DATA-AND-REPLAY.md` for historical/replay rules. These describe intended behavior, not implemented capabilities.

## Implementation boundaries
- Planned sprint stack: one local C#/.NET API process hosting feed, recorder, analytics, engine and paper broker; custom React/TypeScript UI over REST snapshots and SignalR. SQLite stores journal/config/paper state; append-only versioned files store market events. Logical modules do not require separate services.
- Financial calculations belong in shared .NET Analytics/Risk code, reused by dashboard and engine; React owns presentation/interaction. Domain/selection code receives data and a clock from hosts and never calls broker endpoints or the system clock directly.
- Kite is the live data source; the paper broker is a local simulator. Omit the real execution adapter and never call broker order/GTT/modify/cancel endpoints in this milestone. Keep provider credentials/tokens server-side.
- Recording and open-position monitoring must run independently of browser refresh/UI throttling. Recorder pressure that compromises completeness must expose degraded state and inhibit entries.

## Data and decision traps
- Use UTC internally and Asia/Kolkata for sessions/display. Distinguish bar start, end and `AvailableAt`; completed bars cannot influence decisions before availability. Replay uses a supplied clock and deterministic ordering of events at the same decision timestamp.
- Load point-in-time contract validity, lot/tick sizes, constituent membership/weights and effective-date fees. Do not reuse old examples or current weights for historical replay. Missing bank inputs must disclose missing weight, not silently renormalize it.
- Cash indices have no traded volume for VWAP; use futures/banks and label candle-derived VWAP approximate. Missing values remain null/unavailable, not zero; do not invent depth, Greeks or tradable prices from candles.
- Track event time, receipt time, bid/ask freshness and last-trade age separately: transport heartbeat is not quote freshness. UI labels are `PAPER + LIVE DATA`, `PAPER + REPLAY`, or `SYNTHETIC TEST`; weekend cached quotes are not live.
- Hard validity/freshness/liquidity/cash/risk/cost gates precede ranking; manual paper entry bypasses only setup detection and is labelled `MANUAL_TEST`. A score is not a probability, and zero trades is valid.
- Keep gross stop triggers, fees and net risk separate. Reject infeasible whole-lot risk instead of widening the budget. The sprint's provisional ₹1,200 net session pause is paper-only; the real-execution daily loss budget remains unagreed.

## Paper execution and verification
- Fill only against a fresh quote received after intent plus configured delay: long entry at ask plus adverse slippage, exit at bid minus adverse slippage, rounded adversely to tick size. Require whole-lot observed depth; do not charge spread twice.
- Feed loss blocks entry and leaves unexecutable exits visibly pending; never fill from stale quotes. Persist/idempotently recover intents and positions; restart paused. Evaluate quote-driven cash stops independently of completed-bar entry signals.
- Before implementing tests, use the sprint's verification section: future isolation/completed-bar timing, deterministic replay, fee/cash reconciliation, delayed bid/ask fills, duplicate-intent prevention, stale-feed/restart recovery and zero real-order requests. Browser E2E covers feed → candidate → paper fill → position → exit → journal.
- Synthetic fixtures verify software only. Candle replay cannot establish subsecond fills, and one live paper session does not establish profitability. Preserve data/config/code provenance and report gaps and stop overruns.
