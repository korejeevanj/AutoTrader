# AutoTrader dashboard

## Live paper workspace

Open **Live paper** (`#live`) for the experimental Bank Nifty quote session. The
backend owns WebSocket recording, delayed local fills, protection timers, recovery
and engine/risk calculations independently of browser polling. The page polls
`/api/live/status` every 500 ms with a five-second deadline and removes prior
observations when connectivity fails. It displays server-derived quote freshness,
completed-bar/constituent evidence, pending intents versus fills, pending exits,
candidate gates and the fee-reconciled journal. Start recording before 09:15 IST;
validate the manual lifecycle before arming auto. Replay selects an immutable SQLite
recording, optionally a changed engine JSON profile, and produces a separate result.

Use the [live runbook](../../docs/22-LIVE-QUOTE-PAPER-TRADING.md) and daily token guide.
`e2e/live.spec.ts` covers controlled HTTP UI transitions and an unmocked real-backend
synthetic packet/engine/fill/exit/SQLite journal rehearsal on desktop/mobile; backend
Live tests additionally exercise timing, storage-pressure protection and recovery.
These checks do not establish authenticated provider availability or profitability.

Compact dark React/TypeScript multi-index cockpit and durable shared plan journal.
`../../docs/10-MULTI-INDEX-AND-OPENAPI-INCREMENT.md` governs this increment;
`../../docs/09-SYNTHETIC-DASHBOARD-INCREMENT.md` supplies the legacy workflow.
Market observations
come from the real .NET REST endpoints. **SYNTHETIC TEST** is persistent: these
fixtures establish software behavior, not a live feed, strategy or paper fills.

## Historical Replay

Open **Replay** (`#replay`) and select a completed Bank Nifty date. Backend jobs
resolve historical identities, check the exchange calendar, download all date-valid
CE/PE strikes/expiries, reuse validated cached responses and expose progress. Historical
catalogue/weight/fee gaps are explicit; today's instrument list is not used as past
evidence. Configure metadata once as described in the
[daily Kite setup guide](../../docs/12-DAILY-KITE-SETUP.md), linked directly in the page.

Daily credentials stay in backend `src/Trading.Api/kite.credentials.Local.json`
(gitignored and automatically reloaded). The page shows only credential status;
there is no token input or token storage in React. Date/job IDs alone are retained for
browser refresh recovery. A newer date cannot be replaced by an obsolete response.
Changing dates cancels preparation only; an explicitly started replay needs an explicit
pause/cancel. Retry preserves completed source bindings and settings. Refresh creates
a new revision. Backend restart is labelled Interrupted and replay restarts from cache.

Play/Pause/Step/Run Full Day share the replay library.
Run Full Day finishes the current preset once. **Run all presets** is a separate,
explicit comparison action; the control panel shows the current preset and counter
as each comparison preset deliberately restarts the clock. Publishing reports is
shown separately from completed playback.
The chart, bank observations, reasons and hypothetical positions contain only
current simulated-time evidence.
The [profit-exit guide](../../docs/16-PAPER-PROFIT-EXITS.md) documents the **Profit
protection** selector, configurable fixed/trailing net policies, new paused run
boundaries, active profit locks and trigger-versus-fill evidence. Earlier runs
remain available for same-dataset comparison.
The UI rejects future candle payloads. All candidates are assessed in bounded batches,
not truncated to nearest strikes. Report links are versioned and appear after publication.
Completed reports remain downloadable after restart. Candle-research mode and frozen
settings are shown separately from the synthetic cockpit and from strict Engine gates.

### Replay display ownership

`src/replayDisplay.ts` owns selected-day lifecycle, polling/cancellation, command
responses, server retirement, revision ordering and acquisition/run evidence matching.
Its snapshot contains accepted display evidence; failed health reads remove prior
qualification, and changing runs clears prior lifecycle events. All response paths
cross the same acceptance seam. `src/useReplayDisplay.ts` connects that module to React
through `useSyncExternalStore` and supplies the existing transport, browser storage and
timer adapters. `HistoricalReplay.tsx` retains layout, tabs and inspection choices.
The selected instrument stays in React; the hook supplies it as request context.
An acknowledged command from an obsolete inspection triggers a current-context read
instead of projecting its old chart or resubmitting the command.
Financial values and historical qualification still come from the .NET implementation.

