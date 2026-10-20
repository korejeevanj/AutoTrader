export interface HealthResponse {
  status: 'ok';
  application: 'AutoTrader';
  mode: 'SYNTHETIC TEST';
  paperAutoEnabled: false;
}

export type ApiConnection =
  | { status: 'checking' }
  | { status: 'connected'; health: HealthResponse }
  | { status: 'unavailable'; message: string };

export type ScenarioId = 'normal' | 'missing-inputs' | 'stale-quotes' | 'feed-loss' | 'open-position';
export type QuoteState = 'fresh' | 'missing' | 'stale' | 'disconnected';
export type PlanStatus = 'considered' | 'planned' | 'skipped';
export interface ScenarioDescriptor { id: ScenarioId; label: string; description: string }
export interface IndexDefinition {
  id: string; displayName: string; exchange: string;
  timeZone: string; constituentLabel: string; definitionVersion: string;
  effectiveFromUtc: string; effectiveToUtc: string | null; isSynthetic: true;
  capabilities: { futuresChart: boolean; optionComparison: boolean; weightedConstituents: boolean };
}
export interface FixtureContract {
  id: string; symbol: string; exchange: 'SYNTHETIC'; underlying: string;
  optionType: 'CE' | 'PE'; expiry: string; strike: number; lotSize: number;
  tickSize: number; validFromUtc: string; validToUtc: string; isSynthetic: true;
}
export interface Quote {
  lastPrice: number | null; bid: number | null; ask: number | null;
  bidQuantity: number | null; askQuantity: number | null;
  eventTimeUtc: string | null; receivedAtUtc: string | null;
  lastTradeTimeUtc: string | null; state: QuoteState;
}
export interface OptionRow {
  contract: FixtureContract; quote: Quote; spread: number | null; quoteAgeSeconds: number | null;
  decision: { action: 'WATCH' | 'SKIP' | 'INSUFFICIENT_DATA'; reasonCodes: string[] };
}
export interface FixtureBar {
  startUtc: string; endUtc: string; availableAtUtc: string;
  open: number; high: number; low: number; close: number; volume: number;
}
export interface ConstituentRow {
  id: string; name: string; weightPercent: number | null; lastPrice: number | null;
  vwap: number | null; changePercent: number | null; aboveVwap: boolean | null;
  state: QuoteState; eventTimeUtc: string | null; receivedAtUtc: string | null;
}
export interface Planning {
  roundTripFees: number | null; requiredCash: number | null; netRisk: number | null; availabilityReason: string;
}
export interface SnapshotEvidence {
  schemaVersion: string; fixtureVersion: string; snapshotId: string; scenarioId: ScenarioId;
  scenarioLabel: string; sequence: number; asOfUtc: string; source: 'SYNTHETIC_FIXTURE'; mode: 'SYNTHETIC TEST';
  freshness: { state: 'fresh' | 'partial' | 'stale' | 'disconnected'; quoteAgeSeconds: number | null; receiptAgeSeconds: number | null; message: string };
  market: { indexName: string; indexValue: number | null; futuresSymbol: string; futuresValue: number | null; changePercent: number | null };
  chart: { symbol: string; bars: FixtureBar[]; openingRangeHigh: number | null; openingRangeLow: number | null; vwap: { timeUtc: string; value: number }[]; vwapLabel: string };
  indexId?: string | null; constituents?: ConstituentRow[] | null;
  /** Deprecated current Bank Nifty alias; original evidence for schema-1 journals. */
  banks: ConstituentRow[];
  participation: { aboveVwapWeightPercent: number | null; belowVwapWeightPercent: number | null; missingWeightPercent: number; coverageLabel: string };
  options: OptionRow[];
  position: { state: 'none' | 'open' | 'unpriced'; positionId: string | null; contractId: string | null; lots: number | null; units: number | null; entryPrice: number | null; currentBid: number | null; grossPnl: number | null; netPnl: number | null; message: string };
  planning: Planning;
}
/** Current HTTP reads require explicit index metadata; saved legacy evidence stays unmodified. */
export interface DashboardSnapshot extends SnapshotEvidence { schemaVersion: '2'; indexId: string; constituents: ConstituentRow[] }
export interface PlanSelection { indexId: string; scenarioId: ScenarioId; snapshotId: string; contractId: string; lots: number }
export interface PlanPreview { lots: number; units: number; referenceAsk: number | null; quoteState: QuoteState; planning: Planning }
export interface SavePlanRequest extends PlanSelection { status: PlanStatus; thesis: string; stopPlan: string; invalidation: string; note: string }
export interface JournalEntry extends SavePlanRequest { id: string; savedAtUtc: string; mode: 'SYNTHETIC TEST'; units: number; snapshot: SnapshotEvidence }
export type Remote<T> = { status: 'loading' } | { status: 'ready'; data: T } | { status: 'error'; message: string };
