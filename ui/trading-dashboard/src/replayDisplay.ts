import { errorMessage } from './api';
import type { DataHealth, HistoricalQualification, PaperLogRow, ProfitExitSettings, ReplayJob, ReplayStatus, ReplayView } from './replayApi';

export type ReplayDisplayTransport = Pick<typeof import('./replayApi'),
  'readReplayStatus' | 'prepareReplay' | 'readReplay' | 'replayCommand' | 'readDataHealth' | 'readPaperLog'>;
export interface ReplayDisplayTimers {
  setTimeout(callback: () => void, milliseconds: number): number;
  clearTimeout(id: number): void;
}
interface Environment {
  transport: ReplayDisplayTransport;
  timers: ReplayDisplayTimers;
  storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
  selectionId(): string;
}
export interface ReplayDisplaySnapshot {
  date: string; id: string | null; view: ReplayView | null; status: ReplayStatus | null;
  health: DataHealth | null; qualification: HistoricalQualification | null; paperLog: PaperLogRow[];
  error: string; statusError: string; healthError: string; logError: string; notice: string;
  busy: boolean; speed: number;
  preparing: boolean; evidenceLabel: string; jobLabel: string | undefined; credentialLabel: string; healthIssues: number | null;
}
type Poll = 'status' | 'view' | 'health' | 'log';
interface PollScope { abort: AbortController; timer: number | null }
// The server decides what counts as preparing; the client never re-derives it from the state name.
const preparing = (job: ReplayJob) => job.preparingData === true;

/** Owns selection, concurrent reads/commands and accepted evidence. Rendering never reconciles responses. */
export class ReplayDisplay {
  private readonly listeners = new Set<() => void>();
  private readonly polls = new Map<Poll, PollScope>();
  private readonly commands = new Set<AbortController>();
  private readonly retiredServers = new Set<string>();
  private server: string | null = null;
  private active = false;
  private generation = 0;
  private preparingRequest = false;
  private date: string;
  private id: string | null;
  private view: ReplayView | null = null;
  private status: ReplayStatus | null = null;
  private health: DataHealth | null = null;
  private paperLog: PaperLogRow[] = [];
  private error = ''; private statusError = ''; private healthError = ''; private logError = ''; private notice = '';
  private speed = 60;
  private inspectionContext: string | null = null;
  private snapshot: ReplayDisplaySnapshot;

  constructor(private readonly environment: Environment) {
    this.date = environment.storage.getItem('replay-date') ?? '';
    this.id = environment.storage.getItem('replay-job');
    this.snapshot = this.project();
  }
  getSnapshot = () => this.snapshot;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  start() { if (!this.active) { this.active = true; this.publish(); this.retry(); } }
  stop() {
    this.active = false;
    for (const key of this.polls.keys()) this.stopPoll(key);
    for (const command of this.commands) command.abort();
    this.commands.clear();
    this.publish();
  }
  retry = () => {
    if (!this.active) return;
    this.pollStatus(); this.pollView(); this.pollHealth(); this.pollLog();
  };
  setInspectionContext = (instrumentId: string | null) => {
    if (instrumentId === this.inspectionContext) return;
    this.inspectionContext = instrumentId; this.pollView();
  };

  choose = async (date: string, refresh = false) => {
    const previous = this.view; const generation = ++this.generation;
    const selection = this.environment.selectionId(); const storage = this.environment.storage;
    storage.setItem('replay-selection', selection); storage.setItem('replay-date', date); storage.removeItem('replay-job');
    for (const key of this.polls.keys()) this.stopPoll(key);
    for (const command of this.commands) command.abort();
    this.commands.clear();
    this.date = date; this.id = null; this.view = null; this.health = null; this.paperLog = [];
    this.error = ''; this.healthError = ''; this.logError = ''; this.notice = '';
    this.preparingRequest = /^\d{4}-\d{2}-\d{2}$/.test(date);
    this.publish(); this.pollStatus();
    if (!this.preparingRequest) return;
    const ownsSelection = () => generation === this.generation && storage.getItem('replay-selection') === selection;
    try {
      if (previous && preparing(previous.job)) {
        try { await this.environment.transport.replayCommand(previous.job.id, 'cancelPreparation', this.speed, new AbortController().signal); }
        catch { if (ownsSelection()) { this.notice = 'Previous preparation cancellation was not confirmed. The new day is queued independently.'; this.publish(); } }
      }
      // Keep the response observable even after selection/unmount, so an obsolete
      // remote preparation can be cancelled by its returned identity.
      const job = await this.environment.transport.prepareReplay(date, refresh, new AbortController().signal);
      if (job.date !== date) throw new Error('Replay prepared the wrong selected day.');
      if (!ownsSelection()) {
        if (job.date !== storage.getItem('replay-date') && preparing(job))
          await this.environment.transport.replayCommand(job.id, 'cancelPreparation', this.speed, new AbortController().signal);
        return;
      }
      this.id = job.id; storage.setItem('replay-job', job.id);
      this.retry();
    } catch (error) { if (ownsSelection()) this.error = errorMessage(error); }
    finally { if (generation === this.generation) { this.preparingRequest = false; this.publish(); } }
  };

  command = async (action: string, requestedSpeed = this.speed, profitExit?: ProfitExitSettings) => {
    if (!this.id || !this.active) return;
    const generation = this.generation; const server = this.server; const abort = new AbortController();
    const inspection = this.inspectionContext;
    this.commands.add(abort); this.error = '';
    if (action === 'setSpeed') this.speed = requestedSpeed;
    this.publish();
    const current = () => this.active && generation === this.generation && !abort.signal.aborted;
    try {
      const next = await this.environment.transport.replayCommand(this.id, action, requestedSpeed, abort.signal, inspection, profitExit);
      if (current()) {
        // The command was acknowledged, but its chart belongs to its dispatch
        // context. Read the current projection without resubmitting the command.
        if (inspection !== this.inspectionContext) this.retry();
        else this.accept(next, true);
      }
    } catch (error) {
      if (current() && (!server || server === this.server)) {
        this.error = errorMessage(error);
        if (this.view?.timing) this.speed = this.view.timing.requestedSpeed;
      }
    } finally { this.commands.delete(abort); if (current()) this.publish(); }
  };

