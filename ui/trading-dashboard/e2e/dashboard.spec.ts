import { expect, test } from '@playwright/test';

test('user can inspect the synthetic cockpit without confusing API health with market freshness', async ({ page }, testInfo) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Bank Nifty cockpit' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'API connectivity' }).getByText('Connected', { exact: true })).toBeVisible();
  await expect(page.getByText('SYNTHETIC TEST', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('region', { name: 'Futures context' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('cockpit.png'), fullPage: true });
});

test('user saves a whole-lot plain-text plan with actual 201 and reloads its original journal evidence', async ({ page }, testInfo) => {
  const note = `<b>Keep as plain text</b> · ${testInfo.project.name} · ${Date.now()}`;
  await page.goto('/');
  await page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / }).first().click();
  const plan = page.getByRole('region', { name: 'Option plan' });
  await plan.getByLabel('Whole lots').fill('2');
  await expect(plan.getByText('50 units', { exact: true })).toBeVisible();
  await plan.getByLabel('Plan status').selectOption('planned');
  await plan.getByLabel('Thesis', { exact: true }).fill('Observe breadth before considering a trade');
  await plan.getByLabel('Stop plan', { exact: true }).fill('Review only, no executable stop');
  await plan.getByLabel('Invalidation', { exact: true }).fill('Missing bank evidence');
  await plan.getByLabel('Note', { exact: true }).fill(note);
  const saved = page.waitForResponse(r => r.url().endsWith('/api/journal/plans') && r.request().method() === 'POST');
  await plan.getByRole('button', { name: 'Save plan', exact: true }).click();
  expect((await saved).status()).toBe(201);
  await expect(plan.getByRole('status')).toContainText('Saved to journal');
  await page.getByLabel('Fixture scenario').selectOption('feed-loss');
  await page.getByRole('link', { name: 'Journal', exact: true }).click();
  await page.reload();
  const entry = page.getByRole('article').filter({ hasText: note });
  await expect(entry).toContainText('50 units');
  await expect(entry.getByText(note, { exact: true })).toBeVisible();
  await entry.getByRole('button', { name: 'Inspect saved evidence' }).click();
  const evidence = page.getByRole('region', { name: 'Original saved evidence' });
  await expect(evidence).toContainText('synthetic-dashboard-v2:BANKNIFTY:illustrative-v1:normal');
  await expect(evidence).toContainText('SYNTHETIC_FIXTURE');
  await expect(evidence).toContainText('synthetic-dashboard-v2');
  await page.screenshot({ path: testInfo.outputPath('journal.png'), fullPage: true });
});

test('user inspects completed futures history, disclosed bank coverage and synthetic options', async ({ page }, testInfo) => {
  await page.goto('/');
  await expect(page.getByRole('region', { name: 'Futures context' })).toBeVisible();
  await expect(page.getByText('Fixed fixture clock', { exact: true })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Bank participation' })).toContainText('Missing weight');
  await expect(page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / })).toHaveCount(24);
  const chart = page.getByRole('region', { name: 'Futures context' });
  await chart.getByRole('button', { name: 'Zoom in chart' }).click();
  await chart.getByRole('button', { name: 'Pan chart earlier' }).click();
  await chart.getByRole('button', { name: 'Reset chart' }).click();
  await chart.getByRole('img', { name: 'Interactive completed one-minute futures candlestick chart' }).hover({ position: { x: 100, y: 120 } });
  await expect(chart.getByText(/^Hover · .* IST$/)).toBeVisible();
  await page.mouse.move(0, 0);
  await chart.getByText('Accessible chart summary', { exact: true }).click();
  await expect(chart).toContainText('60 completed one-minute bars');
  await expect(chart).toContainText('12 Oct 2026, 10:15:05 IST');
  await expect(page.getByRole('link', { name: 'TradingView Lightweight Charts™' })).toHaveAttribute('href', 'https://www.tradingview.com/');
  await chart.getByText('Accessible chart summary', { exact: true }).click();
  await page.screenshot({ path: testInfo.outputPath('snapshot.png'), fullPage: true });
});

