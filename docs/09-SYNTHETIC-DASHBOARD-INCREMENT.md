# Synthetic dashboard increment

Agreed with the user after grilling; implementation scope for this increment.
The Monday sprint (`08-MONDAY-LIVE-PAPER-SPRINT.md`) still governs the wider milestone.

## Outcome

Build a dark, compact desktop cockpit with a focused mobile summary while external
API validation proceeds separately. Backend snapshots are deterministic, explicitly
synthetic fixtures. No broker connection, recorder, selection policy or paper broker
is implied by this increment.

The workspace includes futures candles, cash-index context, banks, option comparison,
a selected-option plan drawer, preset paper-position monitoring and a durable journal.
Use one-minute completed bars, hover, zoom/pan, opening-range levels and approximate
VWAP from .NET. Prices/calculations belong in .NET; React formats and interacts.
Use Lightweight Charts with its license/NOTICE attribution.

## Shared public contract

JSON uses camelCase; all UTC timestamps use ISO 8601 and are displayed in Asia/Kolkata.
Prices/weights are .NET decimals serialized as JSON numbers. Missing values are null.
Synthetic contracts explicitly use a synthetic exchange and are not live contract data.

Endpoints:

- Existing `GET /api/health` retains its synthetic/paused contract.
- `GET /api/dashboard/scenarios` returns `ScenarioDescriptor[]`.
- `GET /api/dashboard/snapshot?scenarioId=normal` returns `DashboardSnapshot`.
  Omitting `scenarioId` selects `normal`; unknown scenarios return HTTP 404.
- `POST /api/journal/plans` accepts `SavePlanRequest`, returns HTTP 201 with
  `JournalEntry` and a Location pointing to its read endpoint.
- `POST /api/journal/plans/preview` accepts `PlanSelection`, returns `PlanPreview`:
  server-derived units and an indicative quote reference, without saving a plan.
- `GET /api/journal/plans` returns `JournalEntry[]`, newest first.
- `GET /api/journal/plans/{id}` returns the preserved entry or HTTP 404.

Validation failures return HTTP 400 Problem Details with a useful explanation.
Journal failures must not be displayed as saved. The journal snapshots are resolved
by the server, not accepted as client-supplied market evidence.

Contract notation (all properties are required; `?` below is expressed with `| null`):

```typescript
type ScenarioId = 'normal' | 'missing-inputs' | 'stale-quotes' | 'feed-loss' | 'open-position';
type QuoteState = 'fresh' | 'missing' | 'stale' | 'disconnected';
type PlanStatus = 'considered' | 'planned' | 'skipped';

interface ScenarioDescriptor { id: ScenarioId; label: string; description: string }
interface FixtureContract {
  id: string; symbol: string; exchange: 'SYNTHETIC'; underlying: 'BANKNIFTY';
  optionType: 'CE' | 'PE'; expiry: string; strike: number; lotSize: number;
  tickSize: number; validFromUtc: string; validToUtc: string; isSynthetic: true;
}
interface Quote {
  lastPrice: number | null; bid: number | null; ask: number | null;
  bidQuantity: number | null; askQuantity: number | null;
  eventTimeUtc: string | null; receivedAtUtc: string | null;
  lastTradeTimeUtc: string | null; state: QuoteState;
}
interface OptionRow {
  contract: FixtureContract; quote: Quote; spread: number | null;
  quoteAgeSeconds: number | null;
  decision: { action: 'WATCH' | 'SKIP' | 'INSUFFICIENT_DATA'; reasonCodes: string[] };
}
interface FixtureBar {
  startUtc: string; endUtc: string; availableAtUtc: string;
  open: number; high: number; low: number; close: number; volume: number;
}
interface BankRow {
  id: string; name: string; weightPercent: number | null;
  lastPrice: number | null; vwap: number | null; changePercent: number | null;
  aboveVwap: boolean | null; state: QuoteState;
  eventTimeUtc: string | null; receivedAtUtc: string | null;
}
interface DashboardSnapshot {
  schemaVersion: string; fixtureVersion: string; snapshotId: string;
  scenarioId: ScenarioId; scenarioLabel: string; sequence: number;
  asOfUtc: string; source: 'SYNTHETIC_FIXTURE'; mode: 'SYNTHETIC TEST';
  freshness: {
    state: 'fresh' | 'partial' | 'stale' | 'disconnected';
    quoteAgeSeconds: number | null; receiptAgeSeconds: number | null; message: string;
  };
  market: {
    indexName: string; indexValue: number | null; futuresSymbol: string;
    futuresValue: number | null; changePercent: number | null;
  };
  chart: {
    symbol: string; bars: FixtureBar[]; openingRangeHigh: number | null;
    openingRangeLow: number | null;
    vwap: { timeUtc: string; value: number }[]; vwapLabel: string;
  };
  banks: BankRow[];
  participation: {
    aboveVwapWeightPercent: number | null; belowVwapWeightPercent: number | null;
    missingWeightPercent: number; coverageLabel: string;
  };
  options: OptionRow[];
  position: {
    state: 'none' | 'open' | 'unpriced'; positionId: string | null;
    contractId: string | null; lots: number | null; units: number | null;
    entryPrice: number | null; currentBid: number | null;
    grossPnl: number | null; netPnl: number | null; message: string;
  };
  planning: {
    roundTripFees: number | null; requiredCash: number | null;
    netRisk: number | null; availabilityReason: string;
  };
}
interface PlanSelection {
  scenarioId: ScenarioId; snapshotId: string; contractId: string;
  lots: number;
}
interface PlanPreview {
  lots: number; units: number; referenceAsk: number | null; quoteState: QuoteState;
  planning: DashboardSnapshot['planning'];
}
interface SavePlanRequest {
  scenarioId: ScenarioId; snapshotId: string; contractId: string;
  lots: number; status: PlanStatus; thesis: string;
  stopPlan: string; invalidation: string; note: string;
}
interface JournalEntry {
  id: string; savedAtUtc: string; mode: 'SYNTHETIC TEST';
  scenarioId: ScenarioId; snapshotId: string; contractId: string;
  lots: number; units: number; status: PlanStatus; thesis: string;
  stopPlan: string; invalidation: string; note: string;
  snapshot: DashboardSnapshot;
}
```

