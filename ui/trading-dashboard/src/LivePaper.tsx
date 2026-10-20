import { useEffect, useState } from 'react';
import { ist, num as formatNumber } from './format';

const num = (value: number | null | undefined) => formatNumber(value ?? null);
const mb = (bytes: number | undefined) => Math.round((bytes ?? 0) / 1048576).toLocaleString('en-IN');
const enumLabel = (value: string | number | undefined, labels: readonly string[]) => typeof value === 'number' ? labels[value] ?? 'Unavailable' : value ?? 'Unavailable';

interface Instrument { id: string; instrumentToken: number; role: string; expiry: string | null; lotSize: number | null; tickSize: number | null }
interface Level { price: number; quantity: number }
interface Tick { instrumentToken: number; lastPrice: number; exchangeTimeUtc: string | null; lastTradeTimeUtc: string | null; receivedAtUtc: string; bids: Level[]; asks: Level[] }
interface Gate { code: string; outcome: string | number; detail: string }
interface Candidate { contractId: string; action: string | number; rank: number | null; reasonCodes: string[]; gates: Gate[]; financialEvidence: { netRisk: number | null; scenarioNetProfit: number | null; requiredCash: number | null } | null }
interface Approval { id: string; origin: string; contract: Instrument; acceptedAtUtc: string; reservedCash: number }
interface Position { approval: Approval; entryPrice: number; enteredAtUtc: string; units: number; exitReason: string | null; exitRequestedAtUtc: string | null }
interface Trade { id: string; contractId: string; origin: string; entryPrice: number; exitPrice: number; netPnl: number; grossPnl: number; exitReason: string; grossStopOverrun: number; entryFees: { total: number }; exitFees: { total: number } }
interface Paper { ledger: { cash: number; paidFees: number; realizedNetPnl: number }; pending: Approval | null; position: Position | null; trades: Trade[]; events: { id: string; timeUtc: string; kind: string; intentId: string; detail: string }[] }
interface LiveView {
  mode: string; status: string; errorCode: string | null; credentialStatus: string; runId: string | null; tradingDate: string | null; asOfUtc: string | null;
  connected: boolean; autoEnabled: boolean; recordingHealthy: boolean; recordingIncomplete: boolean; receivedSequence: number; durableSequence: number;
  recorderQueueDepth: number; recorderLagMilliseconds: number; issues: string[]; instruments: Instrument[]; universe: string[]; quotes: Tick[]; candidates: Candidate[];
  paper: Paper | null; sessionNet: number | null; positionDecision: { action: string | number; netLiquidationPnl: number | null; reasonCodes: string[] } | null;
  paperPersistenceHealthy: boolean; paperPersistencePending: boolean;
  marketRecordingBytes: number; freeDiskBytes: number; archiveError: string | null;
  quoteFreshness: { instrumentToken: number; state: string; quoteAgeSeconds: number | null; receiptAgeSeconds: number | null; lastTradeAgeSeconds: number | null }[];
  contextGates: Gate[];
  directional: { status: string | number; openingHigh: number | null; openingLow: number | null; vwap: number | null; aboveWeightPercent: number | null; belowWeightPercent: number | null; missingWeightPercent: number | null;
    constituents: { id: string; name: string; weightPercent: number | null; lastPrice: number | null; vwap: number | null; state: string }[] } | null;
  futuresBars: { startUtc: string; endUtc: string; availableAtUtc: string; open: number; high: number; low: number; close: number; volume: number }[];
}
interface Recording { id: string; createdAtUtc: string; lastSequence: number; engineJson?: string }
interface ReplayResult { id: string; sourceRunId: string; mode: string; limitations: string[]; result: { paper: Paper; recordingIncomplete: boolean; candidates?: Candidate[] } }

