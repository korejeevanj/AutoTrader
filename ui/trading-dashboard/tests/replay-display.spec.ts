import { expect, test } from '@playwright/test';
import { ReplayDisplay, type ReplayDisplayTransport, type ReplayDisplayTimers } from '../src/replayDisplay';
import type { DataHealth, HistoricalQualification, PaperLogRow, ProfitExitSettings, ReplayEvidence, ReplayJob, ReplayStatus, ReplayView } from '../src/replayApi';

const date = '2026-10-16';
const jobId = 'job-2026-10-16';
const settle = () => new Promise<void>(resolve => setImmediate(resolve));
const qualified: HistoricalQualification = { qualified: true, decisionTimes: 4, incompleteDecisionTimes: 0, maximumUnknownOptions: 0, firstIncompleteAtUtc: null, issues: [] };

// The verdict the server publishes with every view; the display never derives it.
function evidence(overrides: Partial<ReplayEvidence> = {}): ReplayEvidence {
  return { label: 'Evidence qualified', jobLabel: 'Ready', preparing: false, readiness: 'Qualified historical inputs — candle model only',
    issues: 0, qualification: qualified, ...overrides };
}
const acquiring = (): ReplayEvidence => evidence({ label: 'Evidence awaiting validation', jobLabel: 'Downloading', preparing: true,
  readiness: 'Acquisition in progress — evidence awaiting validation', qualification: null });

function view(revision: number, serverInstanceId = 'server-A', selectedDate = date): ReplayView {
  return { evidence: evidence(), serverInstanceId, revision, job: { id: `job-${selectedDate}`, date: selectedDate, state: 'Ready', message: `Revision ${revision}`,
    calendarStatus: 'TradingDay', catalogSource: 'Fixture catalogue', warnings: [], instruments: [], total: 1, finished: 1,
    canReplay: true, mode: 'PAPER + REPLAY', run: 1, reportsAvailable: false, preparationComplete: true, attempt: 1 },
    simulatedTimeUtc: null, paused: true, complete: false, bars: [], feature: null, constituents: [], decisions: [], trades: [],
    openPositions: 0, pendingEntries: 0, netSessionPnl: null, settings: null, reportFiles: [], paperRunId: 'run-A' };
}

function health(attempt = 1, serverInstanceId = 'server-A'): DataHealth {
  return { jobId, attempt, serverInstanceId, total: 1, processed: 1, complete: 1, sparse: 0, empty: 0, failed: 0,
    pending: 0, invalid: 0, cached: 1, requiredAffected: 0, readiness: 'Qualified inputs', instruments: [], warnings: [],
    qualification: { qualified: true, decisionTimes: 4, incompleteDecisionTimes: 0, maximumUnknownOptions: 0, firstIncompleteAtUtc: null, issues: [] } };
}

function paperEvent(runId: string): PaperLogRow {
  return { sequence: 1, runId, timeUtc: '2026-10-16T04:00:00Z', indexId: 'BANKNIFTY', contractId: `option-${runId}`,
    category: 'CANDLE_RESEARCH', action: 'HYPOTHETICAL_FILLED', setupId: 'setup', reasonsJson: '[]' };
}

class Pending<T> {
  resolve!: (value: T) => void;
  reject!: (error: unknown) => void;
  readonly promise = new Promise<T>((resolve, reject) => { this.resolve = resolve; this.reject = reject; });
  constructor(readonly signal: AbortSignal) { }
}