## Fixture semantics

- Stable scenario/snapshot IDs and versions; stable contract IDs across scenarios.
  Use about 24 options and a small illustrative bank set. Lot sizes, weights, expiry
  and quantities are explicitly fixture definitions, not current exchange facts.
- Normal: usable synthetic observations, no position. Missing inputs: incomplete
  bank coverage and some absent option fields. Stale quotes: old observations with
  an unpriced preset position. Feed loss: disconnected/unpriced state. Open position:
  preset synthetic position with gross indicative liquidation, net P&L unavailable.
- Quote ages are evaluated against snapshot `asOfUtc`, not today's wall clock;
  make this distinction visible. An API connection does not imply a live feed.
- Retained candles in disconnected/stale scenarios remain visibly fixture history;
  they do not make quotes executable. Completed bars end and are available no later
  than snapshot as-of. Futures candle VWAP is labelled approximate; cash index has none.
- Both candle and VWAP chart coordinates label the candle's period start. VWAP values
  use only completed, available bars; bar end/availability remain separate evidence.
- Missing bank weight remains disclosed without renormalizing the observed portion.
  Candidate decisions are illustrative WATCH/SKIP/INSUFFICIENT_DATA with reason codes,
  not real strategy recommendations or probabilities. No Greeks/depth are inferred.
- Fee/cash/net-risk estimates stay null with an explanation. Paper execution and
  auto controls remain disabled; an entry in the journal is a plan, not an intent/fill.

## Interaction and persistence

- Navigation: cockpit and journal within the same app.
- Select an option for its read-only contract/quote details and editable plan.
- Pin up to two contracts. Freeze captures the two observations and their snapshot
  identity/as-of; scenario changes still update the rest of the cockpit. Unfreeze
  resolves the pinned IDs against the current snapshot. No synthetic feed timer.
- Editable fields: positive integer whole lots (default one), considered/planned/skipped
  status, thesis, stop-plan/invalidation text, plain-text note. Backend derives units
  from the selected fixture contract; no JavaScript financial/quantity calculation.
- Save into SQLite at `Storage:SqliteJournalPath`, resolved against API content root.
  Trading.Data owns SQLite access; Trading.Portfolio owns synthetic journal behavior;
  Trading.Api supplies clock/time and composes endpoints. Persist immutable snapshots
  with schema/fixture provenance. Backend restart retains entries. No destructive reset
  endpoint is part of this increment. Initialize the journal schema lazily on journal use.
- Prevent overlapping saves in the UI. Preserve edits or explain/reset them explicitly
  when changing the selected contract/scenario. Plain-text notes are rendered as text.
- Read failures expose unavailable state and retry, and never silently present cached
  snapshots as current. Preserve the existing validated API-health semantics.

## Verification seams and acceptance

Public backend snapshot/journal HTTP APIs and the browser workflow are the agreed seams.
Use vertical red/green slices, not private-method or implementation-mirroring tests.

1. Inspect a deterministic snapshot, chart and banks; verify completed-bar availability,
   explicit missing coverage, unavailable fields and synthetic-only provenance.
2. Select and compare two options; freeze, change scenario, retain the frozen as-of;
   unfreeze and display current observations.
3. Save a plan/note with server-derived whole-lot units and retrieve its original
   evidence after browser refresh and a new backend host using the same SQLite file.
4. Reject unknown/mismatched snapshot/contract IDs and invalid whole lots through HTTP.
5. Show stale/feed-loss/unpriced states and disabled paper controls, independently of
   API transport health. Verify frontend error/recovery states.
6. Desktop and narrow-mobile accessibility/overflow checks, then backend build/tests,
   frontend typecheck/build, and browser E2E. Generated DBs/test artifacts are ignored.
