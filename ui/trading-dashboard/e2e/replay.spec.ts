import { expect, test, type Page } from '@playwright/test';
import type { HistoricalQualification, ProfitExitSettings, ReplayEvidence, ReplayJob, ReplayView } from '../src/replayApi';

// A stand-in for the server: it states what is preparing and publishes one verdict per view.
const preparingStates = ['Queued', 'Resolving', 'Downloading', 'Validating'];
let currentQualification: HistoricalQualification | null = null;
test.beforeEach(() => { currentQualification = null; });

function job(date: string, state = 'Ready'): ReplayJob {
  const result: ReplayJob = { id: `job-${date}`, date, state, message: 'Historical candles prepared', calendarStatus: 'TradingDay', catalogSource: 'Dated full catalogue',
    warnings: [], instruments: [{ id: 'NFO:BANKNIFTY', token: 123, role: 'Futures', state: 'Complete', rows: 375, cached: true, error: null }],
    total: 1, finished: 1, canReplay: state === 'Ready', preparationComplete: state === 'Ready', mode: 'PAPER + REPLAY — CANDLE RESEARCH', run: 1, reportsAvailable: false, attempt: 1 };
  let pinned: boolean | undefined;
  Object.defineProperty(result, 'preparingData', { enumerable: true, configurable: true,
    get: () => pinned ?? preparingStates.includes(result.state), set: (value: boolean) => { pinned = value; } });
  return result;
}
function evidenceFor(view: ReplayView): ReplayEvidence {
  const state = view.job.state; const preparing = view.job.preparingData === true; const priceOnly = view.playbackKind === 'PriceOnly';
  const qualification = preparing || priceOnly ? null : currentQualification;
  const label = state === 'MarketClosed' ? 'Not applicable — market closed'
    : state === 'AuthenticationRequired' ? 'Evidence blocked — authentication required'
    : state === 'NoData' ? 'Evidence unavailable'
    : priceOnly ? view.job.preparationComplete ? 'Prepared strategy inputs await Restart replay' : preparing ? 'Strategy input preparation in progress' : 'Strategy inputs unavailable'
    : !qualification ? 'Evidence awaiting validation' : qualification.qualified ? 'Evidence qualified' : 'Evidence incomplete';
  const jobLabel = state !== 'Completed' ? state
    : priceOnly ? view.job.preparationComplete ? 'Price preview completed — restart to apply prepared inputs' : 'Price preview completed — strategy inputs unavailable'
    : !qualification ? 'Playback completed — checking evidence' : qualification.qualified ? 'Playback completed — input evidence qualified' : 'Playback completed — evidence incomplete';
  const readiness = preparing ? 'Acquisition in progress — evidence awaiting validation'
    : !view.job.canReplay ? 'Not ready' : priceOnly ? 'Price preview only'
    : currentQualification?.qualified ? 'Qualified historical inputs — candle model only' : 'Incomplete — diagnostic replay only';
  return { label, jobLabel, preparing, readiness, issues: qualification?.issues.length ?? 0, qualification };
}
function initial(date: string, state = 'Ready'): ReplayView {
  const view = { job: job(date, state), simulatedTimeUtc: null, paused: true, complete: false, bars: [], feature: null, constituents: [], decisions: [], trades: [],
    openPositions: 0, pendingEntries: 0, netSessionPnl: 0, reportFiles: [],
    settings: { version: 'paper-policy-v1', preset: 'base', grossStop: 200, maximumNetRisk: 400, candidateBatchSize: 35, spreadTicks: 20, slippageTicks: 2 } } as Omit<ReplayView, 'evidence'> as ReplayView;
  // The verdict is live, so tests that change the job's state see the server's wording follow.
  Object.defineProperty(view, 'evidence', { enumerable: true, configurable: true, get: () => evidenceFor(view) });
  return view;
}
function advance(view: ReplayView) {
  view.simulatedTimeUtc = `${view.job.date}T03:46:00Z`;
  view.bars = [{ id: 'bar-1', startUtc: `${view.job.date}T03:45:00Z`, endUtc: view.simulatedTimeUtc, availableAtUtc: view.simulatedTimeUtc,
    open: 55000, high: 55020, low: 54990, close: 55010 }];
  view.feature = { openingHigh: null, openingLow: null, vwap: 55006.67, aboveWeight: 70, belowWeight: 30, missingWeight: 0, direction: null, setupId: null };
  view.decisions = [{ contractId: 'NFO:BANKNIFTYCE', class: 'STRICT_ENGINE', action: 'INSUFFICIENT_DATA', reasons: ['OPTION_QUOTE_MISSING'] }];
}
function runInfo(view: ReplayView) {
  return { runId: view.paperRunId ?? `${view.job.id}:run-1:base`, jobId: view.job.id, date: view.job.date, preset: 'base', datasetHash: 'fixture-data', inputHash: 'fixture-input', simulatedTimeUtc: view.simulatedTimeUtc, realizedNetPnl: view.netSessionPnl, trades: view.trades.length, complete: view.complete };
}
async function mockReplay(page: Page) {
  const views = new Map<string, ReplayView>();
  const requests: { path: string; body: Record<string, unknown> }[] = [];
  await page.route('**/api/replay/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/replay/status') return route.fulfill({ json: { credentialStatus: 'ConfiguredNotVerified', latestCompletedDate: '2026-10-16',
      guideUrl: '/api/replay/setup', metadataMessage: 'Dated metadata required' } });
    if (path === '/api/replay/jobs') {
      const body = route.request().postDataJSON(); requests.push({ path, body });
      const view = initial(body.date); views.set(view.job.id, view);
      return route.fulfill({ status: 202, json: view.job });
    }
    if (path === '/api/replay/runs') return route.fulfill({ json: [...views.values()].map(runInfo) });
    const id = path.split('/')[4] ?? ''; const view = views.get(id);
    if (!view) return route.fulfill({ status: 404, json: { detail: 'Job not found' } });
    if (path.endsWith('/data-health')) return route.fulfill({ json: { jobId: view.job.id, attempt: view.job.attempt, total: view.job.total, processed: view.job.finished, complete: view.job.finished, sparse: 0, empty: 0, failed: 0, invalid: 0, pending: 0, cached: 1, requiredAffected: 0, readiness: 'Ready', instruments: [], warnings: [] } });
    if (path.endsWith('/diagnostics')) return route.fulfill({ json: { operations: [], runs: [runInfo(view)] } });
    if (path.endsWith('/paper-log')) return route.fulfill({ json: view.paperRunId ? [{ sequence: 1, runId: view.paperRunId,
      timeUtc: view.simulatedTimeUtc ?? `${view.job.date}T03:45:00Z`, indexId: 'BANKNIFTY', contractId: 'NFO:BANKNIFTYCE',
      category: 'CANDLE_RESEARCH', action: 'HYPOTHETICAL_FILLED', setupId: 'setup-1', reasonsJson: '["DECLARED_CANDLE_MODEL"]' }] : [] });
    if (path.endsWith('/commands')) {
      const body = route.request().postDataJSON(); requests.push({ path, body });
      if (body.action === 'setSpeed') view.timing = { mode: 'Paced', requestedSpeed: body.speed, achievedSpeed: null, lagMilliseconds: 0, processingMilliseconds: 2 };
      if (body.action === 'step') { advance(view); if (view.playbackKind === 'PriceOnly') view.feature = null; }
      if (body.action === 'play') { view.job.state = 'Playing'; view.paused = false; }
      if (body.action === 'pause') { view.job.state = 'Paused'; view.paused = true; }
      if (body.action === 'pauseEntries' && view.paper) view.paper.entriesPaused = true;
      if (body.action === 'enableEntries' && view.paper) view.paper.entriesPaused = false;
      if (body.action === 'run') {
        advance(view); view.complete = true; view.job.state = 'Completed'; view.job.reportsAvailable = true;
        view.reportFiles = ['summary.md', 'trades.csv'];
        view.trades = [{ id: 'trade-1', contractId: 'NFO:BANKNIFTYCE', units: 25, entryTimeUtc: `${view.job.date}T03:45:00Z`, entryPrice: 200,
          exitKnownAtUtc: view.simulatedTimeUtc, exitPrice: 202, netPnl: 25, exitReason: 'SESSION_EXIT', evidenceClass: 'HYPOTHETICAL_CANDLE_RESEARCH' }];
        if (view.paper) { view.paper.positions = []; view.paper.realizedNetPnl = 25; view.paper.cash = 500025; }
      }
      if (body.action === 'runAllPresets') {
        view.complete = false; view.paused = false; view.job.state = 'Playing'; view.job.reportsAvailable = false; view.reportFiles = [];
        view.presetProgress = { id: 'base', number: 1, total: 2, allPresets: true };
      }
      if (body.action === 'setProfitExit' && view.settings) {
        view.settings.profitExit = body.profitExit; view.job.run++; view.job.state = 'Paused'; view.paused = true;
        view.complete = false; view.simulatedTimeUtc = null; view.trades = []; view.job.reportsAvailable = false;
      }
      if (body.action === 'restart') { const reset = initial(view.job.date); views.set(id, reset); return route.fulfill({ json: reset }); }
      if (body.action === 'cancel' || body.action === 'cancelPreparation') view.job.state = 'Cancelled';
    }
    return route.fulfill({ json: view });
  });
  return { views, requests };
}