`npm run test:display` runs deterministic response-order checks through the complete
display module's interface using controlled transport/storage/timer adapters. It uses
the existing Playwright runner without launching a browser or local servers; no new
test dependency is required. The normal replay browser checks remain in
`e2e/replay.spec.ts` and exercise the React adapter on desktop/mobile.

### Isolated browser-test ports

Default E2E ports remain 5080/5173. If another local server is running, leave it alone
and set two free ports for the test process (from this UI directory):

```powershell
$env:AUTOTRADER_E2E_API_PORT = '5380'
$env:AUTOTRADER_E2E_UI_PORT = '5373'
npm run test:e2e
```

Playwright starts isolated API/Vite processes and points the test proxy at that API.
No broker credentials or live provider calls are needed for browser tests.

## Tooling and verification

- Node **22.23.2**, npm **10.9.8** (`.node-version`, `packageManager`, engine ranges).
- Exact dependencies with an npm-generated lockfile. React / React DOM **19.3.0**,
  Lightweight Charts **5.2.1**, Vite **8.3.4**, TypeScript **7.0.2**,
  React plugin **6.1.2**, Playwright **1.64.0**. Types: React / React DOM **19.3.0**,
  Node **22.20.5**. No UI-kit or table dependency.

Run commands from `ui/trading-dashboard`. Verify this directory before installing
or building. Every terminal launching browser tests needs the pinned .NET SDK
**10.0.401** on its PATH:

```powershell
Test-Path -LiteralPath "C:\Projects\AutoTrader\ui\trading-dashboard"
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
npm ci
npm run typecheck
npm run build
npm run test:display
npm run test:e2e
```

`build` typechecks and builds `dist/`, including the chart license and notice.
`test:e2e -- --project=desktop --grep "freezes two"` runs a focused slice.

## Local startup

With the SDK on PATH, start the backend in one terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project ../../src/Trading.Api/Trading.Api.csproj --no-launch-profile --urls http://localhost:5080
```

In a second terminal, run `npm run dev` and open **http://localhost:5173**. Vite
uses a strict port and proxies `/api` and `/hubs` to **http://localhost:5080**.
Production hosting must provide same-origin API routing; the proxy is not part
of `dist/`. This increment has no SignalR subscription or synthetic feed timer.
Development Swagger is on the **API origin**, `http://localhost:5080/swagger`;
the UI does not proxy or link to a same-origin Swagger route.

## Workspace behavior

- **Cockpit / Journal** navigation uses URL fragments; reloading `#journal`
  retrieves durable backend entries. Changing tabs preserves in-memory drafts.
- **Cockpit index** comes from `GET /api/dashboard/indices`, initially Bank Nifty,
  Nifty 50 and Sensex. IDs are strings, with no frontend enumeration; additional
  configured catalog identities use the same chart/options/plan/position modules.
  Headings and constituent labels use catalog metadata. Catalog definitions and
  capabilities are illustrative, not verified market-master evidence.
- Catalog capabilities control the current cockpit. Disabled `futuresChart`
  displays an explanatory unavailable panel with no canvas, zoom, pan or reset
  actions. Cash-index context remains available. Disabled `optionComparison`
  removes candidate selection, pins, freeze and plan actions; selection and frozen
  state clear, while the stable plan component retains per-index/contract drafts
  for later re-enabling. Disabled `weightedConstituents` retains the observation
  table but explicitly labels weighted confirmation unavailable; row weights and
  aggregate above/below weights stay null, with the server's 100% missing weight
  disclosed. Each capability is independent; all three may be disabled together.
- Five server-described scenarios: normal, missing inputs, stale quotes, feed
  loss, and open position. Load/change/explicit refresh fetch a REST snapshot.
- **Fixed fixture clock** and snapshot as-of are displayed in **Asia/Kolkata**.
  Quote/receipt ages are supplied by .NET relative to that as-of, never the
  browser's wall clock. Retained candles remain labelled fixture history.