class Transport implements ReplayDisplayTransport {
  readonly snapshots: (Pending<ReplayView> & { id: string; instrumentId: string | null | undefined })[] = [];
  readonly health: Pending<DataHealth>[] = [];
  readonly logs: Pending<PaperLogRow[]>[] = [];
  readonly preparations: (Pending<ReplayJob> & { date: string; refresh: boolean })[] = [];
  readonly commands: (Pending<ReplayView> & { id: string; action: string; speed: number; instrumentId: string | null | undefined; profitExit: ProfitExitSettings | undefined })[] = [];
  readReplayStatus = async (): Promise<ReplayStatus> => ({ credentialStatus: 'ConfiguredNotVerified', latestCompletedDate: date,
    guideUrl: '/api/replay/setup', metadataMessage: 'Fixture status' });
  readReplay = (id: string, signal: AbortSignal, instrumentId?: string | null) => {
    const pending = Object.assign(new Pending<ReplayView>(signal), { id, instrumentId }); this.snapshots.push(pending); return pending.promise;
  };
  readDataHealth = (_id: string, signal: AbortSignal) => { const pending = new Pending<DataHealth>(signal); this.health.push(pending); return pending.promise; };
  readPaperLog = (_id: string, _category: string, signal: AbortSignal) => { const pending = new Pending<PaperLogRow[]>(signal); this.logs.push(pending); return pending.promise; };
  prepareReplay = (selected: string, refresh: boolean, signal: AbortSignal) => {
    const pending = Object.assign(new Pending<ReplayJob>(signal), { date: selected, refresh }); this.preparations.push(pending); return pending.promise;
  };
  replayCommand = (id: string, action: string, speed: number, signal: AbortSignal, instrumentId?: string | null, profitExit?: ProfitExitSettings) => {
    const pending = Object.assign(new Pending<ReplayView>(signal), { id, action, speed, instrumentId, profitExit }); this.commands.push(pending); return pending.promise;
  };
}

class Timers implements ReplayDisplayTimers {
  private now = 0;
  private sequence = 0;
  readonly pending = new Map<number, { at: number; callback: () => void }>();
  setTimeout = (callback: () => void, delay: number) => { const id = ++this.sequence; this.pending.set(id, { at: this.now + delay, callback }); return id; };
  clearTimeout = (id: number) => { this.pending.delete(id); };
  advance(milliseconds: number) {
    this.now += milliseconds;
    for (const [id, timer] of [...this.pending]) if (timer.at <= this.now) { this.pending.delete(id); timer.callback(); }
  }
}

function harness() {
  const values = new Map<string, string>([['replay-date', date], ['replay-job', jobId]]);
  const storage = { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); } };
  const transport = new Transport(); const timers = new Timers(); let selection = 0;
  const display = new ReplayDisplay({ transport, timers, storage, selectionId: () => `selection-${++selection}` });
  display.start();
  return { display, transport, timers, storage };
}

test('a late command cannot replace a newer poll or leave controls busy', async () => {
  const { display, transport, timers } = harness();
  try {
    transport.snapshots[0]!.resolve(view(10)); await settle();
    const command = display.command('step');
    timers.advance(1500);
    transport.snapshots[1]!.resolve(view(12)); await settle();
    transport.commands[0]!.resolve(view(11)); await command;
    expect(display.getSnapshot().view?.revision).toBe(12);
    expect(display.getSnapshot().view?.job.message).toBe('Revision 12');
    expect(display.getSnapshot().busy).toBe(false);
  } finally { display.stop(); }
});

test('overlapping commands remain busy until both finish and a failed speed change restores its acknowledged value', async () => {
  const { display, transport } = harness();
  try {
    const initial = view(1); initial.timing = { mode: 'Paced', requestedSpeed: 60, achievedSpeed: null, lagMilliseconds: 0, processingMilliseconds: 0 };
    transport.snapshots[0]!.resolve(initial); await settle();
    const first = display.command('step'); const second = display.command('setSpeed', 1);
    expect(display.getSnapshot().speed).toBe(1);
    transport.commands[0]!.resolve({ ...initial, revision: 2 }); await first;
    expect(display.getSnapshot().busy).toBe(true);
    transport.commands[1]!.reject(new Error('Speed change rejected')); await second;
    expect(display.getSnapshot().busy).toBe(false);
    expect(display.getSnapshot().speed).toBe(60);
    expect(display.getSnapshot().error).toBe('Speed change rejected');
  } finally { display.stop(); }
});

test('changing the inspected instrument rejects its obsolete poll and supplies the new context to commands', async () => {
  const { display, transport, timers } = harness();
  try {
    const initial = view(1); initial.chartInstrumentId = 'future';
    transport.snapshots[0]!.resolve(initial); await settle();
    timers.advance(1500); const obsolete = transport.snapshots[1]!;
    display.setInspectionContext('option-A');
    expect(obsolete.signal.aborted).toBe(true);
    expect(transport.snapshots[2]!.instrumentId).toBe('option-A');
    const selected = view(2); selected.chartInstrumentId = 'option-A';
    transport.snapshots[2]!.resolve(selected); await settle();
    obsolete.resolve({ ...initial, revision: 9 }); await settle();
    expect(display.getSnapshot().view?.chartInstrumentId).toBe('option-A');
    const command = display.command('step');
    expect(transport.commands[0]!.instrumentId).toBe('option-A');
    transport.commands[0]!.resolve({ ...selected, revision: 3 }); await command;
  } finally { display.stop(); }
});

