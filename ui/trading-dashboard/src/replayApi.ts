export interface ReplayJob {
  id: string; date: string; state: string; message: string; calendarStatus: string; catalogSource: string | null;
  warnings: string[]; instruments: { id: string; token: number; role: string; state: string; rows: number; cached: boolean; error: string | null }[];
  total: number; finished: number; canReplay: boolean; mode: string; run: number; reportsAvailable: boolean;
  preparingData?: boolean;
  preparationComplete?: boolean;
  attempt?: number;
}
export interface ReplayBar { id: string; startUtc: string; endUtc: string; availableAtUtc: string; open: number; high: number; low: number; close: number }
export interface ProfitExitSettings { version: string; mode: 'Disabled' | 'FixedNetTarget' | 'TrailingNetProfit'; netTarget: number; trailActivation: number; trailGiveback: number; minimumLockedNetProfit: number }
export interface ProfitExitEvidence { policy: ProfitExitSettings; peakNetLiquidationPnl: number | null; triggeredAtUtc: string | null; triggerNetLiquidationPnl: number | null; triggerThreshold: number | null; realizedShortfall: number | null }
export interface ReplayDirectionalEvidence {
  version: string; status: 'UP' | 'DOWN' | 'NO_SETUP' | 'INSUFFICIENT_DATA' | 'NOT_EVALUATED';
  asOfUtc: string; barEndUtc: string | null; evidenceAvailableAtUtc: string | null; meetsDirectionalRules: boolean;
  closeMinusOpeningHigh: number | null; closeMinusOpeningLow: number | null; closeMinusVwap: number | null;
  return1MinutePercent: number | null; return5MinutePercent: number | null;
  confirmingCount: number | null; opposingCount: number | null; neutralCount: number | null;
  topThreeConfirmingWeightPercent: number | null; minimumConfirmingConstituents: number | null; reasons: string[];
}
export interface ReplayView {
  serverInstanceId?: string | null;
  presetProgress?: { id: string; number: number; total: number; allPresets: boolean } | null;
  journalPending?: boolean;
  diagnosticError?: string | null;
  revision?: number;
  timing?: { mode: string; requestedSpeed: number; achievedSpeed: number | null; lagMilliseconds: number; processingMilliseconds: number } | null;
  paperRunId?: string | null;
  paper?: { startingCapital: number; cash: number; availableCash: number; reservedCash: number; realizedNetPnl: number; paidFees: number;
    entriesPaused: boolean; acceptedAttempts: number;
    pendingIntents: { id: string; contractId: string; setupId: string; acceptedAtUtc: string; expiresAtUtc: string; reservedCash: number }[];
    positions: { id: string; contractId: string; units: number; entryTimeUtc: string; entryPrice: number; grossPnl: number | null; netPnl: number | null;
      policyAction: string; reasons: string[]; pendingExitReason: string | null; profitExit?: ProfitExitEvidence | null; currentProfitExitThreshold?: number | null }[] } | null;
  playbackKind?: string; chartLabel?: string; chartInstrumentId?: string | null; chartInstruments?: { id: string; role: string }[];
  job: ReplayJob; simulatedTimeUtc: string | null; paused: boolean; complete: boolean; bars: ReplayBar[];
  feature: { openingHigh: number | null; openingLow: number | null; vwap: number | null; aboveWeight: number | null; belowWeight: number | null; missingWeight: number | null; direction: string | null; setupId: string | null; directionalEvidence?: ReplayDirectionalEvidence | null } | null;
  constituents: { instrumentId: string; weightPercent: number | null; close: number | null; vwap: number | null; periodEndUtc: string | null; state: string }[];
  decisions: { contractId: string; class: string; action: string; reasons: string[]; timeUtc?: string | null; indexId?: string; setupId?: string | null;
    rank?: number | null; rankingMetrics?: { spreadPercent: number; roundTripFriction: number; strikeDistance: number } | null;
    gates?: { code: string; outcome: number | string; detail: string; observed: number | null; threshold: number | null }[] | null }[];
  trades: { id: string; contractId: string; units: number; entryTimeUtc: string; entryPrice: number; exitKnownAtUtc: string | null; exitPrice: number | null; netPnl: number | null; exitReason: string | null; evidenceClass: string;
    entryFees?: FeeBreakdown | null; exitFees?: FeeBreakdown | null; grossPnl?: number | null; profitExit?: ProfitExitEvidence | null }[];
  openPositions: number; pendingEntries: number; netSessionPnl: number | null;
  settings: { version: string; preset: string; grossStop: number; maximumNetRisk: number; candidateBatchSize: number; spreadTicks: number; slippageTicks: number; profitExit?: ProfitExitSettings | null } | null;
  reportFiles: string[];
}
export interface ReplayStatus { credentialStatus: string; latestCompletedDate: string; guideUrl: string; metadataMessage: string }
export interface FeeBreakdown { brokerage: number; exchange: number; regulatory: number; transactionTax: number; stampDuty: number; gst: number; total: number; scheduleVersion: string }
const object = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;
const text = (v: unknown): v is string => typeof v === 'string';
const number = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const nullableNumber = (v: unknown): v is number | null => v === null || number(v);
const nullableText = (v: unknown): v is string | null => v === null || text(v);
const strings = (v: unknown): v is string[] => Array.isArray(v) && v.every(text);
const directionalEvidence = (v: unknown, clock: unknown): v is ReplayDirectionalEvidence => object(v)
  && text(v.version) && text(v.status) && ['UP', 'DOWN', 'NO_SETUP', 'INSUFFICIENT_DATA', 'NOT_EVALUATED'].includes(v.status)
  && text(v.asOfUtc) && text(clock) && Date.parse(v.asOfUtc) <= Date.parse(clock)
  && nullableText(v.barEndUtc) && (v.barEndUtc === null || Date.parse(v.barEndUtc) <= Date.parse(v.asOfUtc))
  && nullableText(v.evidenceAvailableAtUtc) && (v.evidenceAvailableAtUtc === null || Date.parse(v.evidenceAvailableAtUtc) <= Date.parse(v.asOfUtc))
  && typeof v.meetsDirectionalRules === 'boolean' && strings(v.reasons)
  && ['closeMinusOpeningHigh', 'closeMinusOpeningLow', 'closeMinusVwap', 'return1MinutePercent', 'return5MinutePercent', 'topThreeConfirmingWeightPercent']
    .every(key => nullableNumber(v[key]))
  && ['confirmingCount', 'opposingCount', 'neutralCount', 'minimumConfirmingConstituents']
    .every(key => v[key] === null || number(v[key]) && Number.isSafeInteger(v[key]) && v[key] >= 0);