- Cash-index context has no VWAP. The futures chart has completed one-minute
  candles, backend candle-derived approximate VWAP, opening-range levels,
  hover OHLC/IST, mouse/touch zoom and pan, keyboard-accessible zoom/pan/reset
  buttons, and an expandable text/data summary. ResizeObserver, crosshair
  subscriptions and chart instances are cleaned up on replacement/unmount.
- Constituent weight above/below VWAP and missing weight are disclosed without client
  renormalization. The 24 synthetic option rows retain contract identity and
  show bid/ask, observed depth, source age, lot/tick metadata and reason codes.
- Selecting a row focuses the plan heading. Closing returns focus to its
  contract control. The skip link focuses the main region without changing the
  active Cockpit/Journal view. Tables have labelled, focusable contained scroll regions;
  narrow mobile prioritizes market/position summaries and has no page overflow.
- Pin up to two options from the plan. **Freeze** copies their observations,
  snapshot ID and as-of; other panels continue to change with the scenario.
  Pins are locked while frozen. **Unfreeze** resolves IDs against the current
  snapshot. Frozen evidence is explicitly historical even if a read fails.
- Switching indices clears active selection, pins and frozen comparison, including
  the parent pin lock. A brief accessible message explains the reset. The scenario
  remains selected consistently; captured observations cannot cross workspaces.
- Plan edits are retained **per index + contract in this browser tab**, including across
  scenario changes and drawer closure. A scenario change preserves the selected
  contract and edits; saving captures its **current** scenario/snapshot. Reload
  discards unsaved drafts. Reset requires **Discard draft edits** confirmation;
  **Keep editing** cancels. Saving preserves the draft and disables another save
  of the same unchanged plan until edited/reset or its evidence changes.
- Whole lots default to one. `/preview` supplies units, reference ask and planning
  availability. React performs no VWAP/spread/units/P&L calculations. Invalid lots
  disable save. Fee, cash and net-risk fields stay unavailable with server copy.
- Preview and save submit required **index/scenario/snapshot/contract** identity.
  Save includes the draft text. Only a validated,
  matching **HTTP 201** displays success. One request at a time; draft, selection
  index, scenario, comparison and contract controls are locked while saving. Failures retain edits and expose
  retry. A lost response can have persisted: inspect the journal before retrying.
- Journal defaults to **All indices**, independently of the cockpit selection.
  Its route heading is **Shared decision journal**, with **ALL INDICES / PLAN
  REVIEW** as the eyebrow. An accessible body scope states **All indices** or the
  selected catalog label/ID; changing the journal filter never makes the page
  heading imply the cockpit's index.
  Its catalog-driven filter sends `GET /api/journal/plans?indexId=…`; a misrouted
  response containing another index is rejected. Every card and evidence view
  labels its index. Notes are plain text. Original contract, quotes, constituents,
  position, chart and full snapshot JSON retain schema/fixture provenance.
- Known schema-1 `synthetic-dashboard-v1` Bank Nifty journal snapshots remain
  readable when the API supplies explicit root `indexId: "BANKNIFTY"`. Their
  missing/null snapshot index/constituents remain untouched; original `banks` are
  used only by the constituent presentation. The JSON view prints the received
  original snapshot, never a schema-2 normalization. Legacy identity also requires
  the exact original `market.indexName: "BANKNIFTY (synthetic cash context)"`, in
  addition to the known schema/fixture/snapshot, synthetic source/mode and Bank
  Nifty contract underlyings. Unknown versions/fixtures, contradictory market
  names, mixed underlyings or mismatched journal identities are rejected.
- Preset positions show none/open/unpriced. Gross P&L is a backend indicative
  liquidation value; net is unavailable. Entry, exit and auto controls are disabled
  with explanations. A journal plan is never represented as an intent or fill.

## Connectivity and failure semantics

`GET /api/health` still requires **all four** approved fields:

```json
{
  "status": "ok",
  "application": "AutoTrader",
  "mode": "SYNTHETIC TEST",
  "paperAutoEnabled": false
}
```

**Connected** means the latest REST health check validated this contract, not
continuous availability or market freshness. It rechecks every fifteen seconds;
manual refresh clears prior connected status. Health is independent of fixture
freshness and snapshot/journal failures.

