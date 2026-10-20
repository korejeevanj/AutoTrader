# Replay paper trader

[Paper profit exits](16-PAPER-PROFIT-EXITS.md) adds independently selectable fixed
net targets and trailing net-profit locks, frozen per run/position, with trigger,
peak, threshold and realized-shortfall evidence. Baseline remains available.

The [historical readiness increment](15-HISTORICAL-DATA-READINESS.md) governs
qualification, provenance and gap recovery. Processed acquisition and completed
playback are separate from qualified historical evidence.

For the current controls, tabbed UI, data-health/error reporting and complete run-log
downloads, see [Replay reliability and diagnostics](14-REPLAY-RELIABILITY-AND-DIAGNOSTICS.md).

Open **Replay** and choose a completed day; selecting the date starts preparation.
**Prepare selected day** resumes/retries that date with compatible cached inputs.
Each date resolves its own contracts, candles and session clock. A finished price-only
preview is separate from a finished download: returning to an incomplete preview resumes
the remaining acquisition instead of treating its report as a fully prepared dataset.
The three readiness stages distinguish price playback, historical preparation and the
Decision Engine. Downloading every eligible option can take several minutes.

A paper-ready preparation reuses compatible cached option candles and adds the missing
front-month future/bank inputs. Old price-only reports and their frozen inputs remain
unchanged. If enrichment completes during active playback, **Restart replay** adopts it
at an explicit boundary; an idle completed preview can adopt the prepared dataset
automatically. A downloaded future permits Engine diagnostics even when some other
required inputs are missing; missing gates reject trades with recorded reasons.

## Where trading is visible

- **Signals and trading decisions:** defaults to signals/trade highlights, supplemented
  by the loaded durable lifecycle events. Switch to **All assessments** or **Blocked /
  missing evidence**, search a contract/action/reason, or select a blocking-reason chip.
  Repeated outcomes are grouped and paged (10/25/50 groups); **Inspect decision** opens
  the selected contract's setup/gate evidence. Every actual evaluation is durably recorded;
  the live snapshot contains the latest 200 assessments. Strict execution qualification
  and candle-research eligibility are separate—historical candles are never converted
  to observed quotes/depth.
- **Replay paper account:** virtual starting capital, cash, reservations, paid model
  fees, accepted attempts and estimated realized net P&L.
- **Pending paper entries:** accepted intents, deadlines and reserved cash.
- **Open paper positions:** gross/net marks, HOLD/WATCH_EXIT/EXIT and pending exits.
- **Paper trade ledger:** simulated entry/exit fills, reasons, gross/net results and
  component entry/exit fee breakdowns.
- **Persisted paper events:** class-filtered, searchable event history backed by SQLite,
  with ten rows per page, independent of the synthetic plan journal and browser refresh.
  The endpoint supplies the latest 200 events for the selected class; full evaluation
  batches and final reports retain the complete evidence.

Starting playback evaluates the configured strategy automatically. **Pause new paper
entries** blocks new entries and cancels queued entries at the next event step; exits
continue to be monitored. Pausing playback stops the replay clock. These are different
controls. All amounts/settings remain configurable in the Engine/Replay profiles.

**No actual trades are placed at any time.** The provider clients support fixed-origin
read-only historical/master GET requests. There is no real order/GTT/modify/cancel
adapter. The ledger and fills below are simulations, not an actual broker account.

## Historical inputs and their fidelity

The selected day's official NSE F&O contract master defines contracts, expiry, strike,
lot and tick. Price-discovery/pre-open status `6` is a valid daily-master identifier:
it must not discard the front month in favour of the next month. Reserved, deleted,
suspended and ineligible records remain distinct. Broker rows join exact identifiers
only; they never select a current universe for a historical day.

For bank confirmation, the app retrieves the latest available **prior-month official
Nifty Bank report**, preserves its ZIP/hash and PDF table, validates the date and full
weight total, and carries that published snapshot forward. For 14 October 2026 the
model uses **30 September 2026's 14-bank snapshot (100%)**. Those are published monthly
weights, **not reconstructed daily October weights**. The page/preparation retains
the snapshot date and convention. A future-published report cannot influence a prior
decision. Exact daily constituent data would require a source that supplies it.