test('keyboard selection focuses the plan and closing returns to the selected contract', async ({ page }) => {
  await page.goto('/');
  const option = page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / }).first();
  await option.focus();
  await option.press('Enter');
  const plan = page.getByRole('region', { name: 'Option plan' });
  await expect(plan.getByRole('heading', { name: 'Option plan' })).toBeFocused();
  await plan.getByRole('button', { name: 'Close option plan' }).click();
  await expect(option).toBeFocused();
  await page.getByRole('link', { name: 'Journal', exact: true }).click();
  const skip = page.getByRole('link', { name: 'Skip to workspace' });
  await skip.focus();
  await skip.press('Enter');
  await expect(page.getByRole('main')).toBeFocused();
  await expect(page.getByRole('heading', { name: 'Decision journal', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Journal', exact: true })).toHaveAttribute('aria-current', 'page');
});

test('narrow mobile prioritizes the position summary without page horizontal overflow', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mobile');
  await page.goto('/');
  await page.getByLabel('Fixture scenario').selectOption('open-position');
  const position = page.getByRole('region', { name: 'Preset position' });
  await expect(position).toContainText('Gross indicative');
  const chart = page.getByRole('region', { name: 'Futures context' });
  const positionBox = await position.boundingBox();
  const chartBox = await chart.boundingBox();
  expect(positionBox!.y).toBeLessThan(chartBox!.y);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('mobile-summary.png'), fullPage: true });
});

test('user freezes two observations while scenario and selected contract continue to update', async ({ page }, testInfo) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  const options = page.getByRole('region', { name: 'Option candidates' });
  await options.getByRole('button', { name: /^Select / }).nth(0).click();
  const plan = page.getByRole('region', { name: 'Option plan' });
  await plan.getByRole('button', { name: 'Pin for comparison' }).click();
  await options.getByRole('button', { name: /^Select / }).nth(1).click();
  await plan.getByRole('button', { name: 'Pin for comparison' }).click();
  const compare = page.getByRole('region', { name: 'Option comparison' });
  await options.getByRole('button', { name: /^Select / }).nth(2).click();
  await expect(plan.getByRole('button', { name: 'Pin for comparison' })).toBeDisabled();
  await options.getByRole('button', { name: /^Select / }).nth(1).click();
  await compare.getByRole('button', { name: 'Freeze comparison' }).click();
  await page.getByLabel('Fixture scenario').selectOption('stale-quotes');
  await expect(page.getByRole('region', { name: 'Fixture freshness' })).toContainText('stale');
  await expect(compare).toContainText('Frozen evidence');
  await expect(compare).toContainText('synthetic-dashboard-v2:BANKNIFTY:illustrative-v1:normal');
  await expect(compare.getByText('● Fresh fixture', { exact: true })).toHaveCount(2);
  await expect(plan).toContainText('SYNTH-BANKNIFTY-2026-10-29-51500-PE');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('frozen-comparison-plan.png'), fullPage: true });
  await compare.getByRole('button', { name: 'Unfreeze comparison' }).click();
  await expect(compare).toContainText('synthetic-dashboard-v2:BANKNIFTY:illustrative-v1:stale-quotes');
  await expect(compare.getByText('● stale', { exact: true })).toHaveCount(2);
  expect(errors).toEqual([]);
});

