import type { DashboardSnapshot, IndexDefinition, JournalEntry, PlanPreview, ScenarioDescriptor, SnapshotEvidence } from './model';

type Check = (value: unknown) => boolean;
const text: Check = v => typeof v === 'string';
const nonempty: Check = v => typeof v === 'string' && v.trim().length > 0;
const number: Check = v => typeof v === 'number' && Number.isFinite(v);
const positive: Check = v => number(v) && (v as number) > 0;
const integer: Check = v => Number.isSafeInteger(v) && (v as number) >= 0;
const wholeLots: Check = v => integer(v) && (v as number) > 0;
const boolean: Check = v => typeof v === 'boolean';
const timestamp: Check = v => typeof v === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|\+00:00)$/.test(v) && Number.isFinite(Date.parse(v));
const nullable = (check: Check): Check => v => v === null || check(v);
const oneOf = (...values: unknown[]): Check => v => values.includes(v);
const array = (check: Check): Check => v => Array.isArray(v) && v.every(check);
const object = (shape: Record<string, Check>): Check => v => typeof v === 'object' && v !== null && !Array.isArray(v) && Object.entries(shape).every(([key, check]) => key in v && check((v as Record<string, unknown>)[key]));
const scenario = oneOf('normal', 'missing-inputs', 'stale-quotes', 'feed-loss', 'open-position');
const quoteState = oneOf('fresh', 'missing', 'stale', 'disconnected');
const planStatus = oneOf('considered', 'planned', 'skipped');
const optionalNumber = nullable(number);
const optionalTime = nullable(timestamp);
const planning = object({ roundTripFees: optionalNumber, requiredCash: optionalNumber, netRisk: optionalNumber, availabilityReason: text });
const contract = object({ id: nonempty, symbol: nonempty, exchange: oneOf('SYNTHETIC'), underlying: nonempty, optionType: oneOf('CE', 'PE'), expiry: v => typeof v === 'string' && /^\d{4}-\d\d-\d\d$/.test(v) && Number.isFinite(Date.parse(v)), strike: positive, lotSize: wholeLots, tickSize: positive, validFromUtc: timestamp, validToUtc: timestamp, isSynthetic: oneOf(true) });
const quote = object({ lastPrice: optionalNumber, bid: optionalNumber, ask: optionalNumber, bidQuantity: nullable(integer), askQuantity: nullable(integer), eventTimeUtc: optionalTime, receivedAtUtc: optionalTime, lastTradeTimeUtc: optionalTime, state: quoteState });
const option = object({ contract, quote, spread: optionalNumber, quoteAgeSeconds: optionalNumber, decision: object({ action: oneOf('WATCH', 'SKIP', 'INSUFFICIENT_DATA'), reasonCodes: array(text) }) });
const bar = object({ startUtc: timestamp, endUtc: timestamp, availableAtUtc: timestamp, open: number, high: number, low: number, close: number, volume: number });
const constituent = object({ id: nonempty, name: nonempty, weightPercent: optionalNumber, lastPrice: optionalNumber, vwap: optionalNumber, changePercent: optionalNumber, aboveVwap: nullable(boolean), state: quoteState, eventTimeUtc: optionalTime, receivedAtUtc: optionalTime });
const snapshotShape = object({
  schemaVersion: nonempty, fixtureVersion: nonempty, snapshotId: nonempty, scenarioId: scenario, scenarioLabel: nonempty, sequence: integer, asOfUtc: timestamp, source: oneOf('SYNTHETIC_FIXTURE'), mode: oneOf('SYNTHETIC TEST'),
  freshness: object({ state: oneOf('fresh', 'partial', 'stale', 'disconnected'), quoteAgeSeconds: optionalNumber, receiptAgeSeconds: optionalNumber, message: text }),
  market: object({ indexName: nonempty, indexValue: optionalNumber, futuresSymbol: nonempty, futuresValue: optionalNumber, changePercent: optionalNumber }),
  chart: object({ symbol: nonempty, bars: array(bar), openingRangeHigh: optionalNumber, openingRangeLow: optionalNumber, vwap: array(object({ timeUtc: timestamp, value: number })), vwapLabel: text }),
  banks: array(constituent),
  participation: object({ aboveVwapWeightPercent: optionalNumber, belowVwapWeightPercent: optionalNumber, missingWeightPercent: number, coverageLabel: text }),
  options: array(option),
  position: object({ state: oneOf('none', 'open', 'unpriced'), positionId: nullable(nonempty), contractId: nullable(nonempty), lots: nullable(wholeLots), units: nullable(wholeLots), entryPrice: optionalNumber, currentBid: optionalNumber, grossPnl: optionalNumber, netPnl: optionalNumber, message: text }),
  planning,
});
function hasNoPosition(p: SnapshotEvidence['position']): boolean {
  return p.state === 'none' && p.positionId === null && p.contractId === null && p.lots === null && p.units === null
    && p.entryPrice === null && p.currentBid === null && p.grossPnl === null && p.netPnl === null;
}
function isEvidence(value: unknown): value is SnapshotEvidence {
  if (!snapshotShape(value)) return false;
  const s = value as SnapshotEvidence;
  const asOf = Date.parse(s.asOfUtc);
  return (s.options.length > 0 || hasNoPosition(s.position)) && new Set(s.options.map(o => o.contract.id)).size === s.options.length
    && new Set(s.banks.map(b => b.id)).size === s.banks.length
    && s.chart.bars.every((b, i, bars) => Date.parse(b.endUtc) - Date.parse(b.startUtc) === 60_000
      && Date.parse(b.availableAtUtc) >= Date.parse(b.endUtc) && Date.parse(b.availableAtUtc) <= asOf
      && b.high >= Math.max(b.open, b.close, b.low) && b.low <= Math.min(b.open, b.close)
      && (i === 0 || Date.parse(b.startUtc) > Date.parse(bars[i - 1]!.startUtc)))
    && (s.position.contractId === null || s.options.some(o => o.contract.id === s.position.contractId))
    && s.chart.vwap.every((p, i, points) => Date.parse(p.timeUtc) <= asOf && (i === 0 || Date.parse(p.timeUtc) > Date.parse(points[i - 1]!.timeUtc)));
}
export function isSnapshot(value: unknown, indexId?: string, capabilities?: IndexDefinition['capabilities']): value is DashboardSnapshot {
  if (!isEvidence(value) || !object({ schemaVersion: oneOf('2'), indexId: nonempty, constituents: array(constituent) })(value)) return false;
  const s = value as DashboardSnapshot;
  return (indexId === undefined || s.indexId === indexId)
    && s.options.every(o => o.contract.underlying === s.indexId)
    && new Set(s.constituents.map(c => c.id)).size === s.constituents.length
    && (s.indexId === 'BANKNIFTY' || s.banks.length === 0)
    && (!capabilities || (capabilities.futuresChart ? s.chart.bars.length > 0 && s.chart.vwap.length > 0
      : s.chart.bars.length === 0 && s.chart.vwap.length === 0
        && s.chart.openingRangeHigh === null && s.chart.openingRangeLow === null && s.market.futuresValue === null))
    && (!capabilities || (capabilities.optionComparison ? s.options.length > 0 : s.options.length === 0 && hasNoPosition(s.position)))
    && (!capabilities || capabilities.weightedConstituents || (s.constituents.every(c => c.weightPercent === null)
      && s.banks.every(c => c.weightPercent === null) && s.participation.aboveVwapWeightPercent === null
      && s.participation.belowVwapWeightPercent === null && s.participation.missingWeightPercent === 100));
}
function isSavedSnapshot(value: unknown): value is SnapshotEvidence {
  if (isSnapshot(value)) return true;
  if (!isEvidence(value)) return false;
  // A display bridge for the known pre-increment fixture only; never mutate its payload.
  return value.schemaVersion === '1' && value.fixtureVersion === 'synthetic-dashboard-v1'
    && value.snapshotId === `synthetic-dashboard-v1:${value.scenarioId}`
    && value.market.indexName === 'BANKNIFTY (synthetic cash context)'
    && value.indexId == null && value.constituents == null
    && value.options.length > 0 && value.options.every(o => o.contract.underlying === 'BANKNIFTY');
}
export function isIndices(value: unknown): value is IndexDefinition[] {
  return array(object({ id: nonempty, displayName: nonempty, exchange: nonempty, timeZone: nonempty, constituentLabel: nonempty, definitionVersion: nonempty, effectiveFromUtc: timestamp, effectiveToUtc: optionalTime, isSynthetic: oneOf(true), capabilities: object({ futuresChart: boolean, optionComparison: boolean, weightedConstituents: boolean }) }))(value)
    && (value as IndexDefinition[]).length > 0
    && new Set((value as IndexDefinition[]).map(i => i.id)).size === (value as IndexDefinition[]).length;
}
export function isScenarios(value: unknown): value is ScenarioDescriptor[] {
  return array(object({ id: scenario, label: nonempty, description: text }))(value)
    && (value as ScenarioDescriptor[]).length === 5 && new Set((value as ScenarioDescriptor[]).map(s => s.id)).size === 5;
}
export function isPreview(value: unknown): value is PlanPreview {
  return object({ lots: wholeLots, units: wholeLots, referenceAsk: optionalNumber, quoteState, planning })(value);
}
export function isEntry(value: unknown): value is JournalEntry {
  if (!object({ id: nonempty, indexId: nonempty, savedAtUtc: timestamp, mode: oneOf('SYNTHETIC TEST'), scenarioId: scenario, snapshotId: nonempty, contractId: nonempty, lots: wholeLots, units: wholeLots, status: planStatus, thesis: text, stopPlan: text, invalidation: text, note: text, snapshot: isSavedSnapshot })(value)) return false;
  const e = value as JournalEntry;
  return e.indexId === (e.snapshot.schemaVersion === '1' ? 'BANKNIFTY' : e.snapshot.indexId)
    && e.scenarioId === e.snapshot.scenarioId && e.snapshotId === e.snapshot.snapshotId && e.snapshot.options.some(o => o.contract.id === e.contractId);
}
export function isEntries(value: unknown): value is JournalEntry[] { return array(isEntry)(value); }
