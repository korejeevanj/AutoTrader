import { expect, test, type Page } from '@playwright/test';
import type { DashboardSnapshot, IndexDefinition } from '../src/model';

type Capability = keyof IndexDefinition['capabilities'];

async function disableCapability(page: Page, capability: Capability | Capability[]) {
  const disabled = Array.isArray(capability) ? capability : [capability];
  await page.route('**/api/dashboard/indices', async route => {
    const response = await route.fetch();
    const indices: IndexDefinition[] = await response.json();
    for (const key of disabled) indices.find(i => i.id === 'BANKNIFTY')!.capabilities[key] = false;
    await route.fulfill({ response, json: indices });
  });
  await page.route('**/api/dashboard/snapshot?indexId=BANKNIFTY&*', async route => {
    const response = await route.fetch();
    const snapshot: DashboardSnapshot = await response.json();
    if (disabled.includes('futuresChart')) {
      snapshot.market.futuresValue = null;
      snapshot.chart.bars = [];
      snapshot.chart.vwap = [];
      snapshot.chart.openingRangeHigh = null;
      snapshot.chart.openingRangeLow = null;
      snapshot.chart.vwapLabel = 'Unavailable: futures chart capability is disabled.';
      snapshot.freshness.message = snapshot.freshness.message.replace('Retained candles are fixture history.', 'Futures chart unavailable for this configured index.');
    }
    if (disabled.includes('optionComparison')) {
      snapshot.options = [];
      snapshot.position = {
        state: 'none', positionId: null, contractId: null, lots: null, units: null,
        entryPrice: null, currentBid: null, grossPnl: null, netPnl: null,
        message: 'Unavailable: option comparison capability is disabled; no preset position.',
      };
    }
    if (disabled.includes('weightedConstituents')) {
      for (const row of [...snapshot.constituents, ...snapshot.banks]) row.weightPercent = null;
      snapshot.participation = {
        aboveVwapWeightPercent: null, belowVwapWeightPercent: null, missingWeightPercent: 100,
        coverageLabel: 'Unavailable: weighted constituent capability is disabled; all weight is missing.',
      };
    }
    await route.fulfill({ response, json: snapshot });
  });
}

test('disabled futures capability shows an unavailable panel without chart actions and retains cash context', async ({ page }) => {
  await page.goto('/');
  const chart = page.getByRole('region', { name: 'Futures context', exact: true });
  await expect(chart.getByRole('img', { name: 'Interactive completed one-minute futures candlestick chart' })).toBeVisible();
  await disableCapability(page, 'futuresChart');
  await page.getByRole('button', { name: 'Refresh snapshot' }).click();
  await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
  await expect(chart).toContainText('Futures chart unavailable');
  await expect(chart.getByRole('img')).toHaveCount(0);
  await expect(chart.getByRole('button')).toHaveCount(0);
  await expect(chart.locator('canvas')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Market summary' })).toContainText('52,140.00');
  await expect(page.getByRole('region', { name: 'Option candidates' }).getByRole('button', { name: /^Select / })).toHaveCount(24);
  await page.unroute('**/api/dashboard/indices');
  await page.unroute('**/api/dashboard/snapshot?indexId=BANKNIFTY&*');
  await page.getByRole('button', { name: 'Refresh snapshot' }).click();
  await expect(chart.getByRole('img', { name: 'Interactive completed one-minute futures candlestick chart' })).toBeVisible();
  await expect(chart.getByRole('button', { name: 'Zoom in chart' })).toBeEnabled();
});

test('disabled weighted capability discloses unavailable metrics and retains constituent price observations', async ({ page }) => {
  await page.goto('/');
  const participation = page.getByRole('region', { name: 'Bank participation', exact: true });
  await expect(participation).toContainText('80.00%');
  await disableCapability(page, 'weightedConstituents');
  await page.getByRole('button', { name: 'Refresh snapshot' }).click();
  await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
  await expect(participation).toContainText('Weighted participation unavailable');
  await expect(participation.getByText('↑ Above VWAP Unavailable', { exact: true })).toBeVisible();
  await expect(participation.getByText('↓ Below Unavailable', { exact: true })).toBeVisible();
  await expect(participation.getByText('Missing weight 100.00%', { exact: true })).toBeVisible();
  await expect(participation.getByRole('row', { name: /Fixture Bank A/ })).toContainText('102.00');
  await expect(participation.getByRole('row', { name: /Fixture Bank A/ })).toContainText('100.00');
  await expect(participation.getByRole('row', { name: /Fixture Bank A/ })).toContainText('Unavailable');
  await expect(participation.getByRole('table')).toBeVisible();
  await expect(participation).not.toContainText('40.00%');
  await expect(page.getByRole('region', { name: 'Option candidates' })).toBeVisible();
  await page.unroute('**/api/dashboard/indices');
  await page.unroute('**/api/dashboard/snapshot?indexId=BANKNIFTY&*');
  await page.getByRole('button', { name: 'Refresh snapshot' }).click();
  await expect(participation).toContainText('80.00%');
  await expect(participation).not.toContainText('Weighted participation unavailable');
});

test('disabled options capability accepts an empty universe and removes frozen comparison and plan actions while retaining drafts', async ({ page }) => {
  await page.goto('/');
  const options = page.getByRole('region', { name: 'Option candidates', exact: true });
  const plan = page.getByRole('region', { name: 'Option plan', exact: true });
  await options.getByRole('button', { name: /^Select / }).first().click();
  await plan.getByLabel('Note', { exact: true }).fill('Retain when capability returns');
  await plan.getByRole('button', { name: 'Pin for comparison' }).click();
  await options.getByRole('button', { name: /^Select / }).nth(1).click();
  await plan.getByRole('button', { name: 'Pin for comparison' }).click();
  const compare = page.getByRole('region', { name: 'Option comparison', exact: true });
  await compare.getByRole('button', { name: 'Freeze comparison', exact: true }).click();
  await disableCapability(page, 'optionComparison');
  await page.getByRole('button', { name: 'Refresh snapshot' }).click();
  await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
  await expect(compare).toContainText('Option comparison and planning unavailable');
  await expect(compare.getByRole('button')).toHaveCount(0);
  await expect(compare.getByRole('article')).toHaveCount(0);
  await expect(options).toHaveCount(0);
  await expect(plan).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^(Select |Pin for comparison|Unpin|Freeze comparison|Unfreeze comparison|Save plan)/ })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Market summary' })).toContainText('52,140.00');
  await expect(page.getByRole('region', { name: 'Futures context', exact: true }).getByRole('button', { name: 'Zoom in chart' })).toBeEnabled();
  await page.unroute('**/api/dashboard/indices');
  await page.unroute('**/api/dashboard/snapshot?indexId=BANKNIFTY&*');
  await page.getByRole('button', { name: 'Refresh snapshot' }).click();
  await options.getByRole('button', { name: /^Select / }).first().click();
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Retain when capability returns');
  await expect(plan.getByRole('button', { name: 'Pin for comparison' })).toBeEnabled();
  await expect(compare.getByRole('article')).toHaveCount(0);
});

