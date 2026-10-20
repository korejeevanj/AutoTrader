import type { FullConfig } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

// Seed only the run-specific journal before browser workflows. Literal pre-increment
// evidence matches the backend compatibility fixture; normal workflows use HTTP.
export default function setup(config: FullConfig) {
  const path: unknown = config.metadata.journalPath;
  if (typeof path !== 'string' || existsSync(path)) throw new Error('Expected a fresh, isolated E2E journal path.');
  const parent = dirname(path);
  if (!existsSync(dirname(parent))) throw new Error('E2E journal parent must be inside the existing data directory.');
  mkdirSync(parent, { recursive: true });
  const json = readFileSync(new URL('./fixtures/legacy-bank-plan.json', import.meta.url), 'utf8');
  const entry = JSON.parse(json);
  const database = new DatabaseSync(path);
  try {
    database.exec('CREATE TABLE journal_plans (id TEXT PRIMARY KEY NOT NULL, saved_at_utc TEXT NOT NULL, entry_json TEXT NOT NULL)');
    database.prepare('INSERT INTO journal_plans VALUES (?, ?, ?)').run(entry.id, entry.savedAtUtc, json);
  } finally {
    database.close();
  }
}