async function request<T>(path: string, signal?: AbortSignal, body?: unknown): Promise<T> {
  const deadline = AbortSignal.timeout(body === undefined ? 5000 : 120000);
  const response = await fetch(path, { signal: signal ? AbortSignal.any([signal, deadline]) : deadline, ...(body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }) });
  const json: unknown = await response.json();
  if (!response.ok) {
    const detail = typeof json === 'object' && json !== null && 'detail' in json && typeof json.detail === 'string' ? json.detail : `HTTP ${response.status}`;
    throw new Error(detail);
  }
  if (path.endsWith('/status') || path.endsWith('/commands')) {
    if (typeof json !== 'object' || json === null || !('status' in json) || typeof json.status !== 'string'
      || !('connected' in json) || typeof json.connected !== 'boolean' || !('autoEnabled' in json) || typeof json.autoEnabled !== 'boolean'
      || !('receivedSequence' in json) || typeof json.receivedSequence !== 'number' || !Number.isSafeInteger(json.receivedSequence)
      || !('instruments' in json) || !Array.isArray(json.instruments) || !('quotes' in json) || !Array.isArray(json.quotes)
      || !('candidates' in json) || !Array.isArray(json.candidates) || !('universe' in json) || !Array.isArray(json.universe)
      || !('contextGates' in json) || !Array.isArray(json.contextGates) || !('futuresBars' in json) || !Array.isArray(json.futuresBars)
      || !('quoteFreshness' in json) || !Array.isArray(json.quoteFreshness) || !('paper' in json))
      throw new Error('Invalid live paper response; current state unavailable');
  }
  return json as T;
}