for (const revision of [2, 3]) {
  test(`a command with an obsolete chart projection cannot replace the current inspection at revision ${revision}`, async () => {
    const { display, transport } = harness();
    try {
      display.setInspectionContext('future');
      const initial = view(1); initial.chartInstrumentId = 'future'; initial.chartLabel = 'Futures context';
      transport.snapshots.at(-1)!.resolve(initial); await settle();
      const command = display.command('step');
      display.setInspectionContext('option-B');
      const selected = view(2); selected.chartInstrumentId = 'option-B'; selected.chartLabel = 'Option B context';
      transport.snapshots.at(-1)!.resolve(selected); await settle();
      transport.commands[0]!.resolve({ ...initial, revision }); await command;
      expect(display.getSnapshot().view?.chartInstrumentId).toBe('option-B');
      expect(display.getSnapshot().view?.chartLabel).toBe('Option B context');
      expect(display.getSnapshot().busy).toBe(false);
      expect(transport.snapshots.at(-1)!.instrumentId).toBe('option-B');
      expect(transport.commands).toHaveLength(1);
      transport.snapshots.at(-1)!.reject(new Error('Current projection unavailable')); await settle();
      expect(display.getSnapshot().view?.chartInstrumentId).toBe('option-B');
      expect(display.getSnapshot().error).toBe('Current projection unavailable');
    } finally { display.stop(); }
  });
}

test('a server restart accepts reset revisions and rejects retired-server commands, errors and health', async () => {
  const { display, transport, timers } = harness();
  try {
    transport.snapshots[0]!.resolve(view(100)); transport.health[0]!.resolve(health()); await settle();
    expect(display.getSnapshot().qualification?.qualified).toBe(true);
    expect(display.getSnapshot().health?.serverInstanceId).toBe('server-A');
    const oldCommand = display.command('step'); const oldError = display.command('setSpeed', 1);
    timers.advance(1500);
    transport.snapshots[1]!.resolve(view(1, 'server-B')); await settle();
    expect(display.getSnapshot().health).toBeNull(); // rows from the retired server are never shown
    transport.health.at(-1)!.resolve(health(1, 'server-B')); await settle();
    expect(display.getSnapshot().health?.serverInstanceId).toBe('server-B');
    transport.commands[0]!.resolve(view(101)); transport.commands[1]!.reject(new Error('Retired-server failure'));
    await Promise.all([oldCommand, oldError]);
    expect(display.getSnapshot().view?.serverInstanceId).toBe('server-B');
    expect(display.getSnapshot().view?.revision).toBe(1);
    expect(display.getSnapshot().qualification?.qualified).toBe(true);
    expect(display.getSnapshot().error).toBe('');
    expect(display.getSnapshot().busy).toBe(false);
    timers.advance(1500);
    transport.snapshots.at(-1)!.resolve(view(102)); await settle();
    expect(display.getSnapshot().view?.serverInstanceId).toBe('server-B');
  } finally { display.stop(); }
});