const profitPolicy = (v: unknown): v is ProfitExitSettings => object(v) && text(v.version) && !!v.version
  && text(v.mode) && ['Disabled', 'FixedNetTarget', 'TrailingNetProfit'].includes(v.mode)
  && number(v.netTarget) && v.netTarget > 0 && number(v.trailActivation) && v.trailActivation > 0
  && number(v.trailGiveback) && v.trailGiveback > 0 && number(v.minimumLockedNetProfit) && v.minimumLockedNetProfit >= 0 && v.minimumLockedNetProfit < v.trailActivation;
const profitEvidence = (v: unknown, clock: unknown): v is ProfitExitEvidence => object(v) && profitPolicy(v.policy)
  && nullableNumber(v.peakNetLiquidationPnl) && nullableNumber(v.triggerNetLiquidationPnl) && nullableNumber(v.triggerThreshold) && nullableNumber(v.realizedShortfall)
  && nullableText(v.triggeredAtUtc) && (v.triggeredAtUtc === null || text(clock) && Date.parse(v.triggeredAtUtc) <= Date.parse(clock));
function job(v: unknown): v is ReplayJob {
  return object(v) && ['id', 'date', 'state', 'message', 'calendarStatus', 'mode'].every(key => text(v[key]))
    && (v.attempt === undefined || number(v.attempt) && Number.isSafeInteger(v.attempt) && v.attempt >= 0)
    && nullableText(v.catalogSource) && strings(v.warnings) && number(v.total) && number(v.finished) && number(v.run)
    && typeof v.canReplay === 'boolean' && typeof v.reportsAvailable === 'boolean' && Array.isArray(v.instruments)
    && v.instruments.every(row => object(row) && text(row.id) && number(row.token) && text(row.role) && text(row.state)
      && number(row.rows) && typeof row.cached === 'boolean' && nullableText(row.error));
}
function view(v: unknown): v is ReplayView {
  if (!object(v) || !job(v.job) || !nullableText(v.simulatedTimeUtc) || typeof v.paused !== 'boolean' || typeof v.complete !== 'boolean'
    || !number(v.openPositions) || !number(v.pendingEntries) || !nullableNumber(v.netSessionPnl) || !strings(v.reportFiles)) return false;
  if (v.playbackKind !== undefined && v.playbackKind !== 'Strategy' && v.playbackKind !== 'PriceOnly') return false;
  if (v.revision !== undefined && (!number(v.revision) || !Number.isSafeInteger(v.revision) || v.revision < 0)) return false;
  if (v.journalPending !== undefined && typeof v.journalPending !== 'boolean') return false;
  if (v.diagnosticError !== undefined && !nullableText(v.diagnosticError)) return false;
  if (v.serverInstanceId !== undefined && v.serverInstanceId !== null && (!text(v.serverInstanceId) || !v.serverInstanceId)) return false;
  if (v.presetProgress !== undefined && v.presetProgress !== null && (!object(v.presetProgress) || !text(v.presetProgress.id)
    || !number(v.presetProgress.number) || !Number.isSafeInteger(v.presetProgress.number) || v.presetProgress.number < 1
    || !number(v.presetProgress.total) || !Number.isSafeInteger(v.presetProgress.total) || v.presetProgress.total < v.presetProgress.number
    || typeof v.presetProgress.allPresets !== 'boolean')) return false;
  if (v.timing !== undefined && v.timing !== null && (!object(v.timing) || !['Paced', 'Maximum'].includes(String(v.timing.mode))
    || !number(v.timing.requestedSpeed) || v.timing.requestedSpeed <= 0 || !nullableNumber(v.timing.achievedSpeed) || !number(v.timing.lagMilliseconds) || !number(v.timing.processingMilliseconds))) return false;
  if (v.chartLabel !== undefined && !text(v.chartLabel)) return false;
  if (v.chartInstrumentId !== undefined && !nullableText(v.chartInstrumentId)) return false;
  if (v.chartInstruments !== undefined && (!Array.isArray(v.chartInstruments)
    || !v.chartInstruments.every(item => object(item) && text(item.id) && text(item.role)))) return false;
  if (v.paperRunId !== undefined && !nullableText(v.paperRunId)) return false;
  if (v.paper !== undefined && v.paper !== null && (!object(v.paper)
    || !['startingCapital', 'cash', 'availableCash', 'reservedCash', 'realizedNetPnl', 'paidFees', 'acceptedAttempts'].every(key => number(object(v.paper) ? v.paper[key] : undefined))
    || typeof v.paper.entriesPaused !== 'boolean' || !Array.isArray(v.paper.pendingIntents) || !Array.isArray(v.paper.positions)
    || !v.paper.pendingIntents.every(row => object(row) && text(row.id) && text(row.contractId) && text(row.setupId) && text(row.acceptedAtUtc) && text(row.expiresAtUtc) && number(row.reservedCash))
    || !v.paper.positions.every(row => object(row) && text(row.id) && text(row.contractId) && number(row.units) && text(row.entryTimeUtc) && number(row.entryPrice)
      && nullableNumber(row.grossPnl) && nullableNumber(row.netPnl) && text(row.policyAction) && strings(row.reasons) && nullableText(row.pendingExitReason)
      && (row.profitExit === undefined || row.profitExit === null || profitEvidence(row.profitExit, v.simulatedTimeUtc))
      && (row.currentProfitExitThreshold === undefined || nullableNumber(row.currentProfitExitThreshold))))) return false;
  if (!Array.isArray(v.bars) || !v.bars.every(bar => object(bar) && text(bar.id) && text(bar.startUtc) && text(bar.endUtc)
    && text(bar.availableAtUtc) && ['open', 'high', 'low', 'close'].every(key => number(bar[key]))
    && text(v.simulatedTimeUtc) && Date.parse(bar.endUtc) <= Date.parse(v.simulatedTimeUtc) && Date.parse(bar.availableAtUtc) <= Date.parse(v.simulatedTimeUtc))) return false;
  return (v.feature === null || object(v.feature) && ['openingHigh', 'openingLow', 'vwap', 'aboveWeight', 'belowWeight', 'missingWeight'].every(key => nullableNumber(v.feature && object(v.feature) ? v.feature[key] : undefined))
    && nullableText(v.feature.direction) && nullableText(v.feature.setupId)
    && (v.feature.directionalEvidence === undefined || v.feature.directionalEvidence === null || directionalEvidence(v.feature.directionalEvidence, v.simulatedTimeUtc)))
     && (v.settings === null || object(v.settings) && text(v.settings.version) && text(v.settings.preset)
       && (v.settings.profitExit === undefined || v.settings.profitExit === null || profitPolicy(v.settings.profitExit))
      && ['grossStop', 'maximumNetRisk', 'candidateBatchSize', 'spreadTicks', 'slippageTicks'].every(key => number(object(v.settings) ? v.settings[key] : undefined)))
    && Array.isArray(v.constituents) && v.constituents.every(row => object(row) && text(row.instrumentId) && nullableNumber(row.weightPercent)
      && nullableNumber(row.close) && nullableNumber(row.vwap) && nullableText(row.periodEndUtc) && text(row.state)
      && (row.periodEndUtc === null || text(v.simulatedTimeUtc) && Date.parse(row.periodEndUtc) <= Date.parse(v.simulatedTimeUtc)))
    && Array.isArray(v.decisions) && v.decisions.every(row => object(row) && text(row.contractId) && text(row.class) && text(row.action) && strings(row.reasons))
    && Array.isArray(v.trades) && v.trades.every(row => object(row) && text(row.id) && text(row.contractId) && number(row.units)
       && text(row.entryTimeUtc) && number(row.entryPrice) && nullableText(row.exitKnownAtUtc) && nullableNumber(row.exitPrice)
       && nullableNumber(row.netPnl) && nullableText(row.exitReason) && text(row.evidenceClass)
       && (row.profitExit === undefined || row.profitExit === null || profitEvidence(row.profitExit, v.simulatedTimeUtc)));
}
async function request<T>(path: string, check: (v: unknown) => v is T, signal: AbortSignal, body?: unknown, expected = 200, timeout = 5000): Promise<T> {
  const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', cache: 'no-store',
    signal: AbortSignal.any([signal, AbortSignal.timeout(timeout)]),
    headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  const json: unknown = await response.json().catch(() => null);
  if (response.status !== expected) throw new Error(object(json) && text(json.detail) ? json.detail : `Replay API returned HTTP ${response.status}.`);
  if (!check(json)) throw new Error('Replay response is invalid or contains candles beyond the supplied replay clock. Retry the connection.');
  return json;
}
export const readReplayStatus = (signal: AbortSignal) => request('/api/replay/status', (v): v is ReplayStatus => object(v)
  && text(v.credentialStatus) && text(v.latestCompletedDate) && v.guideUrl === '/api/replay/setup' && text(v.metadataMessage), signal);