Carried-forward snapshots now have a configurable 45-calendar-day limit. An HTTP
`Last-Modified` header alone is unqualified historical publication evidence; the
automatically fetched snapshot remains diagnostic research until stronger dated
evidence is supplied. A known membership change must end the old effective interval.

Bank equity identities, lots/ticks and closing-auction flags come from the dated NSE
CM security masters, with previous-day identity checks and exact broker mapping.
Kite supplies bank minute candles. CAS-eligible securities end continuous trading at
15:15, so their final 15 minutes are not fabricated or counted as missing continuous
bars. Cash indices have no traded-volume VWAP.

Historical candle/contract availability remains a source capability. Some expired
option minute series cannot be retrieved from Kite. No-data responses remain distinct
from holidays, inactivity and invalid data. The system does not promise records that
the provider does not retain.

## Cost model

`src/Trading.Replay/replay.example.json` includes an estimated, versioned paper reference:
₹20/executed order; options-sale STT 0.15% from 1 April 2026; combined exchange/IPFT
0.03553%; SEBI 0.0001%; buy stamp 0.003%; GST 18% on brokerage/exchange/regulatory
charges. Rates are JSON fractions, not percentages. STT/stamp round to whole rupees
in this reference model; other components round to paise. Public exchange/IPFT and
contract-note rounding details are qualified: this estimates paper costs, not exact
broker reconciliation. It never applies the older 0.10% options STT to this period.

Sources: [Zerodha charges](https://zerodha.com/charges/),
[NSE FATAX73524](https://nsearchives.nseindia.com/content/circulars/FATAX73524.pdf),
[Nifty monthly reports](https://www.niftyindices.com/reports/monthly-reports),
[September 2026 full weights](https://www.niftyindices.com/Indices_-_Market_Capitalisation_and_Weightage/indices_dataSep2026.zip),
[NSE pre-open FAQ](https://nsearchives.nseindia.com//web/mediaattachment/2026-09/Annexure_Faq_on_Pre-open_Session_in_Equity_Derivatives_FO_Segment_20260903182159.pdf).

## Coverage presentation

An option may have no returned candles or a sparse series. These are coverage states,
not one corrupt-data error for every possible minute. They disable dependent candidates
until usable evidence is available. Unrelated options can still be assessed for
diagnosis, but an unresolved potentially eligible option makes selection evidence
incomplete. Required futures/bank gaps still block dependent setup/confirmation.
Invalid rows and conflicting duplicates remain errors. Zero volume remains observed
zero, never depth; it is an explicit exclusion under the candle policy.

`quality.json` includes per-instrument expected/returned/missing/zero-volume counts.
Session reporting also checks reference qualification and decision-time coverage
against the full strategy-permitted universe. An unexplained empty distant strike
can leave selection incomplete; the resulting diagnostic trades are excluded from
qualified aggregate performance. Detailed `DATA_READINESS` events name unknown inputs.
Price-only reports say financial metrics were not evaluated rather than apparent zero
performance.

## Persistence and recovery

The separate file is **`data/replay/paper-replay.sqlite`** (relative to the configured
historical root). Each transaction commits:

1. Full compressed evaluation batch, including detailed gate records.
2. Indexed decision/intent/fill/exit events with deterministic keys.
3. Coherent paper account/position snapshot and entry-control history.

Duplicate batches are idempotent; conflicting evidence is rejected. Failed journal
commits pause playback and must be retried before another tick advances. After API
restart, **Retry preparation** restores pinned raw data/configuration and reconstructs
the saved paper clock/controls deterministically, compares its state to the durable
checkpoint, and leaves it paused. Changed input/code fingerprints start a new run.
Each stress preset and explicit restart is run-scoped; no paper fill is counted twice.

The read-only event endpoint is `/api/replay/jobs/{id}/paper-log?limit=200&category=...`.
Full final reports retain config/data/code versions and the declared candle fill model.
An idealized candle fill is not evidence of actual liquidity, subsecond protection or
profitability. A correctly rejected session may have zero trades.