Requests bypass cache and time out after five seconds. Nested catalog, snapshot, preview,
scenario and journal fields are validated at runtime, including finite numbers,
nullable fields, enums, identities and sorted/completed chart timestamps. Failed
or malformed reads clear current observations and display unavailable/retry.
An explicit frozen comparison remains identifiable as captured history.
Replacement/unmount aborts requests and obsolete responses are ignored.
Current reads require schema **2**, explicit `indexId` matching the requested
catalog identity, generic `constituents`, and every contract's `underlying`
matching that index. Schema 1 is supported only for saved legacy evidence.
The cockpit passes its catalog definition into snapshot validation. Disabled
features must have matching unavailable evidence: empty/null futures fields,
an empty option universe with no held-position evidence, and null weights with
100% missing coverage. Empty charts/options are rejected for enabled default
fixtures. Saved evidence is validated independently of today's capability flags
so catalog changes do not rewrite historical records.
Invalid current metadata exposes an unavailable state with an explicit error
and genuine retry. Index/scenario/preview/filter changes cannot accept obsolete
async results. Nullable estimates remain unavailable without client calculations.

Public client functions in `src/api.ts`:

```typescript
readIndices(signal: AbortSignal): Promise<IndexDefinition[]>
readSnapshot(indexId: string, scenarioId: ScenarioId, signal: AbortSignal, definition?: IndexDefinition): Promise<DashboardSnapshot>
readJournal(signal: AbortSignal, indexId?: string): Promise<JournalEntry[]>
previewPlan(selection: PlanSelection, signal: AbortSignal): Promise<PlanPreview>
savePlan(plan: SavePlanRequest, signal: AbortSignal): Promise<JournalEntry>
readEntry(id: string, signal: AbortSignal): Promise<JournalEntry>
```

`IndexDefinition` matches docs/10 exactly: `id`, `displayName`, `exchange`,
`timeZone`, `constituentLabel`, `definitionVersion`, `effectiveFromUtc`, nullable
`effectiveToUtc`, `isSynthetic: true`, and boolean `capabilities.futuresChart`,
`optionComparison`, `weightedConstituents`. Lot/tick metadata belongs to contracts.
`readSnapshot` uses the supplied definition's identity/capabilities; omitting the
optional definition retains the default fully enabled fixture validation.

## Browser tests and isolation

Leave **5080 / 5173** free: Playwright starts fresh API and Vite processes with
`reuseExistingServer: false`, then shuts them down. The API executable is portable
`dotnet`; `DOTNET_HOST_PATH` can override it. `webServer.env` supplies an absolute
`ASPNETCORE_ENVIRONMENT=Development` and an absolute `Storage__SqliteJournalPath`
under `../../data/e2e/`, unique per Playwright process and run. Global setup uses
Node's built-in `node:sqlite` (no new dependency) to seed only this fresh isolated
DB with the literal pre-increment Bank Nifty evidence fixture. Normal workflows
use actual HTTP endpoints; no successful market responses are browser mocks.
Capability browser checks transform genuine HTTP catalog/snapshot responses at
the system boundary into matching unavailable-feature payloads. Normal workflows
and recovery still use the actual backend without those transformations.
Tests never use the development
journal DB. Generated databases and browser results are ignored by the workspace.