export const prepareReplay = (date: string, refresh: boolean, signal: AbortSignal) => request('/api/replay/jobs', job, signal, { date, refresh }, 202);
export async function readReplay(id: string, signal: AbortSignal, instrumentId?: string | null): Promise<ReplayView> {
  const result = await request(`/api/replay/jobs/${encodeURIComponent(id)}?light=true${instrumentId ? `&instrumentId=${encodeURIComponent(instrumentId)}` : ''}`, view, signal);
  if (result.job.id !== id) throw new Error('Replay API returned the wrong job.');
  return result;
}
export async function replayCommand(id: string, action: string, speed: number, signal: AbortSignal, instrumentId?: string | null, profitExit?: ProfitExitSettings): Promise<ReplayView> {
  const result = await request(`/api/replay/jobs/${encodeURIComponent(id)}/commands${instrumentId ? `?instrumentId=${encodeURIComponent(instrumentId)}` : ''}`, view, signal, { action, speed, ...(profitExit ? { profitExit } : {}) });
  if (result.job.id !== id) throw new Error('Replay command returned the wrong job.');
  return result;
}
export interface PaperLogRow { sequence: number; runId: string; timeUtc: string; indexId: string; contractId: string; category: string; action: string; setupId: string | null; reasonsJson: string }
export const readPaperLog = (id: string, category: string, signal: AbortSignal) => request(`/api/replay/jobs/${encodeURIComponent(id)}/paper-log?limit=200${category ? `&category=${encodeURIComponent(category)}` : ''}`,
  (v): v is PaperLogRow[] => Array.isArray(v) && v.every(row => object(row) && number(row.sequence) && text(row.runId) && text(row.timeUtc)
    && text(row.indexId) && text(row.contractId) && text(row.category) && text(row.action) && nullableText(row.setupId) && text(row.reasonsJson)), signal);

