import { expect, test, type APIRequestContext } from '@playwright/test';

async function saveFixturePlan(request: APIRequestContext, indexId: string, note: string) {
  const response = await request.get(`/api/dashboard/snapshot?indexId=${indexId}&scenarioId=normal`);
  expect(response.status()).toBe(200);
  const snapshot = await response.json();
  const saved = await request.post('/api/journal/plans', { data: {
    indexId, scenarioId: 'normal', snapshotId: snapshot.snapshotId, contractId: snapshot.options[0].contract.id,
    lots: 1, status: 'considered', thesis: '', stopPlan: '', invalidation: '', note,
  } });
  expect(saved.status()).toBe(201);
  return saved.json();
}

test('index switch clears selection and frozen comparison and restores index-contract drafts', async ({ page }, testInfo) => {
  await page.goto('/');
  const options = page.getByRole('region', { name: 'Option candidates' });
  await expect(options).toBeVisible();
  const index = page.getByLabel('Cockpit index', { exact: true });
  await expect(index).toBeVisible();
  await expect(index).toHaveValue('BANKNIFTY');
  const plan = page.getByRole('region', { name: 'Option plan' });
  await options.getByRole('button', { name: /^Select / }).first().click();
  await plan.getByLabel('Note', { exact: true }).fill('Bank draft');
  await plan.getByRole('button', { name: 'Pin for comparison' }).click();
  await options.getByRole('button', { name: /^Select / }).nth(1).click();
  await plan.getByRole('button', { name: 'Pin for comparison' }).click();
  const compare = page.getByRole('region', { name: 'Option comparison' });
  await compare.getByRole('button', { name: 'Freeze comparison', exact: true }).click();
  await index.selectOption('NIFTY');
  await expect(page.getByRole('heading', { name: 'Nifty 50 cockpit', exact: true })).toBeVisible();
  await expect(plan).toHaveCount(0);
  await expect(compare.getByRole('article')).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: 'Comparison cleared' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Constituent participation' })).toBeVisible();
  await options.getByRole('button', { name: /^Select / }).first().click();
  await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('');
  await expect(plan.getByRole('button', { name: 'Pin for comparison' })).toBeEnabled();
  await plan.getByLabel('Note', { exact: true }).fill('Nifty draft');
  await page.screenshot({ path: testInfo.outputPath('nifty-cockpit.png'), fullPage: true });
  await index.selectOption('SENSEX');
  await expect(page.getByRole('heading', { name: 'Sensex cockpit', exact: true })).toBeVisible();
  await expect(options).toBeVisible();
  await expect(plan).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('sensex-cockpit.png'), fullPage: true });
  for (const [id, note] of [['BANKNIFTY', 'Bank draft'], ['NIFTY', 'Nifty draft']]) {
    await index.selectOption(id!);
    await options.getByRole('button', { name: /^Select / }).first().click();
    await expect(plan.getByLabel('Note', { exact: true })).toHaveValue(note!);
    await expect(plan.getByRole('button', { name: 'Pin for comparison' })).toBeEnabled();
  }
});