test('legacy evidence rejects a contradictory market identity and recovers through the actual journal', async ({ page }) => {
  await page.goto('/#journal');
  const card = page.getByRole('article').filter({ hasText: 'legacy-bank-plan' });
  await expect(card).toBeVisible();
  await page.route('**/api/journal/plans/legacy-bank-plan', async route => {
    const response = await route.fetch();
    const json = await response.json();
    json.snapshot.market.indexName = 'NIFTY (synthetic cash context)';
    await route.fulfill({ response, json });
  });
  await card.getByRole('button', { name: 'Inspect saved evidence' }).click();
  const evidence = page.getByRole('region', { name: 'Original saved evidence' });
  await expect(evidence.getByRole('alert')).toContainText('does not match');
  await expect(evidence.getByText('Legacy Bank Nifty schema-1 fixture.', { exact: false })).toHaveCount(0);
  await expect(evidence.getByRole('region', { name: 'Bank participation', exact: true })).toHaveCount(0);
  await expect(evidence.locator('pre')).toHaveCount(0);
  await page.unroute('**/api/journal/plans/legacy-bank-plan');
  await evidence.getByRole('button', { name: 'Retry evidence' }).click();
  await expect(evidence).toContainText('Legacy Bank Nifty schema-1 fixture');
  await evidence.getByText('Full preserved snapshot / provenance (JSON)', { exact: true }).click();
  expect(JSON.parse(await evidence.locator('pre').innerText()).market.indexName).toBe('BANKNIFTY (synthetic cash context)');
});

test('shared journal heading stays independent of the cockpit and explicitly labels all-index and Nifty filter scopes', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Bank Nifty cockpit', exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Journal', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Shared decision journal');
  await expect(page.getByText('ALL INDICES / PLAN REVIEW', { exact: true })).toBeVisible();
  const scope = page.getByRole('status', { name: 'Journal filter scope' });
  await expect(scope).toContainText('All indices');
  const filter = page.getByLabel('Journal index', { exact: true });
  await expect(filter).toBeEnabled();
  await filter.selectOption('NIFTY');
  await expect(scope).toContainText('Nifty 50 · NIFTY');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Shared decision journal');
  await expect(page.getByRole('heading', { name: 'Bank Nifty cockpit', exact: true })).toHaveCount(0);
  await expect(page.getByText('ALL INDICES / PLAN REVIEW', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Cockpit', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Bank Nifty cockpit');
});