export interface HealthRow { id: string; role: string; state: string; rows: number; expectedMinutes: number | null; missingMinutes: number | null; cached: boolean; error: string | null; impact: string;
  recoveryAction?: string;
  issues: { code: string; detail: string; startUtc: string | null }[]; missingIntervals: { startUtc: string; endUtc: string }[] }
export interface HistoricalQualification { qualified: boolean; decisionTimes: number; incompleteDecisionTimes: number; maximumUnknownOptions: number; firstIncompleteAtUtc: string | null; issues: string[] }
export interface DataHealth { total: number; processed: number; complete: number; sparse: number; empty: number; failed: number; pending: number; invalid: number; cached: number; requiredAffected: number; readiness: string; instruments: HealthRow[]; warnings: string[]; qualification?: HistoricalQualification | null; jobId?: string | null; attempt?: number | null; serverInstanceId?: string | null }
export interface RunInfo { runId: string; jobId: string; date: string; preset: string; datasetHash: string; inputHash: string; simulatedTimeUtc: string | null; realizedNetPnl: number | null; trades: number; complete: boolean; qualified?: boolean | null; profitExit?: ProfitExitSettings | null }
export interface RunDiagnostics { initializing?: boolean; operations: { sequence: number; run: number; preset: string | null; wallTimeUtc: string; simulatedTimeUtc: string | null; severity: string; code: string; detailJson: string }[]; runs: RunInfo[] }
export interface EventPage { runId: string; rows: PaperLogRow[]; nextBeforeSequence: number | null }
export interface RunComparison { left: RunInfo; right: RunInfo; configurationChanged: boolean; hasMore: boolean;
  differences: { timeUtc: string; indexId: string; contractId: string; category: string; leftAction: string | null; rightAction: string | null; leftReasons: string | null; rightReasons: string | null }[] }