test('the verdict comes from the accepted view, so health rows can neither qualify nor unqualify a job', async () => {
  const { display, transport } = harness();
  try {
    transport.snapshots[0]!.resolve(view(1)); await settle();
    expect(display.getSnapshot().qualification?.qualified).toBe(true); // no health response has arrived
    expect(display.getSnapshot().evidenceLabel).toBe('Evidence qualified');
    expect(display.getSnapshot().health).toBeNull();
    transport.health[0]!.resolve(health()); await settle();
    expect(display.getSnapshot().health?.readiness).toBe(evidence().readiness);

    const refresh = display.command('refreshGaps');
    const downloading = view(2); downloading.job.attempt = 2; downloading.job.state = 'Downloading';
    downloading.job.preparationComplete = false; downloading.job.canReplay = false; downloading.job.preparingData = true;
    downloading.evidence = acquiring();
    transport.commands[0]!.resolve(downloading); await refresh;
    expect(display.getSnapshot().qualification).toBeNull();
    expect(display.getSnapshot().preparing).toBe(true);
    expect(display.getSnapshot().evidenceLabel).toBe('Evidence awaiting validation');
    expect(display.getSnapshot().jobLabel).toBe('Downloading');
    expect(display.getSnapshot().health).toBeNull(); // the earlier attempt's rows no longer describe this job

    const wrongJob = health(2); delete wrongJob.jobId;
    transport.health.at(-1)!.resolve(wrongJob); await settle();
    expect(display.getSnapshot().health).toBeNull();
    display.retry(); transport.health.at(-1)!.resolve(health(2)); await settle();
    expect(display.getSnapshot().qualification).toBeNull();
    expect(display.getSnapshot().health?.readiness).toContain('Acquisition in progress');

    const ready = view(3); ready.job.attempt = 2;
    transport.snapshots.at(-1)!.resolve(ready); await settle();
    expect(display.getSnapshot().qualification?.qualified).toBe(true);
    display.retry(); transport.health.at(-1)!.reject(new Error('Health storage unavailable')); await settle();
    expect(display.getSnapshot().health).toBeNull();
    expect(display.getSnapshot().healthError).toBe('Health storage unavailable');
    expect(display.getSnapshot().qualification?.qualified).toBe(true); // the rows failed, the verdict did not
    expect(display.getSnapshot().evidenceLabel).toBe('Evidence qualified');
  } finally { display.stop(); }
});

test('an older view cannot replace a newer verdict and the issue count follows the accepted view', async () => {
  const { display, transport, timers } = harness();
  try {
    const incomplete = view(5); incomplete.evidence = evidence({ label: 'Evidence incomplete', jobLabel: 'ReadyWithWarnings', issues: 7,
      readiness: 'Incomplete — diagnostic replay only', qualification: { ...qualified, qualified: false, incompleteDecisionTimes: 4 } });
    transport.snapshots[0]!.resolve(incomplete); await settle();
    expect(display.getSnapshot().evidenceLabel).toBe('Evidence incomplete');
    expect(display.getSnapshot().healthIssues).toBe(7);
    timers.advance(1500);
    transport.snapshots[1]!.resolve(view(4)); await settle(); // stale revision carrying a rosier verdict
    expect(display.getSnapshot().evidenceLabel).toBe('Evidence incomplete');
    expect(display.getSnapshot().qualification?.qualified).toBe(false);
  } finally { display.stop(); }
});

test('switching runs clears prior lifecycle evidence and rejects late or misrouted events', async () => {
  const { display, transport, timers } = harness();
  try {
    transport.snapshots[0]!.resolve(view(1)); await settle();
    transport.logs[0]!.resolve([paperEvent('run-A')]); await settle();
    expect(display.getSnapshot().paperLog.map(row => row.runId)).toEqual(['run-A']);
    timers.advance(2500);
    const oldLog = transport.logs[1]!;
    const restart = display.command('restart');
    const next = view(2); next.paperRunId = 'run-B'; next.job.run = 2;
    transport.commands[0]!.resolve(next); await restart;
    expect(display.getSnapshot().paperLog).toEqual([]);
    expect(oldLog.signal.aborted).toBe(true);
    oldLog.resolve([paperEvent('run-A')]); await settle();
    expect(display.getSnapshot().paperLog).toEqual([]);
    transport.logs.at(-1)!.resolve([paperEvent('run-A'), paperEvent('run-B')]); await settle();
    expect(display.getSnapshot().paperLog.map(row => row.runId)).toEqual(['run-B']);
  } finally { display.stop(); }
});