test('server previews distinct whole lots and shared journal saves and filters every index', async ({ page }, testInfo) => {
  const notes = Object.fromEntries(['BANKNIFTY', 'NIFTY', 'SENSEX'].map(id => [id, `[${id}] saved ${testInfo.project.name} ${Date.now()}`]));
  await page.goto('/');
  const options = page.getByRole('region', { name: 'Option candidates' });
  const plan = page.getByRole('region', { name: 'Option plan' });
  for (const [id, units] of [['BANKNIFTY', '50'], ['NIFTY', '150'], ['SENSEX', '40']]) {
    await expect(page.getByLabel('Cockpit index', { exact: true })).toBeEnabled();
    await page.getByLabel('Cockpit index', { exact: true }).selectOption(id!);
    await options.getByRole('button', { name: /^Select / }).first().click();
    await plan.getByLabel('Whole lots').fill('2');
    await expect(plan.getByText(`${units} units`, { exact: true })).toBeVisible();
    await expect(plan).toContainText('Unavailable');
    await plan.getByLabel('Note', { exact: true }).fill(notes[id!]!);
    const response = page.waitForResponse(r => r.url().endsWith('/api/journal/plans') && r.request().method() === 'POST');
    await plan.getByRole('button', { name: 'Save plan', exact: true }).click();
    const saved = await response;
    expect(saved.status()).toBe(201);
    expect(saved.request().postDataJSON().indexId).toBe(id);
    await expect(plan.getByRole('status')).toContainText('Saved to journal');
  }
  await page.getByRole('link', { name: 'Journal', exact: true }).click();
  const filter = page.getByLabel('Journal index', { exact: true });
  await expect(filter).toHaveValue('');
  for (const id of Object.keys(notes)) {
    const card = page.getByRole('article').filter({ hasText: notes[id]! });
    await expect(card).toContainText(id);
  }
  for (const id of Object.keys(notes)) {
    const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/journal/plans' && new URL(r.url()).searchParams.get('indexId') === id);
    await filter.selectOption(id);
    expect((await response).status()).toBe(200);
    const card = page.getByRole('article').filter({ hasText: notes[id]! });
    await expect(card).toBeVisible();
    for (const other of Object.keys(notes).filter(other => other !== id)) await expect(page.getByRole('article').filter({ hasText: notes[other]! })).toHaveCount(0);
    await card.getByRole('button', { name: 'Inspect saved evidence' }).click();
    await expect(page.getByRole('region', { name: 'Original saved evidence' })).toContainText(id);
    await page.getByRole('button', { name: 'Close evidence' }).click();
  }
  await filter.selectOption('');
  for (const note of Object.values(notes)) await expect(page.getByRole('article').filter({ hasText: note })).toBeVisible();
});

for (const failure of ['legacy current response', 'wrong index', 'cross-index underlying'] as const) {
  test(`current snapshot rejects ${failure} with invalid index metadata and genuine retry`, async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('region', { name: 'Option candidates' })).toBeVisible();
    await page.route('**/api/dashboard/snapshot?*', async route => {
      const response = await route.fetch();
      const json = await response.json();
      if (failure === 'legacy current response') {
        json.schemaVersion = '1';
        delete json.indexId;
        delete json.constituents;
      } else if (failure === 'wrong index') json.indexId = 'SENSEX';
      else json.options[0].contract.underlying = 'NIFTY';
      await route.fulfill({ response, json });
    });
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByRole('heading', { name: 'Snapshot unavailable' })).toBeVisible();
    await expect(page.getByRole('alert')).toContainText('Invalid index metadata');
    await expect(page.getByRole('region', { name: 'Option candidates' })).toHaveCount(0);
    await page.unroute('**/api/dashboard/snapshot?*');
    await page.getByRole('button', { name: 'Retry snapshot' }).click();
    await expect(page.getByRole('region', { name: 'Option candidates' })).toBeVisible();
  });
}

test('obsolete index snapshots and previews cannot overwrite the latest workspace', async ({ page }) => {
  let releaseSnapshot: () => void = () => {};
  let releasePreview: () => void = () => {};
  const snapshotGate = new Promise<void>(resolve => { releaseSnapshot = resolve; });
  const previewGate = new Promise<void>(resolve => { releasePreview = resolve; });
  let snapshotHeld = false;
  let previewHeld = false;
  await page.route('**/api/dashboard/snapshot?indexId=BANKNIFTY&scenarioId=normal', async route => {
    const response = await route.fetch();
    snapshotHeld = true;
    await snapshotGate;
    await route.fulfill({ response }).catch(() => {});
  });
  await page.goto('/');
  const index = page.getByLabel('Cockpit index', { exact: true });
  await expect(index).toBeEnabled();
  await expect.poll(() => snapshotHeld).toBe(true);
  await index.selectOption('NIFTY');
  const market = page.getByRole('region', { name: 'Market summary' });
  await expect(market).toContainText('synthetic-dashboard-v2:NIFTY:');
  releaseSnapshot();
  await expect(market).not.toContainText('BANKNIFTY');
  await page.route('**/api/journal/plans/preview', async route => {
    const response = await route.fetch();
    if (route.request().postDataJSON().indexId === 'NIFTY') {
      previewHeld = true;
      await previewGate;
    }
    await route.fulfill({ response }).catch(() => {});
  });
  const options = page.getByRole('region', { name: 'Option candidates' });
  await options.getByRole('button', { name: /^Select / }).first().click();
  await expect.poll(() => previewHeld).toBe(true);
  await index.selectOption('SENSEX');
  await options.getByRole('button', { name: /^Select / }).first().click();
  const plan = page.getByRole('region', { name: 'Option plan' });
  await expect(plan.getByText('20 units', { exact: true })).toBeVisible();
  releasePreview();
  await expect(plan.getByText('75 units', { exact: true })).toHaveCount(0);
  await expect(plan.getByText('20 units', { exact: true })).toBeVisible();
});

