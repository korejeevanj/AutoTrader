import { defineConfig } from '@playwright/test';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const dotnet = process.env.DOTNET_HOST_PATH ?? 'dotnet';
const journalDirectory = fileURLToPath(new URL('../../data/e2e/', import.meta.url));
const journalPath = resolve(journalDirectory, `journal-${process.pid}-${Date.now()}.sqlite`);
const apiPort = Number(process.env.AUTOTRADER_E2E_API_PORT ?? 5080);
const uiPort = Number(process.env.AUTOTRADER_E2E_UI_PORT ?? 5173);
if (![apiPort, uiPort].every(port => Number.isInteger(port) && port > 1024 && port < 65536)) throw new Error('Invalid E2E port configuration');

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  globalSetup: './e2e/setup.ts',
  metadata: { journalPath },
  use: {
    baseURL: `http://localhost:${uiPort}`,
    browserName: 'chromium',
    channel: process.env.PLAYWRIGHT_CHANNEL ?? 'msedge',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1440, height: 900 } } },
    { name: 'mobile', use: { viewport: { width: 390, height: 844 } } },
  ],
  webServer: [
    {
      command: `"${dotnet}" run --project ../../src/Trading.Api/Trading.Api.csproj --no-launch-profile --urls http://localhost:${apiPort}`,
      url: `http://localhost:${apiPort}/api/health`,
      reuseExistingServer: false,
      env: { ASPNETCORE_ENVIRONMENT: 'Development', Storage__SqliteJournalPath: journalPath, HistoricalReplay__RootDirectory: resolve(journalDirectory, `replay-${process.pid}-${Date.now()}`), LivePaper__RootDirectory: resolve(journalDirectory, `live-${process.pid}-${Date.now()}`), LivePaper__ConnectOnStartup: 'false' },
      timeout: 120_000,
    },
    {
      command: `npm run dev -- --port ${uiPort}`,
      url: `http://localhost:${uiPort}`,
      env: { AUTOTRADER_E2E_API_PORT: String(apiPort) },
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
});
