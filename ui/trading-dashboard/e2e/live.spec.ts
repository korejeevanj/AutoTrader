import { expect, test } from '@playwright/test';

test('live paper controls distinguish pending intents, fills, pending exits, journal and quote replay', async ({ page }) => {
  let phase = 'ready';
  let sequence = 10;
  const contract = { id: 'NFO:TESTCALL', instrumentToken: 258, role: 'Call', expiry: '2026-10-27', lotSize: 30, tickSize: 0.05 };
  const approval = { id: 'manual-test', origin: 'MANUAL_TEST', contract, acceptedAtUtc: '2026-10-21T04:30:00Z', reservedCash: 3020 };
  const trade = { id: 'manual-test', contractId: contract.id, origin: 'MANUAL_TEST', entryPrice: 100.55, exitPrice: 101.95, grossPnl: 42, netPnl: 40, exitReason: 'MANUAL_EXIT', grossStopOverrun: 0, entryFees: { total: 1 }, exitFees: { total: 1 } };
  const paper = () => ({ ledger: { cash: phase === 'closed' ? 500040 : 500000, paidFees: phase === 'closed' ? 2 : 0, realizedNetPnl: phase === 'closed' ? 40 : 0 },
    pending: phase === 'pending' ? approval : null,
    position: phase === 'open' || phase === 'exit-pending' ? { approval, entryPrice: 100.55, enteredAtUtc: '2026-10-21T04:30:00.300Z', units: 30, exitReason: phase === 'exit-pending' ? 'MANUAL_EXIT' : null, exitRequestedAtUtc: phase === 'exit-pending' ? '2026-10-21T04:30:01Z' : null } : null,
    trades: phase === 'closed' ? [trade] : [], events: [] });
  const view = () => ({ mode: 'PAPER + LIVE DATA', status: 'Connected', errorCode: null, credentialStatus: 'Validated', runId: 'recording-1', tradingDate: '2026-10-21', asOfUtc: '2026-10-21T04:30:00Z',
    connected: true, autoEnabled: false, recordingHealthy: true, recordingIncomplete: false, paperPersistenceHealthy: true, paperPersistencePending: false, receivedSequence: sequence, durableSequence: sequence, recorderQueueDepth: 0, recorderLagMilliseconds: 0,
    issues: [], instruments: [contract], universe: [contract.id], contextGates: [], directional: null, futuresBars: [], quotes: [{ instrumentToken: 258, lastPrice: 100.25, exchangeTimeUtc: '2026-10-21T04:30:00Z', lastTradeTimeUtc: '2026-10-21T04:29:59Z', receivedAtUtc: '2026-10-21T04:30:00.100Z', bids: [{ price: 100, quantity: 30 }], asks: [{ price: 100.5, quantity: 30 }] }],
    quoteFreshness: [{ instrumentToken: 258, state: 'fresh', quoteAgeSeconds: 0.1, receiptAgeSeconds: 0, lastTradeAgeSeconds: 1.1 }],
    candidates: [{ contractId: contract.id, action: 'WATCH', rank: null, reasonCodes: ['NO_ACTIVE_SETUP'], gates: [], financialEvidence: null }], paper: paper(), sessionNet: phase === 'closed' ? 40 : 0, positionDecision: null });
  await page.route('**/api/live/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/commands')) {
      const command = route.request().postDataJSON();
      if (command.command === 'manual') { phase = 'pending'; setTimeout(() => { phase = 'open'; sequence++; }, 1000); }
      if (command.command === 'exit') { phase = 'exit-pending'; setTimeout(() => { phase = 'closed'; sequence++; }, 1000); }
      sequence++;
    }
    if (path.endsWith('/recordings')) return route.fulfill({ json: [{ id: 'recording-1', createdAtUtc: '2026-10-21T03:40:00Z', lastSequence: 20 }] });
    if (path.endsWith('/replays')) return route.fulfill({ json: { id: 'replay-1', sourceRunId: 'recording-1', mode: 'PAPER + REPLAY', limitations: ['RECORDED_UNIVERSE_ONLY'], result: { paper: paper(), recordingIncomplete: false } } });
    return route.fulfill({ json: view() });
  });
  await page.goto('/#live');
  await expect(page.getByRole('heading', { name: 'Live paper trading', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Candidate gates' })).toBeVisible();
  await page.getByLabel('Manual-test contract').selectOption(contract.id);
  await page.getByRole('button', { name: 'Submit MANUAL_TEST' }).click();
  await expect(page.getByText(/Pending entry manual-test/)).toBeVisible();
  await expect(page.getByText(/30 units · Entry/)).toBeVisible();
  await page.getByRole('button', { name: 'Request paper exit' }).click();
  await expect(page.getByText(/EXIT PENDING: MANUAL_EXIT/)).toBeVisible();
  await expect(page.getByRole('cell', { name: '40.00', exact: true })).toBeVisible();
  await expect(page.getByText('No open paper position.')).toBeVisible();
  await page.getByRole('button', { name: 'Load recordings' }).click();
  await page.getByLabel('Source recording').selectOption('recording-1');
  await page.getByRole('button', { name: 'Replay recorded quotes' }).click();
  await expect(page.getByRole('heading', { name: 'PAPER + REPLAY', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('live workspace removes trading observations when status polling fails', async ({ page }) => {
  await page.route('**/api/live/status', route => route.fulfill({ status: 503, json: { detail: 'Feed host unavailable' } }));
  await page.goto('/#live');
  await expect(page.getByRole('alert')).toContainText('Trading state is unknown');
  await expect(page.getByRole('button', { name: 'Enable paper-auto' })).toHaveCount(0);
});

test('real backend synthetic packets pass engine, delayed fills, exit, SQLite and browser journal', async ({ page }) => {
  // No request mocking: this uses the isolated local API and genuine .NET pipeline.
  // The fixture is explicitly synthetic and performs no provider requests.
  await page.goto('/#live');
  await page.getByRole('button', { name: 'Run synthetic quote rehearsal' }).click();
  await expect(page.getByRole('heading', { name: 'SYNTHETIC TEST', exact: true })).toBeVisible();
  await expect(page.getByRole('cell', { name: '27.10', exact: true })).toBeVisible();
  await page.getByText('Replayed candidate decisions', { exact: true }).click();
  await expect(page.getByText('call-100 · ENTER · ENTER_QUALIFIED', { exact: true })).toBeVisible();
  await page.getByText('Intent / fill lifecycle (4)', { exact: true }).click();
  await expect(page.getByText(/ENTRY_FILLED · manual-test/)).toBeVisible();
  await expect(page.getByText(/EXIT_FILLED · manual-test/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Enable paper-auto' })).toBeDisabled();
});