test('journal rejects misrouted filtered entries and ignores obsolete filter responses', async ({ page, request }, testInfo) => {
  const bankNote = `Bank filter isolation ${testInfo.project.name} ${Date.now()}`;
  const niftyNote = `Nifty filter isolation ${testInfo.project.name} ${Date.now()}`;
  await saveFixturePlan(request, 'BANKNIFTY', bankNote);
  await saveFixturePlan(request, 'NIFTY', niftyNote);
  await page.goto('/#journal');
  await expect(page.getByRole('article').filter({ hasText: bankNote })).toBeVisible();
  const filter = page.getByLabel('Journal index', { exact: true });
  await expect(filter).toBeEnabled();
  await page.route('**/api/journal/plans?indexId=NIFTY', async route => {
    const response = await route.fetch({ url: route.request().url().replace('indexId=NIFTY', 'indexId=BANKNIFTY') });
    await route.fulfill({ response });
  });
  await filter.selectOption('NIFTY');
  await expect(page.getByRole('heading', { name: 'Journal unavailable' })).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('wrong index');
  await expect(page.getByRole('article')).toHaveCount(0);
  await page.unroute('**/api/journal/plans?indexId=NIFTY');
  await page.getByRole('button', { name: 'Retry journal' }).click();
  await expect(page.getByRole('article').filter({ hasText: niftyNote })).toBeVisible();
  let release: () => void = () => {};
  const gate = new Promise<void>(resolve => { release = resolve; });
  let held = false;
  await page.route('**/api/journal/plans?indexId=BANKNIFTY', async route => {
    const response = await route.fetch();
    held = true;
    await gate;
    await route.fulfill({ response }).catch(() => {});
  });
  await filter.selectOption('BANKNIFTY');
  await expect.poll(() => held).toBe(true);
  await filter.selectOption('NIFTY');
  await expect(page.getByRole('article').filter({ hasText: niftyNote })).toBeVisible();
  release();
  await expect(page.getByRole('article').filter({ hasText: bankNote })).toHaveCount(0);
  await expect(page.getByRole('article').filter({ hasText: niftyNote })).toBeVisible();
});

test('legacy Bank Nifty journal evidence keeps schema 1 and its original payload in the JSON view', async ({ page, request }) => {
  const response = await request.get('/api/journal/plans/legacy-bank-plan');
  expect(response.status()).toBe(200);
  const original = await response.json();
  expect(original.indexId).toBe('BANKNIFTY');
  expect(original.snapshot.schemaVersion).toBe('1');
  expect(original.snapshot.indexId).toBeNull();
  expect(original.snapshot.constituents).toBeNull();
  await page.goto('/#journal');
  const card = page.getByRole('article').filter({ hasText: 'legacy-bank-plan' });
  await expect(card).toContainText('Bank Nifty · BANKNIFTY');
  await expect(card.getByText('<b>old plain text</b>', { exact: true })).toBeVisible();
  await card.getByRole('button', { name: 'Inspect saved evidence' }).click();
  const evidence = page.getByRole('region', { name: 'Original saved evidence' });
  await expect(evidence).toContainText('Legacy Bank Nifty schema-1 fixture');
  await expect(evidence.getByRole('region', { name: 'Bank participation', exact: true })).toContainText('Fixture Bank A');
  await evidence.getByText('Full preserved snapshot / provenance (JSON)', { exact: true }).click();
  expect(JSON.parse(await evidence.locator('pre').innerText())).toEqual(original.snapshot);
  await page.getByLabel('Journal index', { exact: true }).selectOption('NIFTY');
  await expect(card).toHaveCount(0);
  await page.getByLabel('Journal index', { exact: true }).selectOption('BANKNIFTY');
  await expect(card).toBeVisible();
  await page.reload();
  await expect(card).toBeVisible();
});