test('all disabled capabilities retain honest cash and constituent observations across every scenario', async ({ page }, testInfo) => {
  await disableCapability(page, ['futuresChart', 'optionComparison', 'weightedConstituents']);
  await page.goto('/');
  for (const scenario of ['normal', 'missing-inputs', 'stale-quotes', 'feed-loss', 'open-position']) {
    await expect(page.getByLabel('Fixture scenario')).toBeEnabled();
    await page.getByLabel('Fixture scenario').selectOption(scenario);
    await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Market summary' })).toContainText('52,140.00');
    await expect(page.getByRole('region', { name: 'Bank participation', exact: true })).toContainText('Weighted participation unavailable');
    await expect(page.getByRole('region', { name: 'Bank participation', exact: true }).getByRole('table')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Preset position', exact: true })).toContainText('none');
    await expect(page.getByRole('region', { name: 'Futures context', exact: true })).toContainText('Futures chart unavailable');
    await expect(page.getByRole('region', { name: 'Option comparison', exact: true })).toContainText('Option comparison and planning unavailable');
    await expect(page.getByRole('img', { name: 'Interactive completed one-minute futures candlestick chart' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^(Zoom |Pan chart|Reset chart|Select |Pin for comparison|Unpin|Freeze comparison|Unfreeze comparison|Save plan)/ })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Option plan', exact: true })).toHaveCount(0);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('disabled-capabilities.png'), fullPage: true });
});

for (const capability of ['futuresChart', 'optionComparison', 'weightedConstituents'] as const) {
  test(`current snapshot rejects evidence contradicting disabled ${capability} catalog metadata`, async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
    await page.route('**/api/dashboard/indices', async route => {
      const response = await route.fetch();
      const indices: IndexDefinition[] = await response.json();
      indices.find(i => i.id === 'BANKNIFTY')!.capabilities[capability] = false;
      await route.fulfill({ response, json: indices });
    });
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByRole('heading', { name: 'Snapshot unavailable' })).toBeVisible();
    await expect(page.getByRole('alert')).toContainText('catalog capabilities');
    await expect(page.getByRole('region', { name: 'Market summary' })).toHaveCount(0);
    await page.unroute('**/api/dashboard/indices');
    await page.getByRole('button', { name: 'Retry snapshot' }).click();
    await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
  });
}

for (const capability of ['futuresChart', 'optionComparison'] as const) {
  test(`enabled ${capability} rejects an invalid empty default fixture and recovers`, async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
    await disableCapability(page, capability);
    await page.unroute('**/api/dashboard/indices'); // Keep the genuine enabled catalog, with contradictory empty evidence.
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByRole('heading', { name: 'Snapshot unavailable' })).toBeVisible();
    await expect(page.getByRole('alert')).toContainText('catalog capabilities');
    await page.unroute('**/api/dashboard/snapshot?indexId=BANKNIFTY&*');
    await page.getByRole('button', { name: 'Retry snapshot' }).click();
    await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
  });
}

test('empty option universe cannot conceal held-position evidence', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
  await disableCapability(page, 'optionComparison');
  await page.unroute('**/api/dashboard/snapshot?indexId=BANKNIFTY&*');
  await page.route('**/api/dashboard/snapshot?indexId=BANKNIFTY&*', async route => {
    const response = await route.fetch();
    const snapshot: DashboardSnapshot = await response.json();
    snapshot.options = [];
    snapshot.position.units = 25;
    await route.fulfill({ response, json: snapshot });
  });
  await page.getByRole('button', { name: 'Refresh snapshot' }).click();
  await expect(page.getByRole('heading', { name: 'Snapshot unavailable' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Preset position', exact: true })).toHaveCount(0);
  await page.unroute('**/api/dashboard/indices');
  await page.unroute('**/api/dashboard/snapshot?indexId=BANKNIFTY&*');
  await page.getByRole('button', { name: 'Retry snapshot' }).click();
  await expect(page.getByRole('region', { name: 'Fixture freshness' })).toBeVisible();
});

test('journal list rejects a contradictory legacy market identity without labelling it as Bank Nifty', async ({ page }) => {
  await page.goto('/#journal');
  const card = page.getByRole('article').filter({ hasText: 'legacy-bank-plan' });
  await expect(card).toBeVisible();
  await page.route('**/api/journal/plans', async route => {
    const response = await route.fetch();
    const json = await response.json();
    json.find((entry: { id: string }) => entry.id === 'legacy-bank-plan').snapshot.market.indexName = 'SENSEX (synthetic cash context)';
    await route.fulfill({ response, json });
  });
  await page.getByRole('button', { name: 'Refresh journal' }).click();
  await expect(page.getByRole('heading', { name: 'Journal unavailable' })).toBeVisible();
  await expect(page.getByRole('article')).toHaveCount(0);
  await page.unroute('**/api/journal/plans');
  await page.getByRole('button', { name: 'Retry journal' }).click();
  await expect(card).toContainText('Bank Nifty · BANKNIFTY');
});
