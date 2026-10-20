import type { DashboardSnapshot, HealthResponse, IndexDefinition, JournalEntry, PlanPreview, PlanSelection, SavePlanRequest, ScenarioDescriptor, ScenarioId } from './model';
import { isEntries, isEntry, isIndices, isPreview, isScenarios, isSnapshot } from './validation';

function isHealthResponse(value: unknown): value is HealthResponse {
  return typeof value === 'object'
    && value !== null
    && 'status' in value && value.status === 'ok'
    && 'application' in value && value.application === 'AutoTrader'
    && 'mode' in value && value.mode === 'SYNTHETIC TEST'
    && 'paperAutoEnabled' in value && value.paperAutoEnabled === false;
}

export async function readHealth(signal: AbortSignal): Promise<HealthResponse> {
  const response = await fetch('/api/health', {
    signal: AbortSignal.any([signal, AbortSignal.timeout(5_000)]),
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`The API returned HTTP ${response.status}. Retry the connection.`);
  }

  let body: unknown;
  try {
    body = await response.json();
  } catch {
    throw new Error('The API did not return valid health JSON. Check the backend and retry.');
  }

  if (!isHealthResponse(body)) {
    throw new Error('The API response does not match the approved synthetic, paused health contract.');
  }

  return body;
}

export function errorMessage(error: unknown): string {
  return error instanceof DOMException && error.name === 'TimeoutError' ? 'Request timed out. Retry when the API is available.'
    : error instanceof TypeError ? 'Cannot reach the local API. Check the backend and retry.'
      : error instanceof Error ? error.message : 'Request failed. Please retry.';
}

async function request<T>(path: string, check: (value: unknown) => value is T, signal: AbortSignal, body?: unknown, expectedStatus = 200): Promise<T> {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST', cache: 'no-store',
    signal: AbortSignal.any([signal, AbortSignal.timeout(5_000)]),
    headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const json: unknown = await response.json().catch(() => null);
  if (response.status !== expectedStatus) {
    const detail = typeof json === 'object' && json !== null && 'detail' in json && typeof json.detail === 'string' ? ` ${json.detail}` : '';
    throw new Error(`API returned HTTP ${response.status}; expected ${expectedStatus}.${detail} Please retry.`);
  }
  if (!check(json)) throw new Error('API response does not match the approved synthetic dashboard contract. Please retry.');
  return json;
}
export const readScenarios = (signal: AbortSignal): Promise<ScenarioDescriptor[]> => request('/api/dashboard/scenarios', isScenarios, signal);
export const readIndices = (signal: AbortSignal): Promise<IndexDefinition[]> => request('/api/dashboard/indices', isIndices, signal);
export async function readSnapshot(indexId: string, scenarioId: ScenarioId, signal: AbortSignal, definition?: IndexDefinition): Promise<DashboardSnapshot> {
  if (definition && definition.id !== indexId) throw new Error('Invalid index metadata: requested index does not match its catalog definition. Please retry.');
  const capabilities = definition?.capabilities ?? { futuresChart: true, optionComparison: true, weightedConstituents: true };
  const snapshot = await request(`/api/dashboard/snapshot?indexId=${encodeURIComponent(indexId)}&scenarioId=${encodeURIComponent(scenarioId)}`, (value): value is DashboardSnapshot => {
    if (!isSnapshot(value, indexId, capabilities)) throw new Error(`Invalid index metadata or snapshot evidence. Expected schema 2 for catalog index ${indexId}, with matching contract underlyings and catalog capabilities. Please retry.`);
    return true;
  }, signal);
  if (snapshot.scenarioId !== scenarioId) throw new Error('API returned the wrong scenario. Please retry.');
  return snapshot;
}
export const previewPlan = (selection: PlanSelection, signal: AbortSignal): Promise<PlanPreview> => request('/api/journal/plans/preview', isPreview, signal, selection);
export const savePlan = (plan: SavePlanRequest, signal: AbortSignal): Promise<JournalEntry> => request('/api/journal/plans', isEntry, signal, plan, 201);
export async function readJournal(signal: AbortSignal, indexId?: string): Promise<JournalEntry[]> {
  const entries = await request(`/api/journal/plans${indexId ? `?indexId=${encodeURIComponent(indexId)}` : ''}`, isEntries, signal);
  if (indexId && entries.some(e => e.indexId !== indexId)) throw new Error('Journal returned evidence for the wrong index. Please retry.');
  return entries;
}
export const readEntry = (id: string, signal: AbortSignal): Promise<JournalEntry> => request(`/api/journal/plans/${encodeURIComponent(id)}`, isEntry, signal);