const runInfo = (v: unknown): v is RunInfo => object(v) && ['runId', 'jobId', 'date', 'preset', 'datasetHash', 'inputHash'].every(key => text(v[key])) && nullableText(v.simulatedTimeUtc) && nullableNumber(v.realizedNetPnl) && number(v.trades) && typeof v.complete === 'boolean'
  && (v.qualified === undefined || v.qualified === null || typeof v.qualified === 'boolean')
  && (v.profitExit === undefined || v.profitExit === null || profitPolicy(v.profitExit));
export const readDataHealth = (id: string, signal: AbortSignal) => request(`/api/replay/jobs/${encodeURIComponent(id)}/data-health`, (v): v is DataHealth => object(v)
  && (v.jobId === undefined || v.jobId === null || v.jobId === id)
  && (v.serverInstanceId === undefined || v.serverInstanceId === null || text(v.serverInstanceId) && v.serverInstanceId.length > 0)
  && (v.attempt === undefined || v.attempt === null || number(v.attempt) && Number.isSafeInteger(v.attempt) && v.attempt >= 0)
  && ['total', 'processed', 'complete', 'sparse', 'empty', 'failed', 'pending', 'cached', 'requiredAffected', 'invalid'].every(key => number(v[key])) && text(v.readiness) && strings(v.warnings)
  && (v.qualification === undefined || v.qualification === null || object(v.qualification) && typeof v.qualification.qualified === 'boolean'
    && number(v.qualification.decisionTimes) && number(v.qualification.incompleteDecisionTimes) && number(v.qualification.maximumUnknownOptions)
    && nullableText(v.qualification.firstIncompleteAtUtc) && strings(v.qualification.issues))
  && Array.isArray(v.instruments) && v.instruments.every(row => object(row) && text(row.id) && text(row.role) && text(row.state) && number(row.rows) && nullableNumber(row.expectedMinutes) && nullableNumber(row.missingMinutes) && text(row.impact) && nullableText(row.error)
    && (row.recoveryAction === undefined || text(row.recoveryAction)) && Array.isArray(row.issues) && Array.isArray(row.missingIntervals)), signal);