export function LivePaper() {
  const [view, setView] = useState<LiveView | null>(null);
  const [error, setError] = useState('');
  const [commandError, setCommandError] = useState('');
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState('');
  const [manualRequest, setManualRequest] = useState(() => crypto.randomUUID());
  const [recordings, setRecordings] = useState<Recording[]>([]);
  const [replaySource, setReplaySource] = useState('');
  const [changedConfiguration, setChangedConfiguration] = useState('');
  const [replay, setReplay] = useState<ReplayResult | null>(null);
  useEffect(() => {
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try { const next = await request<LiveView>('/api/live/status', controller.signal); if (!controller.signal.aborted) { setView(current => !current || current.runId !== next.runId || next.receivedSequence >= current.receivedSequence ? next : current); setError(''); } }
      catch (reason) { if (!controller.signal.aborted) { setError(reason instanceof Error ? reason.message : 'Live workspace unavailable'); setView(null); } }
      if (!controller.signal.aborted) timer = setTimeout(poll, 500);
    }
    void poll();
    return () => { controller.abort(); clearTimeout(timer); };
  }, []);
  async function command(action: string) {
    setBusy(true); setCommandError('');
    try {
      const next = await request<LiveView>('/api/live/commands', undefined, { command: action, requestId: action === 'manual' ? manualRequest : crypto.randomUUID(), contractId: action === 'manual' ? selected : null });
      setView(next);
      if (action === 'manual') setManualRequest(crypto.randomUUID());
    } catch (reason) { setCommandError(reason instanceof Error ? reason.message : 'Paper command failed'); }
    finally { setBusy(false); }
  }
  async function loadRecordings() {
    setBusy(true); setCommandError('');
    try { setRecordings(await request<Recording[]>('/api/live/recordings')); }
    catch (reason) { setCommandError(reason instanceof Error ? reason.message : 'Recordings unavailable'); }
    finally { setBusy(false); }
  }
  async function runReplay() {
    setBusy(true); setCommandError(''); setReplay(null);
    try { setReplay(await request<ReplayResult>('/api/live/replays', undefined, { sourceRunId: replaySource, engineJson: changedConfiguration.trim() || null })); }
    catch (reason) { setCommandError(reason instanceof Error ? reason.message : 'Quote replay failed'); }
    finally { setBusy(false); }
  }
  async function rehearse() {
    setBusy(true); setCommandError(''); setReplay(null);
    try { setReplay(await request<ReplayResult>('/api/live/rehearsal', undefined, {})); }
    catch (reason) { setCommandError(reason instanceof Error ? reason.message : 'Synthetic rehearsal unavailable'); }
    finally { setBusy(false); }
  }
  const options = view?.instruments.filter(item => view.universe.includes(item.id)) ?? [];
  return <div className="live-paper">
    <section className="panel"><h2>Live feed and recorder</h2>
      <p><strong>PAPER + LIVE DATA</strong> · Local simulation · Bank Nifty</p>
      <p className="helper">Recording and protection run on the server independently of this page. A connection heartbeat is not quote freshness. Closed-session or stale observations are not live prices.</p>
      {error && <p role="alert" className="error">Workspace unavailable: {error}. Trading state is unknown until connection recovers.</p>}
      {commandError && <p role="alert" className="error">{commandError}</p>}
      {view && <>
        <p role="status"><strong>{view.status}</strong> · Feed {view.connected ? 'connected' : 'disconnected'} · Paper-auto {view.autoEnabled ? 'enabled' : 'paused'} · Recording {view.recordingHealthy ? 'healthy' : 'not ready'}</p>
        <p>Credentials: {view.credentialStatus} · {view.errorCode ?? 'No provider error reported'}</p>
        <p>Session {view.tradingDate ?? 'not started'} · As-of {view.asOfUtc ? ist(view.asOfUtc) : 'unavailable'}</p>
        {view.runId && <p><a href={`/api/live/recordings/${encodeURIComponent(view.runId)}/decisions`} target="_blank" rel="noreferrer">Read committed decision history (paged JSON)</a></p>}
        <p>Received sequence {view.receivedSequence} · Committed {view.durableSequence} · Queue {view.recorderQueueDepth} · Lag {num(view.recorderLagMilliseconds)} ms</p>
        <p>Recording file {mb(view.marketRecordingBytes)} MB · Free disk {mb(view.freeDiskBytes)} MB{view.archiveError ? ` · archive fault ${view.archiveError}` : ''}</p>
        <p>Paper persistence {view.paperPersistenceHealthy ? 'healthy' : 'not ready'}{view.paperPersistencePending ? ' · acknowledgement pending; the account below shows committed state only' : ''}</p>
        {view.recordingIncomplete && <p className="error">Recording contains a gap or possible crash-tail loss. Replay must retain this limitation.</p>}
        <p className="helper">{view.issues.join(' · ') || 'No reference/recovery issues reported.'}</p>
        <div className="toolbar"><button disabled={busy || view.status === 'Preparing' || view.status === 'Connected' || view.status === 'Reconnecting'} onClick={() => void command('start')}>Start / reconnect recording</button>
          <button disabled={busy || !view.runId || view.autoEnabled} onClick={() => void command('arm')}>Enable paper-auto</button>
          <button disabled={busy || !view.runId} onClick={() => void command('pause')}>Pause paper-auto</button>
          <button disabled={busy || !view.paper?.position && !view.paper?.pending} onClick={() => void command('exit')}>Request paper exit</button></div>
        <p className="helper">Auto requires a completed manual lifecycle and all strategy gates. Missing opening-range evidence blocks strategy entries. Zero trades is valid.</p>
      </>}
    </section>
    {view && <section className="panel"><h2>Strategy context and completed bars</h2>
      <p>{view.directional ? enumLabel(view.directional.status, ['UP', 'DOWN', 'NO_SETUP', 'INSUFFICIENT_DATA', 'NOT_EVALUATED']) : 'Warming up'} · Opening high / low {num(view.directional?.openingHigh)} / {num(view.directional?.openingLow)} · Approximate candle-derived futures VWAP {num(view.directional?.vwap)}</p>
      <p>Bank weight above / below VWAP {num(view.directional?.aboveWeightPercent)}% / {num(view.directional?.belowWeightPercent)}% · Missing weight {num(view.directional?.missingWeightPercent)}%</p>
      <details><summary>Context / readiness reasons</summary>{view.contextGates.filter(gate => gate.outcome !== 'PASS' && gate.outcome !== 0).map((gate, i) => <p key={`${gate.code}-${i}`}>{gate.code}: {gate.detail}</p>)}</details>
      {!!view.directional?.constituents.length && <div className="table-scroll" role="region" aria-label="Live bank evidence" tabIndex={0}><table><thead><tr><th>Bank</th><th>Weight %</th><th>Close</th><th>VWAP ≈</th><th>State</th></tr></thead><tbody>{view.directional.constituents.map(bank => <tr key={bank.id}><th>{bank.name}</th><td>{num(bank.weightPercent)}</td><td>{num(bank.lastPrice)}</td><td>{num(bank.vwap)}</td><td>{bank.state}</td></tr>)}</tbody></table></div>}
      <details><summary>Available completed futures bars ({view.futuresBars.length})</summary><div className="table-scroll" role="region" aria-label="Live completed futures bars" tabIndex={0}><table><thead><tr><th>Start / end / available (IST)</th><th>Open</th><th>High</th><th>Low</th><th>Close</th><th>Volume</th></tr></thead><tbody>{view.futuresBars.map(bar => <tr key={bar.startUtc}><th>{ist(bar.startUtc)} / {ist(bar.endUtc)} / {ist(bar.availableAtUtc)}</th><td>{num(bar.open)}</td><td>{num(bar.high)}</td><td>{num(bar.low)}</td><td>{num(bar.close)}</td><td>{num(bar.volume)}</td></tr>)}</tbody></table></div></details>
    </section>}
    {view?.paper && <section className="panel"><h2>Paper account and position</h2>
      <p>Cash balance ₹{num(view.paper.ledger.cash)} · Realized net ₹{num(view.paper.ledger.realizedNetPnl)} · Fees paid ₹{num(view.paper.ledger.paidFees)} · Session net ₹{num(view.sessionNet)}</p>
      {view.paper.pending && <p role="status">Pending entry {view.paper.pending.id} · {view.paper.pending.contract.id} · Reserved ₹{num(view.paper.pending.reservedCash)}. This is an intent, not a fill.</p>}
      {view.paper.position ? <><p><strong>{view.paper.position.approval.contract.id}</strong> · {view.paper.position.approval.origin} · {view.paper.position.units} units · Entry ₹{num(view.paper.position.entryPrice)}</p>
        <p>{view.paper.position.exitRequestedAtUtc ? `EXIT PENDING: ${view.paper.position.exitReason}. Waiting for a later fresh quote with whole-position depth.` : enumLabel(view.positionDecision?.action, ['HOLD', 'WATCH_EXIT', 'EXIT', 'TIGHTEN_STOP'])}</p>
        <p>Net liquidation estimate ₹{num(view.positionDecision?.netLiquidationPnl)} · {view.positionDecision?.reasonCodes.join(' · ')}</p></> : <p>No open paper position.</p>}
      <div className="toolbar"><label>Manual-test contract <select aria-label="Manual-test contract" value={selected} disabled={busy} onChange={event => { setSelected(event.target.value); setManualRequest(crypto.randomUUID()); }}><option value="">Select recorded-universe option</option>{options.map(item => <option key={item.id} value={item.id}>{item.id} · lot {item.lotSize}</option>)}</select></label>
        <button disabled={busy || !selected || !!view.paper.pending || !!view.paper.position} onClick={() => void command('manual')}>Submit MANUAL_TEST</button></div>
      <p className="helper">Manual bypasses setup detection only. One lot; ₹200 gross trigger, ₹400 modeled-risk ceiling, ₹1,200 session pause. A trigger does not guarantee realized loss.</p>
    </section>}
    {view && <section className="panel"><h2>Received quotes — source timestamps</h2><div className="table-scroll"><table><thead><tr><th>Instrument / freshness</th><th>Last</th><th>Bid / depth</th><th>Ask / depth</th><th>Exchange</th><th>Receipt</th><th>Last trade</th></tr></thead><tbody>{view.quotes.map(tick => <tr key={tick.instrumentToken}><th>{view.instruments.find(item => item.instrumentToken === tick.instrumentToken)?.id ?? tick.instrumentToken}<small>{view.quoteFreshness.find(item => item.instrumentToken === tick.instrumentToken)?.state ?? 'unavailable'}</small></th><td>{num(tick.lastPrice)}</td><td>{num(tick.bids[0]?.price)} / {tick.bids[0]?.quantity ?? '—'}</td><td>{num(tick.asks[0]?.price)} / {tick.asks[0]?.quantity ?? '—'}</td><td>{tick.exchangeTimeUtc ? ist(tick.exchangeTimeUtc) : 'Unavailable'}</td><td>{ist(tick.receivedAtUtc)}</td><td>{tick.lastTradeTimeUtc ? ist(tick.lastTradeTimeUtc) : 'Unavailable'}</td></tr>)}</tbody></table></div></section>}
    {view && <section className="panel"><h2>Candidate gates</h2><p className="helper">Risk and favorable profit are MODEL_ESTIMATE outputs from an experimental scenario model, not probabilities, broker Greeks, or observed future prices.</p><div className="table-scroll"><table><thead><tr><th>Contract</th><th>Decision / rank</th><th>Modeled net risk</th><th>Favorable net scenario</th><th>Reasons</th></tr></thead><tbody>{view.candidates.map(candidate => <tr key={candidate.contractId}><th>{candidate.contractId}</th><td>{enumLabel(candidate.action, ['ENTER', 'WATCH', 'SKIP', 'INSUFFICIENT_DATA'])} / {candidate.rank ?? '—'}</td><td>{num(candidate.financialEvidence?.netRisk)}</td><td>{num(candidate.financialEvidence?.scenarioNetProfit)}</td><td><details><summary>{candidate.reasonCodes.join(' · ')}</summary>{candidate.gates.filter(gate => gate.outcome !== 'PASS' && gate.outcome !== 0).map((gate, i) => <p key={`${gate.code}-${i}`}>{gate.code}: {gate.detail}</p>)}</details></td></tr>)}</tbody></table></div></section>}
    {view?.paper && <Ledger paper={view.paper} />}
    <section className="panel"><h2>Recorded quote replay</h2><p>Re-run the original events with a supplied clock. Source recordings stay immutable; results receive a separate run identity.</p>
      <button disabled={busy} onClick={() => void rehearse()}>Run synthetic quote rehearsal</button><p className="helper">Development-only, separate SYNTHETIC TEST run: fictional packets, contracts, weights and fees. Tests software without provider access or changing the live session.</p>
      <div className="toolbar"><button disabled={busy} onClick={() => void loadRecordings()}>Load recordings</button><label>Source recording <select aria-label="Source recording" value={replaySource} onChange={event => setReplaySource(event.target.value)}><option value="">Select session</option>{recordings.map(item => <option key={item.id} value={item.id}>{item.id} · {item.lastSequence} events</option>)}</select></label></div>
      <label>Changed engine configuration JSON (optional)<textarea value={changedConfiguration} onChange={event => setChangedConfiguration(event.target.value)} rows={5} placeholder="Leave empty to use the frozen original configuration" /></label>
      <button disabled={busy || !recordings.find(item => item.id === replaySource)?.engineJson} onClick={() => setChangedConfiguration(recordings.find(item => item.id === replaySource)?.engineJson ?? '')}>Load frozen settings for editing</button>
      <button disabled={busy || !replaySource} onClick={() => void runReplay()}>Replay recorded quotes</button>
      {replay && <><h3>{replay.mode}</h3><p>Result {replay.id} · Source {replay.sourceRunId}</p><p>{replay.limitations.join(' · ')}</p>
        {!!replay.result.candidates?.length && <details><summary>Replayed candidate decisions</summary>{replay.result.candidates.map(candidate => <p key={candidate.contractId}>{candidate.contractId} · {enumLabel(candidate.action, ['ENTER', 'WATCH', 'SKIP', 'INSUFFICIENT_DATA'])} · {candidate.reasonCodes.join(' · ')}</p>)}</details>}
        <Ledger paper={replay.result.paper} /></>}
    </section>
  </div>;
}

function Ledger({ paper }: { paper: Paper }) {
  return <section className="panel"><h2>Paper fills and journal</h2><div className="table-scroll"><table><thead><tr><th>Contract / origin</th><th>Entry</th><th>Exit</th><th>Gross</th><th>Entry / exit fees</th><th>Net</th><th>Exit reason / overrun</th></tr></thead><tbody>{paper.trades.map(trade => <tr key={trade.id}><th>{trade.contractId} / {trade.origin}</th><td>{num(trade.entryPrice)}</td><td>{num(trade.exitPrice)}</td><td>{num(trade.grossPnl)}</td><td>{num(trade.entryFees.total)} / {num(trade.exitFees.total)}</td><td>{num(trade.netPnl)}</td><td>{trade.exitReason} / ₹{num(trade.grossStopOverrun)}</td></tr>)}</tbody></table></div><details><summary>Intent / fill lifecycle ({paper.events.length})</summary>{paper.events.map(event => <p key={event.id}>{ist(event.timeUtc)} · {event.kind} · {event.intentId} · {event.detail}</p>)}</details></section>;
}