  private accept(next: ReplayView, refresh = false) {
    if (next.job.date !== this.date || next.job.id !== this.id) throw new Error('Replay returned evidence for the wrong selected day or job.');
    if (next.serverInstanceId && this.retiredServers.has(next.serverInstanceId) || this.server && !next.serverInstanceId) return;
    const serverChanged = !!this.server && !!next.serverInstanceId && this.server !== next.serverInstanceId;
    if (!serverChanged && this.view?.job.id === next.job.id && next.revision !== undefined && this.view.revision !== undefined && next.revision < this.view.revision) return;
    const attemptChanged = !!this.view && this.view.job.attempt !== next.job.attempt;
    const runChanged = this.view?.paperRunId !== next.paperRunId;
    if (serverChanged) this.retiredServers.add(this.server!);
    if (next.serverInstanceId) this.server = next.serverInstanceId;
    if (serverChanged || attemptChanged) { this.health = null; this.healthError = ''; }
    if (serverChanged || runChanged) { this.paperLog = []; this.logError = ''; }
    this.view = next; this.error = '';
    if (next.timing) this.speed = next.timing.requestedSpeed;
    this.publish();
    if (refresh) this.retry();
    else {
      if (serverChanged) this.pollStatus();
      if (serverChanged || attemptChanged) this.pollHealth();
      if (serverChanged || runChanged) this.pollLog();
    }
  }
  private pollStatus() {
    this.poll('status', signal => this.environment.transport.readReplayStatus(signal), value => {
      this.status = value; this.statusError = ''; this.publish();
    }, error => { this.statusError = errorMessage(error); this.publish(); });
  }
  private pollView() {
    if (!this.id) return;
    const id = this.id; const instrument = this.inspectionContext;
    this.poll('view', signal => this.environment.transport.readReplay(id, signal, instrument), value => this.accept(value),
      error => { this.error = errorMessage(error); this.publish(); }, () => this.view?.job.state === 'Playing' ? 500 : 1500);
  }
  private pollHealth() {
    if (!this.id) return;
    const id = this.id;
    this.poll('health', signal => this.environment.transport.readDataHealth(id, signal), value => {
      this.health = value; this.healthError = ''; this.publish();
    }, error => { this.health = null; this.healthError = errorMessage(error); this.publish(); }, () => !this.view || preparing(this.view.job) ? 2500 : 10000);
  }
  private pollLog() {
    this.stopPoll('log');
    if (!this.id || !this.view?.paperRunId) return;
    const id = this.id; const run = this.view.paperRunId;
    this.poll('log', signal => this.environment.transport.readPaperLog(id, 'LIFECYCLE', signal), value => {
      this.paperLog = value.filter(row => row.runId === run); this.logError = ''; this.publish();
    }, error => { this.logError = errorMessage(error); this.publish(); }, () => 2500);
  }
  private poll<T>(key: Poll, load: (signal: AbortSignal) => Promise<T>, accept: (value: T) => void,
    fail: (error: unknown) => void, interval?: () => number) {
    if (!this.active) return;
    this.stopPoll(key);
    const scope: PollScope = { abort: new AbortController(), timer: null }; this.polls.set(key, scope);
    const current = () => this.active && !scope.abort.signal.aborted && this.polls.get(key) === scope;
    const run = async () => {
      try { const value = await load(scope.abort.signal); if (current()) accept(value); }
      catch (error) { if (current()) fail(error); }
      if (current() && interval) scope.timer = this.environment.timers.setTimeout(() => { void run(); }, interval());
    };
    void run();
  }
  private stopPoll(key: Poll) {
    const scope = this.polls.get(key);
    if (scope) { scope.abort.abort(); if (scope.timer !== null) this.environment.timers.clearTimeout(scope.timer); this.polls.delete(key); }
  }
  private publish() { this.snapshot = this.project(); if (this.active) for (const listener of this.listeners) listener(); }
  private project(): ReplayDisplaySnapshot {
    const job = this.view?.job; const raw = this.health; const evidence = this.view?.evidence;
    // The verdict, labels and qualification come from the accepted view. Health rows are only
    // detail: they are shown when they belong to this job, attempt and server, and they mirror the verdict.
    const health = raw && evidence && !this.healthError && job && raw.jobId === job.id && raw.attempt === job.attempt
      && (!this.view?.serverInstanceId || raw.serverInstanceId === this.view.serverInstanceId)
      ? { ...raw, qualification: evidence.qualification, readiness: evidence.readiness } : null;
    return { date: this.date, id: this.id, view: this.view, status: this.status, health, qualification: evidence?.qualification ?? null, paperLog: this.paperLog,
      error: this.error, statusError: this.statusError, healthError: this.healthError, logError: this.logError, notice: this.notice,
      busy: this.preparingRequest || this.commands.size > 0, speed: this.speed,
      preparing: evidence?.preparing ?? false, evidenceLabel: evidence?.label ?? 'Evidence awaiting validation', jobLabel: evidence?.jobLabel,
      credentialLabel: this.status?.credentialStatus === 'ConfiguredNotVerified' ? 'Credentials configured · current access unchecked' : this.status?.credentialStatus ?? 'Checking…',
      healthIssues: evidence ? evidence.issues : null };
  }
}