export const readDiagnostics = (id: string, signal: AbortSignal) => request(`/api/replay/jobs/${encodeURIComponent(id)}/diagnostics`, (v): v is RunDiagnostics => object(v) && Array.isArray(v.runs) && v.runs.every(runInfo)
  && Array.isArray(v.operations) && v.operations.every(row => object(row) && number(row.sequence) && number(row.run) && text(row.wallTimeUtc) && text(row.code) && text(row.severity) && text(row.detailJson)), signal);
export const readRuns = (date: string, signal: AbortSignal) => request(`/api/replay/runs?date=${encodeURIComponent(date)}`, (v): v is RunInfo[] => Array.isArray(v) && v.every(runInfo), signal);
export const readEventPage = (id: string, runId: string, category: string, search: string, before: number | null, signal: AbortSignal) => request(`/api/replay/jobs/${encodeURIComponent(id)}/events?${new URLSearchParams({ runId, limit: '25', ...(category ? { category } : {}), ...(search ? { search } : {}), ...(before === null ? {} : { before: String(before) }) })}`,
  (v): v is EventPage => object(v) && v.runId === runId && nullableNumber(v.nextBeforeSequence) && Array.isArray(v.rows) && v.rows.every(row => object(row) && number(row.sequence) && row.runId === runId && text(row.action) && text(row.reasonsJson) && text(row.contractId) && text(row.timeUtc) && text(row.category)), signal);
export const readEventEvidence = (id: string, runId: string, sequence: number, signal: AbortSignal) => request(`/api/replay/jobs/${encodeURIComponent(id)}/events/${sequence}?runId=${encodeURIComponent(runId)}`,
  (v): v is ReplayView['decisions'][number] => object(v) && text(v.contractId) && text(v.action) && text(v.class) && strings(v.reasons) && Array.isArray(v.gates), signal);
export const compareRuns = (id: string, left: string, right: string, signal: AbortSignal) => request(`/api/replay/jobs/${encodeURIComponent(id)}/compare?${new URLSearchParams({ left, right })}`,
  (v): v is RunComparison => object(v) && runInfo(v.left) && runInfo(v.right) && typeof v.configurationChanged === 'boolean' && typeof v.hasMore === 'boolean' && Array.isArray(v.differences)
    && v.differences.every(row => object(row) && text(row.timeUtc) && text(row.contractId) && text(row.category) && nullableText(row.leftAction) && nullableText(row.rightAction) && nullableText(row.leftReasons) && nullableText(row.rightReasons)), signal, undefined, 200, 120000);