test('obsolete preparations are cancelled without replacing the selected day or its saved job', async () => {
  const { display, transport, storage } = harness();
  try {
    const first = display.choose('2026-10-09');
    const second = display.choose('2026-10-12');
    const selected = view(1, 'server-A', '2026-10-12');
    transport.preparations[1]!.resolve(selected.job); await second;
    transport.snapshots.at(-1)!.resolve(selected); await settle();
    const obsolete = view(1, 'server-A', '2026-10-09'); obsolete.job.state = 'Queued'; obsolete.job.canReplay = false; obsolete.job.preparingData = true;
    transport.preparations[0]!.resolve(obsolete.job); await settle();
    expect(transport.commands.at(-1)?.action).toBe('cancelPreparation');
    expect(transport.commands.at(-1)?.id).toBe('job-2026-10-09');
    transport.commands.at(-1)!.resolve(obsolete); await first;
    transport.snapshots[0]!.resolve(view(99)); await settle();
    expect(display.getSnapshot().date).toBe('2026-10-12');
    expect(display.getSnapshot().view?.job.id).toBe('job-2026-10-12');
    expect(storage.getItem('replay-job')).toBe('job-2026-10-12');
    expect(display.getSnapshot().error).toBe('');
    expect(display.getSnapshot().busy).toBe(false);
  } finally { display.stop(); }
});

test('changing days aborts obsolete commands without clearing the new preparation busy state', async () => {
  const { display, transport } = harness();
  try {
    transport.snapshots[0]!.resolve(view(1)); await settle();
    const command = display.command('step');
    const choose = display.choose('2026-10-09');
    expect(transport.commands[0]!.signal.aborted).toBe(true);
    transport.commands[0]!.reject(new Error('Obsolete command failed')); await command;
    expect(display.getSnapshot().busy).toBe(true);
    expect(display.getSnapshot().view).toBeNull();
    expect(display.getSnapshot().error).toBe('');
    const selected = view(1, 'server-A', '2026-10-09');
    transport.preparations[0]!.resolve(selected.job); await choose;
    transport.snapshots.at(-1)!.resolve(selected); await settle();
    expect(display.getSnapshot().busy).toBe(false);
    expect(display.getSnapshot().view?.job.date).toBe('2026-10-09');
  } finally { display.stop(); }
});

test('a selection superseded in browser storage finishes locally without overwriting the newer saved selection', async () => {
  const { display, transport, storage } = harness();
  try {
    const choose = display.choose('2026-10-09');
    storage.setItem('replay-selection', 'another-tab'); storage.setItem('replay-date', '2026-10-12');
    storage.setItem('replay-job', 'job-2026-10-12');
    const obsolete = view(1, 'server-A', '2026-10-09'); obsolete.job.state = 'Queued'; obsolete.job.preparingData = true;
    transport.preparations[0]!.resolve(obsolete.job); await settle();
    transport.commands.at(-1)!.resolve(obsolete); await choose;
    expect(display.getSnapshot().busy).toBe(false);
    expect(display.getSnapshot().view).toBeNull();
    expect(storage.getItem('replay-job')).toBe('job-2026-10-12');
  } finally { display.stop(); }
});

test('stopping aborts reads and commands, clears timers and prevents late notifications before reactivation', async () => {
  const { display, transport, timers } = harness();
  let notifications = 0;
  const unsubscribe = display.subscribe(() => { notifications++; });
  try {
    transport.snapshots[0]!.resolve(view(1)); await settle();
    const command = display.command('step');
    timers.advance(1500);
    display.stop();
    const stopped = display.getSnapshot(); const count = notifications;
    expect(timers.pending.size).toBe(0);
    expect(transport.snapshots[1]!.signal.aborted).toBe(true);
    expect(transport.health[0]!.signal.aborted).toBe(true);
    expect(transport.logs[0]!.signal.aborted).toBe(true);
    expect(transport.commands[0]!.signal.aborted).toBe(true);
    transport.snapshots[1]!.resolve(view(2)); transport.health[0]!.resolve(health());
    transport.logs[0]!.resolve([paperEvent('run-A')]); transport.commands[0]!.resolve(view(3));
    await command; await settle();
    expect(display.getSnapshot()).toBe(stopped);
    expect(notifications).toBe(count);
    expect(timers.pending.size).toBe(0);
    display.start(); display.start();
    expect(transport.snapshots).toHaveLength(3);
    transport.snapshots[2]!.resolve(view(4)); await settle();
    expect(display.getSnapshot().view?.revision).toBe(4);
    expect(display.getSnapshot().busy).toBe(false);
  } finally { display.stop(); unsubscribe(); }
});