test('scenario and journal read failures expose retry without pretending cached results are current', async ({ page }) => {
  await page.route('**/api/dashboard/scenarios', route => route.fulfill({ status: 200, json: [{ id: 'normal', label: 'Normal' }] }));
  await page.goto('/');
  await expect(page.getByRole('alert')).toContainText('Scenarios unavailable');
  await expect(page.getByLabel('Fixture scenario')).toBeDisabled();
  await page.unroute('**/api/dashboard/scenarios');
  await page.getByRole('button', { name: 'Retry snapshot' }).click();
  await expect(page.getByLabel('Fixture scenario')).toBeEnabled();
  await page.getByRole('link', { name: 'Journal', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Decision journal', exact: true })).toBeVisible();
  await page.route('**/api/journal/plans', route => route.abort('connectionrefused'));
  await page.getByRole('button', { name: 'Refresh journal' }).click();
  await expect(page.getByRole('heading', { name: 'Journal unavailable' })).toBeVisible();
  await expect(page.getByRole('article')).toHaveCount(0);
  await page.unroute('**/api/journal/plans');
  await page.getByRole('button', { name: 'Retry journal' }).click();
  await expect(page.getByRole('heading', { name: 'Journal unavailable' })).toHaveCount(0);
});

test('obsolete snapshot responses cannot overwrite a newer scenario', async ({ page }) => {
  let release: () => void = () => {};
  const gate = new Promise<void>(resolve => { release = resolve; });
  let held = false;
  await page.route('**/api/dashboard/snapshot?indexId=BANKNIFTY&scenarioId=normal', async route => {
    const response = await route.fetch();
    held = true;
    await gate;
    await route.fulfill({ response }).catch(() => {});
  });
  await page.goto('/');
  await expect(page.getByLabel('Fixture scenario')).toBeEnabled();
  await expect.poll(() => held).toBe(true);
  await page.getByLabel('Fixture scenario').selectOption('feed-loss');
  const freshness = page.getByRole('region', { name: 'Fixture freshness' });
  await expect(freshness).toContainText('disconnected');
  release();
  await expect(page.getByRole('region', { name: 'Market summary' })).toContainText('synthetic-dashboard-v2:BANKNIFTY:illustrative-v1:feed-loss');
  await expect(freshness).toContainText('disconnected');
});

test('missing, stale and feed-loss observations remain separate from verified API health', async ({ page }) => {
  await page.goto('/');
  const api = page.getByRole('region', { name: 'API connectivity' });
  await expect(api.getByText('Connected', { exact: true })).toBeVisible();
  await page.getByLabel('Fixture scenario').selectOption('missing-inputs');
  const banks = page.getByRole('region', { name: 'Bank participation' });
  await expect(banks).toContainText('Missing weight');
  await expect(banks).toContainText('30.00%');
  await page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / }).first().click();
  await expect(page.getByRole('region', { name: 'Option plan' })).toContainText('Unavailable');
  for (const scenario of ['stale-quotes', 'feed-loss']) {
    await page.getByLabel('Fixture scenario').selectOption(scenario);
    const freshness = page.getByRole('region', { name: 'Fixture freshness' });
    await expect(freshness).toContainText(scenario === 'feed-loss' ? 'disconnected' : 'stale');
    const position = page.getByRole('region', { name: 'Preset position' });
    await expect(position).toContainText('unpriced');
    await expect(position.getByRole('button', { name: 'Paper entry', exact: true })).toBeDisabled();
    await expect(position.getByRole('button', { name: 'Paper exit', exact: true })).toBeDisabled();
    await expect(position.getByRole('button', { name: 'Enable paper-auto' })).toBeDisabled();
    await expect(api.getByText('Connected', { exact: true })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Futures context' })).toContainText('fixture history');
  }
});

for (const failure of ['disconnect', 'unexpected HTTP 200'] as const) {
  test(`health ${failure} removes connected status and recovers through the genuine API`, async ({ page }) => {
    await page.goto('/');
    const api = page.getByRole('region', { name: 'API connectivity' });
    await expect(api.getByText('Connected', { exact: true })).toBeVisible();
    await page.route('**/api/health', route => failure === 'disconnect' ? route.abort('connectionrefused') : route.fulfill({ status: 200, json: { status: 'ok' } }));
    await page.getByRole('button', { name: 'Refresh connection' }).click();
    await expect(api.getByText('Unavailable', { exact: true })).toBeVisible();
    await expect(api.getByText('Connected', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Fixture freshness' })).toContainText('Fixed fixture clock');
    await page.unroute('**/api/health');
    const retry = page.getByRole('button', { name: 'Retry connection' });
    await retry.focus();
    await retry.press('Enter');
    await expect(api.getByText('Connected', { exact: true })).toBeVisible();
  });
}

for (const failure of ['disconnect', 'malformed snapshot', 'wrong scenario'] as const) {
  test(`snapshot ${failure} clears current observations and preserves draft for genuine retry`, async ({ page }) => {
    await page.goto('/');
    await page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / }).first().click();
    const plan = page.getByRole('region', { name: 'Option plan' });
    await plan.getByLabel('Note', { exact: true }).fill('Keep this draft through recovery');
    await page.route('**/api/dashboard/snapshot?*', async route => {
      if (failure === 'disconnect') return route.abort('connectionrefused');
      const response = await route.fetch();
      const json = await response.json();
      if (failure === 'malformed snapshot') json.options[0].quote.bid = 'not a price';
      else json.scenarioId = 'feed-loss';
      await route.fulfill({ response, json });
    });
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByRole('heading', { name: 'Snapshot unavailable' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Fixture freshness' })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Futures context' })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Option candidates' })).toHaveCount(0);
    await expect(plan.getByRole('button', { name: 'Save plan', exact: true })).toBeDisabled();
    await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Keep this draft through recovery');
    await expect(page.getByRole('region', { name: 'API connectivity' }).getByText('Connected', { exact: true })).toBeVisible();
    await page.unroute('**/api/dashboard/snapshot?*');
    await page.getByRole('button', { name: 'Retry snapshot' }).click();
    await expect(page.getByRole('region', { name: 'Option candidates' })).toBeVisible();
    await expect(plan.getByRole('button', { name: 'Save plan', exact: true })).toBeEnabled();
    await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Keep this draft through recovery');
  });
}

test('failed save keeps edits, prevents overlap and retries only with server confirmation', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / }).first().click();
  const plan = page.getByRole('region', { name: 'Option plan' });
  await plan.getByLabel('Note', { exact: true }).fill('Retain after save failure');
  await expect(plan.getByRole('button', { name: 'Save plan', exact: true })).toBeEnabled();
  let release: () => void = () => {};
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/journal/plans', async route => {
    await gate;
    await route.fulfill({ status: 500, json: { detail: 'Injected journal failure' } });
  });
  await plan.getByRole('button', { name: 'Save plan', exact: true }).click();
  await expect(plan.getByRole('button', { name: 'Save plan', exact: true })).toBeDisabled();
  await expect(page.getByLabel('Fixture scenario')).toBeDisabled();
  await expect(page.getByLabel('Cockpit index', { exact: true })).toBeDisabled();
  await expect(page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / }).first()).toBeDisabled();
  release();
  await expect(plan.getByRole('alert')).toContainText('HTTP 500');
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Retain after save failure');
  await page.unroute('**/api/journal/plans');
  await page.route('**/api/journal/plans', route => route.fulfill({ status: 200, json: {} }));
  await plan.getByRole('button', { name: 'Retry save plan' }).click();
  await expect(plan.getByRole('alert')).toContainText('HTTP 200; expected 201');
  await expect(plan.getByText(/Saved to journal/)).toHaveCount(0);
  await page.unroute('**/api/journal/plans');
  const saved = page.waitForResponse(r => r.url().endsWith('/api/journal/plans') && r.request().method() === 'POST');
  await plan.getByRole('button', { name: 'Retry save plan' }).click();
  expect((await saved).status()).toBe(201);
  await expect(plan.getByRole('status')).toContainText('Saved to journal');
});