test('date-only preparation exposes daily token guide, causal playback, results and reload recovery', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.goto('/#replay');
  await expect(page.getByRole('heading', { name: 'Historical Replay', exact: true })).toBeVisible();
  await expect(page).toHaveTitle('AutoTrader · Historical Replay');
  await expect(page.getByRole('link', { name: 'Daily Kite token setup' })).toHaveAttribute('href', '/api/replay/setup');
  await page.getByLabel('Replay date').fill('2026-10-09');
  await expect(page.getByRole('region', { name: 'Historical playback controls' })).toBeVisible();
  expect(mock.requests.at(0)?.body).toEqual({ date: '2026-10-09', refresh: false });
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('0 completed candles');
  await page.getByRole('button', { name: 'Step', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('1 completed candles');
  const decisions = page.getByRole('region', { name: 'Replay decisions' });
  await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
  await decisions.getByLabel('Decision view').selectOption('all');
  await decisions.getByRole('button', { name: 'Inspect decision' }).first().click();
  await expect(decisions).toContainText('OPTION_QUOTE_MISSING');
  await page.reload();
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('1 completed candles');
  expect(mock.requests.filter(request => request.path === '/api/replay/jobs')).toHaveLength(1);
  await page.getByRole('button', { name: 'Run Full Day', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Hypothetical replay trades' })).toContainText('NFO:BANKNIFTYCE');
  await page.getByRole('tab', { name: 'Run evidence', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Replay reports' }).getByRole('link', { name: 'summary.md' })).toHaveAttribute('href', '/api/replay/jobs/job-2026-10-09/reports/1/summary.md');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await expect(page.locator('input[type=password]')).toHaveCount(0);
});

test('closed days and authentication-required states remain explicit without playback', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date, body.date === '2026-10-08' ? 'MarketClosed' : 'AuthenticationRequired');
    view.job.message = view.job.state === 'MarketClosed' ? 'Exchange calendar confirms market closed' : 'AUTHENTICATION_REQUIRED';
    mock.views.set(view.job.id, view); return route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-08');
  await expect(page.getByRole('region', { name: 'Historical replay preparation' })).toContainText('MarketClosed');
  await expect(page.getByLabel('Replay readiness')).toContainText('Not applicable — market closed');
  await expect(page.getByRole('region', { name: 'Historical playback controls' })).toHaveCount(0);
  await page.getByLabel('Replay date').fill('2026-10-09');
  await expect(page.getByRole('region', { name: 'Historical replay preparation' })).toContainText('AUTHENTICATION_REQUIRED');
  await expect(page.getByLabel('Replay readiness')).toContainText('Evidence blocked — authentication required');
  await expect(page.getByRole('button', { name: 'Retry preparation' })).toBeEnabled();
});

test('an obsolete date response cannot replace the new day and cancels only preparation', async ({ page }) => {
  const mock = await mockReplay(page);
  let release: () => void = () => {}; let entered: () => void = () => {};
  const hold = new Promise<void>(resolve => { release = resolve; });
  const reached = new Promise<void>(resolve => { entered = resolve; });
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date, body.date === '2026-10-09' ? 'Queued' : 'Ready');
    mock.views.set(view.job.id, view);
    if (body.date === '2026-10-09') { entered(); await hold; }
    await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-09'); await reached;
  await page.getByLabel('Replay date').fill('2026-10-12');
  await expect(page.getByRole('region', { name: 'Historical playback controls' })).toBeVisible();
  release();
  await expect.poll(() => mock.requests.some(request => request.path.includes('job-2026-10-09/commands') && request.body.action === 'cancelPreparation')).toBe(true);
  await expect(page.getByLabel('Replay date')).toHaveValue('2026-10-12');
  expect(await page.evaluate(() => localStorage.getItem('replay-job'))).toBe('job-2026-10-12');
});

test('future candle payloads are rejected rather than painted ahead of the replay clock', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs/*', async route => {
    const view = mock.views.get(new URL(route.request().url()).pathname.split('/')[4] ?? '')!;
    advance(view); view.simulatedTimeUtc = `${view.job.date}T03:45:00Z`;
    await route.fulfill({ json: view });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-09');
  await expect(page.getByRole('alert')).toContainText('beyond the supplied replay clock');
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toHaveCount(0);
});

for (const future of [false, true]) {
  test(`rule-based directional evidence ${future ? 'rejects future availability' : 'shows breadth and price context'}`, async ({ page }) => {
    const mock = await mockReplay(page);
    await page.route('**/api/replay/jobs', async route => {
      const body = route.request().postDataJSON(); const view = initial(body.date); advance(view);
      const clock = `${body.date}T04:01:00Z`; view.simulatedTimeUtc = clock;
      Object.assign(view.feature!, { directionalEvidence: {
        version: 'directional-context-v1', status: 'UP', asOfUtc: clock, barEndUtc: clock,
        evidenceAvailableAtUtc: future ? `${body.date}T04:02:00Z` : clock, meetsDirectionalRules: true,
        closeMinusOpeningHigh: 10, closeMinusOpeningLow: 40, closeMinusVwap: 20,
        return1MinutePercent: 0.04, return5MinutePercent: 0.08, confirmingCount: 10, opposingCount: 4,
        neutralCount: 0, topThreeConfirmingWeightPercent: 21, minimumConfirmingConstituents: null, reasons: []
      } });
      mock.views.set(view.job.id, view); return route.fulfill({ status: 202, json: view.job });
    });
    await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-09');
    if (future) {
      await expect(page.getByRole('alert')).toContainText('beyond the supplied replay clock');
      await expect(page.getByRole('region', { name: 'Rule-based directional evidence' })).toHaveCount(0);
    } else {
      const panel = page.getByRole('region', { name: 'Rule-based directional evidence' });
      await expect(panel).toContainText('UP');
      await expect(panel).toContainText('10 / 4 / 0');
      await expect(panel).toContainText('21.00%');
      await expect(panel).toContainText('Directional rules passed');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    }
  });
}