for (const failure of ['wrong root index', 'unknown legacy version', 'unknown legacy fixture', 'mixed legacy underlyings'] as const) {
  test(`saved evidence rejects ${failure} without relabelling market data`, async ({ page }) => {
    await page.goto('/#journal');
    const card = page.getByRole('article').filter({ hasText: 'legacy-bank-plan' });
    await expect(card).toBeVisible();
    await page.route('**/api/journal/plans/legacy-bank-plan', async route => {
      const response = await route.fetch();
      const json = await response.json();
      if (failure === 'wrong root index') json.indexId = 'NIFTY';
      else if (failure === 'unknown legacy version') json.snapshot.schemaVersion = '3';
      else if (failure === 'unknown legacy fixture') json.snapshot.fixtureVersion = 'unknown-fixture';
      else json.snapshot.options.push({ ...json.snapshot.options[0], contract: { ...json.snapshot.options[0].contract, id: 'cross-index-contract', underlying: 'NIFTY' } });
      await route.fulfill({ response, json });
    });
    await card.getByRole('button', { name: 'Inspect saved evidence' }).click();
    const evidence = page.getByRole('region', { name: 'Original saved evidence' });
    await expect(evidence.getByRole('alert')).toContainText('does not match');
    await expect(evidence.locator('pre')).toHaveCount(0);
    await page.unroute('**/api/journal/plans/legacy-bank-plan');
    await evidence.getByRole('button', { name: 'Retry evidence' }).click();
    await expect(evidence).toContainText('Legacy Bank Nifty schema-1 fixture');
  });
}

for (const failure of ['invalid catalog', 'selected identity removed'] as const) {
  test(`catalog ${failure} clears current market evidence and exposes metadata retry`, async ({ page }) => {
    await page.goto('/');
    const options = page.getByRole('region', { name: 'Option candidates' });
    await options.getByRole('button', { name: /^Select / }).first().click();
    const plan = page.getByRole('region', { name: 'Option plan' });
    await plan.getByLabel('Note', { exact: true }).fill('Draft retained through metadata recovery');
    await page.route('**/api/dashboard/indices', async route => {
      const response = await route.fetch();
      let json = await response.json();
      if (failure === 'invalid catalog') delete json[0].capabilities;
      else json = json.filter((i: { id: string }) => i.id !== 'BANKNIFTY');
      await route.fulfill({ response, json });
    });
    await page.getByRole('button', { name: 'Refresh snapshot' }).click();
    await expect(page.getByRole('heading', { name: 'Snapshot unavailable' })).toBeVisible();
    await expect(page.getByRole('alert')).toContainText('Index metadata unavailable');
    await expect(options).toHaveCount(0);
    await expect(plan.getByLabel('Note', { exact: true })).toHaveValue('Draft retained through metadata recovery');
    await expect(plan.getByRole('button', { name: 'Save plan', exact: true })).toBeDisabled();
    await page.unroute('**/api/dashboard/indices');
    await page.getByRole('button', { name: 'Retry snapshot' }).click();
    await expect(options).toBeVisible();
    await expect(plan.getByRole('button', { name: 'Save plan', exact: true })).toBeEnabled();
  });
}