test('changing contracts and scenarios preserves per-contract drafts and reset requires confirmation', async ({ page }) => {
  await page.goto('/');
  const options = page.getByRole('region', { name: 'Option candidates' });
  await options.getByRole('button', { name: /^Select / }).first().click();
  const plan = page.getByRole('region', { name: 'Option plan' });
  await plan.getByLabel('Note', { exact: true }).fill('Draft A');
  await options.getByRole('button', { name: /^Select / }).nth(1).click();
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('');
  await plan.getByLabel('Note', { exact: true }).fill('Draft B');
  await page.getByLabel('Fixture scenario').selectOption('missing-inputs');
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Draft B');
  await options.getByRole('button', { name: /^Select / }).first().click();
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Draft A');
  await plan.getByRole('button', { name: 'Reset draft' }).click();
  await plan.getByRole('button', { name: 'Keep editing' }).click();
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Draft A');
  await plan.getByRole('button', { name: 'Reset draft' }).click();
  await plan.getByRole('button', { name: 'Discard draft edits' }).click();
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('');
  await expect(plan.getByLabel('Whole lots')).toHaveValue('1');
  await plan.getByLabel('Whole lots').fill('1.5');
  await expect(plan.getByRole('button', { name: 'Save plan', exact: true })).toBeDisabled();
  await expect(plan).toContainText('Enter positive integer whole lots');
});

test('obsolete preview responses cannot replace units for the latest selection', async ({ page }) => {
  let release: () => void = () => {};
  const gate = new Promise<void>(resolve => { release = resolve; });
  let held = false;
  await page.route('**/api/journal/plans/preview', async route => {
    const response = await route.fetch();
    if (!held) { held = true; await gate; }
    await route.fulfill({ response }).catch(() => {}); // obsolete request can already be aborted
  });
  await page.goto('/');
  await page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / }).first().click();
  const plan = page.getByRole('region', { name: 'Option plan' });
  await expect(plan).toContainText('Loading server preview');
  await expect.poll(() => held).toBe(true);
  await plan.getByLabel('Whole lots').fill('2');
  await expect(plan.getByText('50 units', { exact: true })).toBeVisible();
  release();
  await expect(plan.getByText('25 units', { exact: true })).toHaveCount(0);
  await expect(plan.getByText('50 units', { exact: true })).toBeVisible();
});