test('missing catalogue allows index price replay with no fabricated futures VWAP or strategy trades', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date, 'ReadyWithWarnings');
    view.job.canReplay = true; view.job.mode = 'PAPER + REPLAY — PRICE ONLY';
    view.job.warnings = ['HISTORICAL_CATALOGUE_UNAVAILABLE'];
    view.job.message = 'Index prices are ready; downloading the selected day’s NSE contract archive.';
    view.playbackKind = 'PriceOnly'; view.chartLabel = 'Bank Nifty index — historical prices (no traded-volume VWAP)';
    view.chartInstrumentId = 'index'; view.chartInstruments = [{ id: 'index', role: 'CashIndex' }];
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-14');
  await expect(page.getByRole('region', { name: 'Historical playback controls' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Historical playback controls' })).toContainText('Trading decisions and simulated trades remain disabled');
  await page.getByRole('button', { name: 'Step', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('1 completed candles');
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('no traded-volume VWAP');
  await expect(page.getByRole('region', { name: 'Hypothetical replay trades' }).locator('tbody tr')).toHaveCount(0);
});

test('paper account controls and durable decision events are visible separately from market playback', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date);
    view.paperRunId = 'paper-run-1'; view.paper = { startingCapital: 500000, cash: 500000, availableCash: 500000, reservedCash: 0,
      realizedNetPnl: 0, paidFees: 0, entriesPaused: false, acceptedAttempts: 0, pendingIntents: [], positions: [] };
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-14');
  await expect(page.getByRole('region', { name: 'Paper account' })).toBeVisible();
  await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Durable paper log' })).toContainText('HYPOTHETICAL_FILLED');
  await page.getByRole('button', { name: 'Pause new paper entries' }).click();
  await expect(page.getByRole('button', { name: 'Enable new paper entries' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Historical playback controls' })).toContainText('Entry pause does not stop position exits');
  await page.getByRole('tab', { name: 'Overview', exact: true }).click();
  await page.getByRole('button', { name: 'Run Full Day' }).click();
  await expect(page.getByRole('region', { name: 'Hypothetical replay trades' })).toContainText('SESSION_EXIT');
  await expect(page.getByRole('region', { name: 'Open paper positions' }).locator('tbody tr')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('large decision snapshots group repeated outcomes and paginate without growing the page', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date); advance(view);
    view.decisions = Array.from({ length: 200 }, (_, i) => ({ contractId: `NFO:OPTION-${i}`, class: 'CANDLE_RESEARCH', action: 'REJECTED',
      reasons: [`BLOCKER_${Math.floor(i / 10)}`], timeUtc: view.simulatedTimeUtc }));
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  const panel = page.getByRole('region', { name: 'Replay decisions' });
  await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
  await expect(panel.getByText('No new signal or trade in the recent snapshot')).toBeVisible();
  await panel.getByLabel('Decision view').selectOption('all');
  await expect(panel.locator('article')).toHaveCount(10);
  await expect(panel).toContainText('20 grouped results');
  await panel.getByRole('button', { name: 'Next page' }).click();
  await expect(panel).toContainText('Page 2 of 2');
  await panel.getByLabel('Search decisions').fill('OPTION-199');
  await expect(panel.locator('article')).toHaveCount(1);
  await panel.getByRole('button', { name: 'Inspect decision' }).click();
  await expect(panel).toContainText('BLOCKER_19');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('switching days prepares the new date and never retains the earlier replay evidence', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.goto('/#replay');
  for (const date of ['2026-10-09', '2026-10-16']) {
    await page.getByLabel('Replay date').fill(date);
    await expect(page.getByRole('region', { name: 'Historical playback controls' })).toBeVisible();
    await page.getByRole('button', { name: 'Step', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('1 completed candles');
    expect(await page.evaluate(() => localStorage.getItem('replay-job'))).toBe(`job-${date}`);
  }
  expect(mock.requests.filter(request => request.path === '/api/replay/jobs').map(request => request.body.date)).toEqual(['2026-10-09', '2026-10-16']);
});

test('changing playback speed sends an immediate command and the server acknowledges the requested speed', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await page.getByLabel('Playback speed', { exact: true }).selectOption('1');
  await expect.poll(() => mock.requests.some(row => row.body.action === 'setSpeed' && row.body.speed === 1)).toBe(true);
  await expect(page.getByRole('region', { name: 'Historical playback controls' })).toContainText('Requested 1×');
  expect(mock.requests.some(row => row.body.action === 'step' || row.body.action === 'play')).toBe(false);
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('0 completed candles');
});

test('changing inspection during a pending command keeps the chosen chart when its refresh fails', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date);
    view.revision = 1; view.chartInstrumentId = 'future'; view.chartLabel = 'Futures context';
    view.chartInstruments = [{ id: 'future', role: 'Futures' }, { id: 'option-B', role: 'Call' }];
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  let returned = false; let release: () => void = () => {}; let entered: () => void = () => {};
  const held = new Promise<void>(resolve => { release = resolve; }); const reached = new Promise<void>(resolve => { entered = resolve; });
  await page.route(/\/api\/replay\/jobs\/[^/?]+\?/, async route => {
    if (returned) return route.fulfill({ status: 503, json: { detail: 'Current projection unavailable' } });
    const url = new URL(route.request().url()); const view = structuredClone(mock.views.get(url.pathname.split('/')[4]!)!);
    view.chartInstrumentId = url.searchParams.get('instrumentId') ?? 'future';
    view.chartLabel = view.chartInstrumentId === 'option-B' ? 'Option B context' : 'Futures context';
    await route.fulfill({ json: view });
  });
  await page.route('**/commands', async route => {
    const body = route.request().postDataJSON(); const path = new URL(route.request().url()).pathname;
    mock.requests.push({ path, body });
    const view = structuredClone(mock.views.get('job-2026-10-16')!); advance(view); view.revision = 2;
    mock.views.set(view.job.id, view); entered(); await held; returned = true; await route.fulfill({ json: view });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await page.getByRole('button', { name: 'Step', exact: true }).click(); await reached;
  try {
    await page.getByLabel('Inspect historical instrument').selectOption('option-B');
    await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('Option B context');
  } finally { release(); }
  await expect(page.getByRole('alert')).toContainText('Current projection unavailable');
  await expect(page.getByLabel('Inspect historical instrument')).toHaveValue('option-B');
  await expect(page.getByRole('region', { name: 'Historical replay chart' })).toContainText('Option B context');
  expect(mock.requests.filter(row => row.body.action === 'step')).toHaveLength(1);
});

test('full day runs once and all-preset comparison is explicit with visible phase progress', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date);
    view.presetProgress = { id: 'base', number: 1, total: 2, allPresets: false };
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  const controls = page.getByRole('region', { name: 'Historical playback controls' });
  await expect(controls).toContainText('Run Full Day finishes the current preset once');
  await controls.getByRole('button', { name: 'Run Full Day', exact: true }).click();
  await expect(controls).toContainText('Completed');
  await expect(controls).toContainText('Current preset: base · single run');
  await page.waitForTimeout(1700);
  expect(mock.requests.filter(row => row.body.action === 'run')).toHaveLength(1);
  await controls.getByRole('button', { name: 'Run all presets', exact: true }).click();
  await expect(controls).toContainText('Preset 1 of 2: base');
  const view = mock.views.get('job-2026-10-16')!;
  view.presetProgress = { id: 'adverse', number: 2, total: 2, allPresets: true };
  await expect(controls).toContainText('Preset 2 of 2: adverse');
  view.complete = true; view.paused = true; view.job.state = 'Completed';
  await expect(controls).toContainText('Completed');
  expect(mock.requests.filter(row => row.body.action === 'runAllPresets')).toHaveLength(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('fixed and trailing profit policies apply as separate runs and expose the active profit lock', async ({ page }) => {
  const mock = await mockReplay(page);
  const policy: ProfitExitSettings = { version: 'profit-exit-v1', mode: 'Disabled', netTarget: 500, trailActivation: 500, trailGiveback: 200, minimumLockedNetProfit: 300 };
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date); view.settings!.profitExit = policy;
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  const panel = page.getByRole('region', { name: 'Paper profit-exit policy' });
  await panel.getByLabel('Paper profit-exit mode').selectOption('FixedNetTarget');
  await panel.getByRole('button', { name: 'Apply policy and restart', exact: true }).click();
  await expect.poll(() => mock.requests.filter(row => row.body.action === 'setProfitExit').length).toBe(1);
  expect(mock.requests.find(row => row.body.action === 'setProfitExit')?.body.profitExit).toMatchObject({ mode: 'FixedNetTarget', netTarget: 500 });
  await panel.getByLabel('Paper profit-exit mode').selectOption('TrailingNetProfit');
  await panel.getByRole('button', { name: 'Apply policy and restart', exact: true }).click();
  await expect.poll(() => mock.requests.filter(row => row.body.action === 'setProfitExit').length).toBe(2);
  const view = mock.views.get('job-2026-10-16')!;
  const trailing = view.settings!.profitExit!;
  expect(trailing).toMatchObject({ mode: 'TrailingNetProfit', trailActivation: 500, trailGiveback: 200, minimumLockedNetProfit: 300 });
  await panel.getByLabel('Trailing giveback ₹', { exact: true }).fill('150');
  await expect(panel.getByRole('button', { name: 'Apply policy and restart', exact: true })).toBeEnabled();
  view.simulatedTimeUtc = '2026-10-16T04:03:00Z'; view.openPositions = 1;
  view.paper = { startingCapital: 500000, cash: 494983, availableCash: 494983, reservedCash: 0, realizedNetPnl: 0, paidFees: 2, entriesPaused: false, acceptedAttempts: 1, pendingIntents: [],
    positions: [{ id: 'position', contractId: 'OPTION-A', units: 25, entryTimeUtc: '2026-10-16T04:01:00Z', entryPrice: 200.6, grossPnl: 744, netPnl: 700, policyAction: 'HOLD', reasons: ['POLICY_VALID'], pendingExitReason: null,
      currentProfitExitThreshold: 600, profitExit: { policy: trailing, peakNetLiquidationPnl: 800, triggeredAtUtc: null, triggerNetLiquidationPnl: null, triggerThreshold: null, realizedShortfall: null } }] };
  await expect(page.getByRole('region', { name: 'Open paper positions' })).toContainText('Peak net mark ₹800.00');
  await expect(page.getByRole('region', { name: 'Open paper positions' })).toContainText('Exit threshold ₹600.00');
  await expect(panel.getByRole('button', { name: 'Apply policy and restart', exact: true })).toBeDisabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('profit-exit ledger distinguishes the trigger mark from the fill and its shortfall', async ({ page }) => {
  const mock = await mockReplay(page);
  const policy: ProfitExitSettings = { version: 'profit-exit-v1', mode: 'FixedNetTarget', netTarget: 500, trailActivation: 500, trailGiveback: 200, minimumLockedNetProfit: 300 };
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date);
    view.settings!.profitExit = policy; view.complete = true; view.job.state = 'Completed'; view.simulatedTimeUtc = `${body.date}T04:04:00Z`;
    view.trades = [{ id: 'trade', contractId: 'OPTION-A', units: 25, entryTimeUtc: `${body.date}T04:02:00Z`, entryPrice: 200.6,
      exitKnownAtUtc: view.simulatedTimeUtc, exitPrice: 219.4, netPnl: 466, exitReason: 'NET_PROFIT_TARGET', evidenceClass: 'HYPOTHETICAL_CANDLE_RESEARCH',
      profitExit: { policy, peakNetLiquidationPnl: 566, triggeredAtUtc: `${body.date}T04:03:00Z`, triggerNetLiquidationPnl: 566, triggerThreshold: 500, realizedShortfall: 34 } }];
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  const ledger = page.getByRole('region', { name: 'Hypothetical replay trades' });
  await ledger.getByText('Profit protection', { exact: true }).click();
  await expect(ledger).toContainText('Peak net mark ₹566.00');
  await expect(ledger).toContainText('net mark ₹566.00 · threshold ₹500.00');
  await expect(ledger).toContainText('Realized shortfall ₹34.00');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('data health keeps failed requests and empty series visible and exposes decision impact', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/data-health', route => route.fulfill({ json: { jobId: 'job-2026-10-16', attempt: 1, total: 3, processed: 3, complete: 1, sparse: 0, empty: 1, failed: 1, pending: 0, invalid: 0, cached: 1, requiredAffected: 1, readiness: 'Degraded — evidence gates apply', warnings: ['WEIGHTS_CARRIED_FORWARD'], instruments: [
    { id: 'BANK-A', role: 'Constituent', state: 'Failed', rows: 0, expectedMinutes: 375, missingMinutes: 375, cached: false, error: 'PROVIDER_UNAVAILABLE', impact: 'Bank confirmation is blocked; missing weight is not renormalized.', issues: [], missingIntervals: [] },
    { id: 'OPTION-A', role: 'Call', state: 'Unavailable', rows: 0, expectedMinutes: 375, missingMinutes: 375, cached: true, error: 'EMPTY_RESPONSE', impact: 'Rejects this contract only.', issues: [], missingIntervals: [{ startUtc: '2026-10-16T03:45:00Z', endUtc: '2026-10-16T10:00:00Z' }] },
  ] } }));
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await expect(page.getByRole('region', { name: 'Historical replay preparation' })).toContainText('1 complete · 0 sparse · 1 empty · 1 failed');
  await page.getByRole('button', { name: 'Inspect data health' }).click();
  const panel = page.getByRole('region', { name: 'Dataset health details' });
  await expect(panel).toContainText('PROVIDER_UNAVAILABLE'); await expect(panel).toContainText('EMPTY_RESPONSE');
  await panel.getByText('Impact and missing intervals', { exact: true }).first().click();
  await expect(panel).toContainText('Bank confirmation is blocked');
  await panel.getByRole('button', { name: 'Retry failed requests' }).click();
  await expect.poll(() => mock.requests.some(row => row.body.action === 'retryFailed')).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('processed responses awaiting current-attempt validation stay pending and can be inspected separately', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date, 'Downloading');
    view.job.attempt = 2; view.job.preparingData = true; view.job.total = 2; view.job.finished = 1;
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.route('**/data-health', route => route.fulfill({ json: { jobId: 'job-2026-10-16', attempt: 2,
    total: 2, processed: 1, complete: 0, sparse: 0, empty: 0, failed: 0, invalid: 0, pending: 2, cached: 0, requiredAffected: 0,
    readiness: 'Acquisition in progress — evidence awaiting validation', warnings: [], qualification: null,
    instruments: [{ id: 'OPTION-A', role: 'Call', state: 'AwaitingValidation', rows: 21, expectedMinutes: null, missingMinutes: null,
      cached: false, error: null, impact: 'Coverage awaits normalization.', issues: [], missingIntervals: [], recoveryAction: 'Wait for matching attempt normalization.' },
    { id: 'OPTION-B', role: 'Put', state: 'Waiting', rows: 0, expectedMinutes: null, missingMinutes: null,
      cached: false, error: null, impact: 'Response pending.', issues: [], missingIntervals: [] }] } }));
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await page.getByRole('button', { name: 'Inspect data health' }).click();
  const panel = page.getByRole('region', { name: 'Dataset health details' });
  await panel.getByLabel('Coverage filter').selectOption('AwaitingValidation');
  await expect(panel.locator('tbody tr')).toHaveCount(1);
  await expect(panel.locator('tbody')).toContainText('OPTION-A');
  await expect(panel.locator('tbody')).toContainText('21 / not validated');
  await expect(page.getByLabel('Replay readiness')).toContainText('Evidence awaiting validation');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('incomplete decision evidence is prominent and gap refresh requests a new source revision', async ({ page }) => {
  const mock = await mockReplay(page);
  currentQualification = { qualified: false, decisionTimes: 4, incompleteDecisionTimes: 4, maximumUnknownOptions: 1, firstIncompleteAtUtc: '2026-10-16T04:00:00Z',
    issues: ['SELECTION_EVIDENCE_INCOMPLETE', 'WEIGHTS_PUBLICATION_UNQUALIFIED'] };
  await page.route('**/data-health', route => route.fulfill({ json: { jobId: 'job-2026-10-16', attempt: 1, total: 2, processed: 2, complete: 1, sparse: 0, empty: 1, failed: 0, invalid: 0, pending: 0, cached: 2, requiredAffected: 0,
    readiness: 'Incomplete — diagnostic replay only', warnings: [],
    qualification: { qualified: false, decisionTimes: 4, incompleteDecisionTimes: 4, maximumUnknownOptions: 1, firstIncompleteAtUtc: '2026-10-16T04:00:00Z', issues: ['SELECTION_EVIDENCE_INCOMPLETE', 'WEIGHTS_PUBLICATION_UNQUALIFIED'] },
    instruments: [{ id: 'OPTION-A', role: 'Call', state: 'Unavailable', rows: 0, expectedMinutes: 375, missingMinutes: 375, cached: true, error: 'EMPTY_RESPONSE',
      impact: 'Selection evidence is incomplete.', recoveryAction: 'Refresh coverage gaps; the source may remain unavailable.', issues: [], missingIntervals: [] }] } }));
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await page.getByRole('button', { name: 'Inspect data health' }).click();
  const panel = page.getByRole('region', { name: 'Dataset health details' });
  await expect(panel).toContainText('Not qualified for strategy-performance conclusions');
  await expect(panel).toContainText('4 of 4 decision times incomplete');
  await expect(panel).toContainText('WEIGHTS_PUBLICATION_UNQUALIFIED');
  await panel.getByRole('button', { name: 'Refresh coverage gaps', exact: true }).click();
  await expect.poll(() => mock.requests.some(row => row.body.action === 'refreshGaps')).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('completed screenshot state explains unresolved evidence without calling it pending', async ({ page }) => {
  const mock = await mockReplay(page);
  currentQualification = { qualified: false, decisionTimes: 120, incompleteDecisionTimes: 120, maximumUnknownOptions: 615, firstIncompleteAtUtc: '2026-10-14T04:00:00Z',
    issues: ['CALENDAR_UNVERIFIED', 'CALENDAR_PUBLICATION_UNQUALIFIED', 'WEIGHTS_PUBLICATION_UNQUALIFIED', 'SELECTION_EVIDENCE_INCOMPLETE'] };
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date);
    view.complete = true; view.job.state = 'Completed'; view.job.reportsAvailable = true;
    view.job.message = 'Replay completed. 1 incomplete experiments are disclosed in the report.';
    view.job.total = view.job.finished = 965;
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.route('**/data-health', route => route.fulfill({ json: { jobId: 'job-2026-10-14', attempt: 1, total: 965, processed: 965, complete: 211, sparse: 259, empty: 495, failed: 0, invalid: 0, pending: 0, cached: 965, requiredAffected: 0,
    readiness: 'Incomplete — diagnostic replay only', warnings: [], instruments: [],
    qualification: { qualified: false, decisionTimes: 120, incompleteDecisionTimes: 120, maximumUnknownOptions: 615, firstIncompleteAtUtc: '2026-10-14T04:00:00Z',
      issues: ['CALENDAR_UNVERIFIED', 'CALENDAR_PUBLICATION_UNQUALIFIED', 'WEIGHTS_PUBLICATION_UNQUALIFIED', 'SELECTION_EVIDENCE_INCOMPLETE'] } } }));
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-14');
  const preparation = page.getByRole('region', { name: 'Historical replay preparation' });
  await expect(preparation).toContainText('Playback completed — evidence incomplete');
  await expect(preparation.getByLabel('Replay readiness')).toContainText('3 · Evidence incomplete');
  await expect(preparation.getByLabel('Replay readiness')).not.toContainText('pending');
  await expect(preparation).toContainText('Exchange calendar is unverified');
  await expect(preparation).toContainText('Bank-weight publication time is unproven');
  await expect(preparation).toContainText('Option-selection evidence is incomplete');
  await expect(preparation).toContainText('615');
  await preparation.getByRole('button', { name: 'Inspect data health' }).click();
  await expect(page.getByRole('region', { name: 'Dataset health details' })).toContainText('A candle refresh does not repair reference-data evidence');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('a failed data-health refresh hides the stale health rows but keeps the server verdict', async ({ page }) => {
  await mockReplay(page); let failed = false;
  currentQualification = { qualified: true, decisionTimes: 4, incompleteDecisionTimes: 0, maximumUnknownOptions: 0, firstIncompleteAtUtc: null, issues: [] };
  await page.route('**/data-health', route => route.fulfill(failed ? { status: 503, json: { detail: 'Data-health storage unavailable' } } : { json: {
    jobId: 'job-2026-10-16', attempt: 1, total: 1, processed: 1, complete: 1, sparse: 0, empty: 0, failed: 0, invalid: 0, pending: 0, cached: 1, requiredAffected: 0, readiness: 'Qualified historical inputs — candle model only', warnings: [], instruments: [],
    qualification: { qualified: true, decisionTimes: 4, incompleteDecisionTimes: 0, maximumUnknownOptions: 0, firstIncompleteAtUtc: null, issues: [] } } }));
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await expect(page.getByLabel('Replay readiness')).toContainText('3 · Evidence qualified');
  await page.getByRole('button', { name: 'Inspect data health' }).click();
  failed = true; const panel = page.getByRole('region', { name: 'Dataset health details' });
  await panel.getByRole('button', { name: 'Refresh data health', exact: true }).click();
  await expect(panel).toContainText('Data-health storage unavailable');
  await expect(panel).not.toContainText('Historical input evidence qualified');
  // Only the detail rows failed; the verdict is the server's and is unchanged.
  await expect(page.getByLabel('Replay readiness')).toContainText('3 · Evidence qualified');
});

test('a previous acquisition health response cannot qualify a newer attempt', async ({ page }) => {
  const mock = await mockReplay(page);
  currentQualification = { qualified: true, decisionTimes: 4, incompleteDecisionTimes: 0, maximumUnknownOptions: 0, firstIncompleteAtUtc: null, issues: [] };
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date); view.job.attempt = 1;
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.route('**/data-health', route => route.fulfill({ json: {
    jobId: 'job-2026-10-16', attempt: 1, total: 1, processed: 1, complete: 0, sparse: 1, empty: 0, failed: 0, invalid: 0, pending: 0, cached: 1, requiredAffected: 0, readiness: 'Qualified historical inputs — candle model only', warnings: [], instruments: [],
    qualification: { qualified: true, decisionTimes: 4, incompleteDecisionTimes: 0, maximumUnknownOptions: 0, firstIncompleteAtUtc: null, issues: [] } } }));
  await page.route('**/commands', async route => {
    const view = mock.views.get('job-2026-10-16')!;
    view.job.attempt = 2; view.job.state = 'Downloading'; view.job.preparationComplete = false; view.job.canReplay = false;
    await route.fulfill({ json: view });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await expect(page.getByLabel('Replay readiness')).toContainText('3 · Evidence qualified');
  await page.getByRole('button', { name: 'Inspect data health' }).click();
  const panel = page.getByRole('region', { name: 'Dataset health details' });
  await panel.getByRole('button', { name: 'Refresh coverage gaps', exact: true }).click();
  const view = mock.views.get('job-2026-10-16')!;
  currentQualification = null; // the server has not qualified the second attempt
  view.job.state = 'ReadyWithWarnings'; view.job.preparationComplete = true; view.job.canReplay = true;
  await expect(page.getByRole('region', { name: 'Historical replay preparation' })).toContainText('ReadyWithWarnings');
  await expect(page.getByLabel('Replay readiness')).toContainText('3 · Evidence awaiting validation');
  await expect(panel).not.toContainText('Historical input evidence qualified');
});

test('server restart resets revision ordering and late retired-server responses cannot replace recovery', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date);
    view.revision = 100; view.serverInstanceId = 'old-server';
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  let release: () => void = () => {}; let entered: () => void = () => {};
  const held = new Promise<void>(resolve => { release = resolve; }); const reached = new Promise<void>(resolve => { entered = resolve; });
  await page.route('**/commands', async route => {
    const old = structuredClone(mock.views.get('job-2026-10-16')!);
    old.revision = 101; old.job.message = 'Late retired-server response';
    entered(); await held; await route.fulfill({ json: old });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await page.getByRole('button', { name: 'Step', exact: true }).click(); await reached;
  const current = mock.views.get('job-2026-10-16')!;
  current.revision = 1; current.serverInstanceId = 'new-server'; current.job.message = 'Recovered after API restart';
  const preparation = page.getByRole('region', { name: 'Historical replay preparation' });
  try { await expect(preparation).toContainText('Recovered after API restart'); }
  finally { release(); }
  await expect(page.getByRole('button', { name: 'Step', exact: true })).toBeEnabled();
  await expect(preparation).not.toContainText('Late retired-server response');
});

test('full run journal browses older pages and loads detailed gates without losing the inspected page to polling', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date); view.paperRunId = 'journal-run'; mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.route('**/events?*', route => {
    const url = new URL(route.request().url()); const end = Number(url.searchParams.get('before') ?? 301);
    return route.fulfill({ json: { runId: 'journal-run', rows: Array.from({ length: 25 }, (_, i) => ({ sequence: end - i - 1, runId: 'journal-run', timeUtc: '2026-10-16T04:00:00Z', indexId: 'BANKNIFTY', contractId: `OPTION-${end - i - 1}`, category: 'CANDLE_RESEARCH', action: 'REJECTED', setupId: 'setup-1', reasonsJson: '["SPREAD_GATE"]' })), nextBeforeSequence: end - 25 } });
  });
  await page.route('**/events/*?*', route => route.fulfill({ json: { contractId: 'OPTION-275', class: 'CANDLE_RESEARCH', action: 'REJECTED', reasons: ['SPREAD_GATE'], gates: [{ code: 'SPREAD_GATE', outcome: 'Fail', detail: 'Observed spread exceeds configured threshold', observed: 15, threshold: 10 }] } }));
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  await page.getByRole('tab', { name: 'Decisions', exact: true }).click(); await page.getByRole('button', { name: 'Full run journal', exact: true }).click();
  const journal = page.getByRole('region', { name: 'Full run decision journal' });
  await expect(journal.locator('tbody tr')).toHaveCount(25);
  await journal.getByRole('button', { name: 'Older events' }).click(); await expect(journal).toContainText('Page 2');
  await journal.getByRole('button', { name: 'Inspect event #275', exact: true }).click();
  await expect(journal).toContainText('Observed spread exceeds configured threshold');
  await page.waitForTimeout(1800); await expect(journal).toContainText('Page 2');
  await expect(journal.getByRole('link', { name: 'Download complete run evidence (.jsonl.gz)' })).toHaveAttribute('href', /runId=journal-run/);
});

test('run evidence exposes operational errors, downloads and same-dataset decision comparison', async ({ page }) => {
  const mock = await mockReplay(page);
  const baseline = { runId: 'baseline', jobId: 'job-2026-10-16', date: '2026-10-16', preset: 'base', datasetHash: 'same-data', inputHash: 'policy-A', simulatedTimeUtc: '2026-10-16T10:00:00Z', realizedNetPnl: -300, trades: 2, complete: true };
  const variant = { ...baseline, runId: 'variant', inputHash: 'policy-B', realizedNetPnl: -100 };
  await page.route('**/api/replay/jobs', async route => { const body = route.request().postDataJSON(); const view = initial(body.date); view.complete = true; view.paperRunId = 'baseline'; mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job }); });
  await page.route('**/diagnostics', route => route.fulfill({ json: { runs: [baseline, variant], operations: [{ sequence: 1, run: 1, preset: 'base', wallTimeUtc: '2026-10-17T05:00:00Z', simulatedTimeUtc: null, severity: 'Error', code: 'PROVIDER_UNAVAILABLE', detailJson: '{"instrument":"OPTION-A"}' }] } }));
  await page.route('**/api/replay/runs?*', route => route.fulfill({ json: [baseline, variant] }));
  await page.route('**/compare?*', route => route.fulfill({ json: { left: baseline, right: variant, configurationChanged: true, hasMore: false, differences: [{ timeUtc: '2026-10-16T04:00:00Z', indexId: 'BANKNIFTY', contractId: 'OPTION-A', category: 'CANDLE_RESEARCH', leftAction: 'REJECTED', rightAction: 'HYPOTHETICAL_ELIGIBLE', leftReasons: '["RISK_LIMIT"]', rightReasons: '["HYPOTHETICAL_ELIGIBLE"]' }] } }));
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16'); await page.getByRole('tab', { name: 'Run evidence', exact: true }).click();
  const logs = page.getByRole('region', { name: 'Run diagnostics' }); await expect(logs).toContainText('PROVIDER_UNAVAILABLE');
  await expect(logs.getByRole('link', { name: 'Download evidence' }).first()).toHaveAttribute('href', /runId=baseline/);
  const comparison = page.getByRole('region', { name: 'Compare replay runs' }); await comparison.getByLabel('Comparison run').selectOption('variant');
  await comparison.getByRole('button', { name: 'Compare runs', exact: true }).click();
  await expect(comparison).toContainText('1 action/reason differences'); await expect(comparison).toContainText('RISK_LIMIT');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('chart zoom survives polling instead of being recreated on each snapshot', async ({ page }) => {
  const mock = await mockReplay(page);
  await page.route('**/api/replay/jobs', async route => {
    const body = route.request().postDataJSON(); const view = initial(body.date); view.simulatedTimeUtc = `${body.date}T04:15:00Z`;
    view.bars = Array.from({ length: 30 }, (_, i) => { const start = Date.parse(`${body.date}T03:45:00Z`) + i * 60000; return { id: `bar-${i}`, startUtc: new Date(start).toISOString(), endUtc: new Date(start + 60000).toISOString(), availableAtUtc: new Date(start + 60000).toISOString(), open: 55000 + i, high: 55020 + i, low: 54990 + i, close: 55010 + i }; });
    mock.views.set(view.job.id, view); await route.fulfill({ status: 202, json: view.job });
  });
  await page.goto('/#replay'); await page.getByLabel('Replay date').fill('2026-10-16');
  const chart = page.getByRole('img', { name: 'Historical completed candles available at replay time' }); await chart.scrollIntoViewIfNeeded();
  await chart.hover(); await page.mouse.wheel(0, -500); await page.mouse.move(0, 0); await page.waitForTimeout(300);
  const zoomed = await chart.screenshot(); await page.waitForTimeout(1800);
  expect((await chart.screenshot()).equals(zoomed)).toBe(true);
});