Default browser: installed **Microsoft Edge**, headless, on desktop **1440×900**
and narrow mobile **390×844**. The mobile project changes viewport, not engine.
If Edge is unavailable:

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
npx playwright install chromium
$env:PLAYWRIGHT_CHANNEL = 'chromium'
npm run test:e2e
```

Public-browser checks cover real snapshots, chart controls/text summary, missing
weight, compare/freeze/scenario/unfreeze, actual-201 plan save and journal reload,
plain text/original provenance, per-index/contract drafts/reset, whole-lot validation,
disabled stale/feed-loss execution, keyboard focus and mobile overflow. Failure
and delayed-response injection occurs only at the browser HTTP boundary; normal
workflow and recovery use the real backend. Health, snapshot, scenario, journal,
save, and obsolete-preview/snapshot behavior are exercised separately. Backend
restart durability is covered by backend HTTP tests, not a browser process hack.
`e2e/multi-index.spec.ts` adds switch/freeze reset and draft restoration, different
server lot previews, cross-index actual-201 saves/all-index/per-index journal reads,
current metadata rejection/retry, obsolete index/preview/filter responses, misrouted
filtered reads, and schema-1 evidence/JSON preservation. Failure injection remains
at the public HTTP boundary. `e2e/dashboard.spec.ts` retains the earlier workflows
with index query parameters and schema-2 fixture identities.
`e2e/review-gaps.spec.ts` verifies independent/all-disabled capabilities across
the five scenarios, capability/evidence mismatches, invalid empty default fixtures,
empty universes that conceal held evidence, capability re-enable draft recovery,
contradictory legacy market identity rejection on both journal reads, and the
shared route heading plus explicit all-index/Nifty filter scopes.

Multi-index assertion-red/green slices: the genuine cockpit rendered before the
missing accessible index selector failed; real preview/save flows reached the
missing journal filter; wrong-index snapshot rejection lacked the required clear
invalid-metadata explanation; a rejected catalog left the snapshot in a loading
state instead of unavailable/retry. All became green. Other focused checks verify the
same slices' state isolation and compatibility; they are not claimed as separate
assertion-red evidence. Backend startup/missing routes are not counted as red.
Review-fix red/green slices additionally observed fake chart controls on disabled
futures, rejection of a valid disabled-options empty universe, missing explicit
weighted-unavailable copy, acceptance of contradictory legacy market identity,
and a Bank Nifty cockpit heading on the shared journal. Each was corrected and
verified through the public browser workflow.

Screenshots: `test-results/**/cockpit.png`, `snapshot.png`,
`frozen-comparison-plan.png`, `journal.png`, `mobile-summary.png`.
Failing tests also retain traces.
Multi-index captures: `test-results/multi-index-index-switch-*/nifty-cockpit.png`
and `sensex-cockpit.png`, on desktop and mobile.
Review capability captures: `test-results/review-gaps-all-disabled-*/disabled-capabilities.png`,
on desktop and mobile.

Initial multi-index verification: repeated `npm run typecheck`, `npm run build`,
and a final focused desktop/mobile run: **53 passed, 1 skipped** (mobile-only layout
assertion skipped on desktop). The focused run includes every multi-index test
and affected chart/freeze/save/draft/error/accessibility workflow. Obsolete
whole-lot preview isolation also passed in an earlier focused run. The parent
session runs the complete E2E suite after review. Production output retains both
license/NOTICE files; the owned API/Vite processes shut down and ports 5080/5173
were checked free after testing.
Review-gap verification: repeated typecheck and production build passed; the final
focused desktop/mobile run passed **44 tests**, including all 26 review-gap checks
and affected normal chart, keyboard, freeze, save, draft, multi-index and legacy
workflows. Existing body-heading assertions now select **Decision journal**
exactly, distinct from the shared page heading. The parent still owns the complete
suite run after these fixes.

## Files and attribution

- `src/model.ts`, `validation.ts`, `api.ts`: public REST contracts and validation.
- `src/useRemote.ts`, `Health.tsx`: cancellation, recovery and independent health.
- `src/Chart.tsx`, `Panels.tsx`: context, constituents, stable option rows and presets.
- `src/Comparison.tsx`, `Plan.tsx`, `Journal.tsx`: evidence and plan workflow.
- `src/App.tsx`, `styles.css`, `format.ts`: navigation, responsive cockpit, display.
- `playwright.config.ts`, `e2e/*.spec.ts`, `e2e/setup.ts`, `e2e/fixtures/`: isolated
  real-backend tests and the pre-increment persisted evidence seed.
- `public/NOTICE.txt`, `public/LICENSE-lightweight-charts.txt`: retained distribution
  notice and full Apache 2.0 license, copied into production `dist/` by Vite.

Charts use **TradingView Lightweight Charts™ 5.2.1**, Apache 2.0. The public footer
links to [TradingView](https://www.tradingview.com/) and local license/NOTICE;
the library's chart attribution logo is enabled. NOTICE retains the upstream
v5.2.1 attribution; LICENSE retains the npm-distributed copyright notice.
