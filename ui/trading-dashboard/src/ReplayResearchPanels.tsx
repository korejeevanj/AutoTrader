import { useEffect, useState } from 'react';
import { errorMessage } from './api';
import { ist, num } from './format';
import { compareRuns, readDiagnostics, readEventEvidence, readEventPage, readRuns, type DataHealth, type EventPage, type ReplayView, type RunComparison, type RunDiagnostics, type RunInfo } from './replayApi';
import { ReplayEvidenceBlockers } from './ReplayEvidenceBlockers';
import { profitExitLabel } from './ReplayProfitExit';

const runPolicyLabel = (run: RunInfo) => !run.profitExit ? 'Legacy policy / not recorded'
  : run.profitExit.mode === 'FixedNetTarget' ? `${profitExitLabel(run.profitExit.mode)} · target ₹${num(run.profitExit.netTarget)}`
  : run.profitExit.mode === 'TrailingNetProfit' ? `${profitExitLabel(run.profitExit.mode)} · activation ₹${num(run.profitExit.trailActivation)} / giveback ₹${num(run.profitExit.trailGiveback)} / floor ₹${num(run.profitExit.minimumLockedNetProfit)}`
  : profitExitLabel(run.profitExit.mode);

export function DataHealthPanel({ health, error, retry, retryFailed, refreshGaps, busy }: { health: DataHealth | null; error: string; retry: () => void; retryFailed: () => void; refreshGaps: () => void; busy: boolean }) {
  const [filter, setFilter] = useState('issues'); const [search, setSearch] = useState(''); const [page, setPage] = useState(0);
  const rows = health?.instruments.filter(row => (filter === 'all' || filter === 'issues' && (row.state !== 'Complete' || row.issues.length > 0) || filter === row.state)
    && `${row.id} ${row.role} ${row.error ?? ''}`.toLowerCase().includes(search.toLowerCase())) ?? [];
  useEffect(() => setPage(0), [filter, search]);
  const current = Math.min(page, Math.max(0, Math.ceil(rows.length / 25) - 1));
  return <section className="panel" aria-label="Dataset health details"><div className="panel-title"><div><p className="eyebrow">PREPARATION EVIDENCE</p><h2>Data health and missing intervals</h2></div>
    <div className="button-group"><button onClick={retry}>Refresh data health</button><button disabled={busy || !health?.failed} onClick={retryFailed}>Retry failed requests</button>
      <button disabled={busy || !health || !(health.sparse + health.empty + health.failed + health.invalid)} onClick={refreshGaps}>Refresh coverage gaps</button></div></div>
    {error && <p role="alert" className="error">Data health unavailable: {error}</p>}
    <p className="helper">Processed means checked, not qualified. Empty responses remain unknown. Gap refresh requests a new revision and preserves earlier evidence; it cannot guarantee the source has the missing history.</p>
    {health ? <><div className="replay-health-counts">{[['Complete', health.complete], ['Sparse', health.sparse], ['Empty', health.empty], ['Failed', health.failed], ['Invalid', health.invalid], ['Pending', health.pending]].map(([label, value]) =>
      <div key={label}><strong>{value}</strong><span>{label}</span></div>)}</div>
      <p><strong>{health.requiredAffected} futures/bank instruments affected</strong> · {health.cached} cache responses · {health.readiness}</p>
      {health.qualification && <div className={health.qualification.qualified ? 'helper' : 'replay-warnings'} aria-label="Historical input qualification">
        <strong>{health.qualification.qualified ? 'Historical input evidence qualified — execution remains a candle model' : 'Not qualified for strategy-performance conclusions'}</strong>
        <p>{health.qualification.incompleteDecisionTimes} of {health.qualification.decisionTimes} decision times incomplete · up to {health.qualification.maximumUnknownOptions} unknown options per decision.
          {health.qualification.firstIncompleteAtUtc && <> First incomplete: {ist(health.qualification.firstIncompleteAtUtc)}.</>}</p>
        <ReplayEvidenceBlockers issues={health.qualification.issues} />
        {health.qualification.issues.some(issue => /^(CALENDAR|WEIGHTS|FEES|OPTION_UNIVERSE)/.test(issue)) && <p>A candle refresh does not repair reference-data evidence. Correct the dated sources/configuration, then use Refresh from Kite to prepare a new revision. <a href="/api/replay/setup" target="_blank" rel="noreferrer">Reference-data setup</a></p>}
        {!health.qualification.qualified && <p>Replay outcomes remain diagnostic. Missing evidence cannot establish that no qualifying trade existed.</p>}
      </div>}
      <div className="toolbar"><label>Coverage filter <select value={filter} onChange={e => setFilter(e.target.value)}><option value="issues">Issues and incomplete</option><option value="all">All instruments</option>{['Complete', 'Sparse', 'Partial', 'Unavailable', 'Failed', 'Invalid', 'Waiting', 'AwaitingValidation'].map(state => <option key={state} value={state}>{state === 'AwaitingValidation' ? 'Awaiting validation' : state}</option>)}</select></label>
        <label>Search instruments <input type="search" value={search} onChange={e => setSearch(e.target.value)} placeholder="Symbol, role or error code" /></label></div>
      <div className="table-scroll replay-health-table"><table><thead><tr><th>Instrument / role</th><th>State / source</th><th>Returned / expected</th><th>Missing minutes</th><th>Error and decision impact</th></tr></thead><tbody>{rows.slice(current * 25, current * 25 + 25).map(row =>
        <tr key={row.id}><th>{row.id}<small>{row.role}</small></th><td><span className={`replay-state state-${row.state.toLowerCase()}`}>{row.state}</span><small>{row.cached ? 'Cached response' : 'Provider response'}</small></td><td>{row.rows} / {row.expectedMinutes ?? 'not validated'}</td><td>{row.missingMinutes ?? 'not validated'}</td><td>{row.error && <strong>{row.error}</strong>}
          <details><summary>Impact and missing intervals</summary><p>{row.impact}</p>{row.recoveryAction && <p><strong>Recovery:</strong> {row.recoveryAction}</p>}{row.missingIntervals.length > 0 && <ul>{row.missingIntervals.map(interval => <li key={interval.startUtc}>{ist(interval.startUtc)} → {ist(interval.endUtc)} (end exclusive)</li>)}</ul>}
            {row.issues.map((issue, i) => <p key={i}><strong>{issue.code}</strong> · {issue.detail} {issue.startUtc && ist(issue.startUtc)}</p>)}</details></td></tr>)}</tbody></table></div>
      {!rows.length && <p className="replay-empty">No matching instruments.</p>}
      <div className="replay-pagination"><span>{rows.length} instruments · Page {current + 1} of {Math.max(1, Math.ceil(rows.length / 25))}</span><div className="button-group"><button disabled={current === 0} onClick={() => setPage(current - 1)}>Previous instruments</button><button disabled={(current + 1) * 25 >= rows.length} onClick={() => setPage(current + 1)}>Next instruments</button></div></div>
      <details><summary>Source and policy limitations ({health.warnings.length})</summary><ul>{health.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul></details>
    </> : <p className="replay-empty">Preparing the data-health summary…</p>}
  </section>;
}

export function ReplayJournalExplorer({ id, runId, date, active }: { id: string; runId: string | null; date: string; active: boolean }) {
  const [runs, setRuns] = useState<RunInfo[]>([]); const [override, setOverride] = useState(''); const selected = override || runId || '';
  const [category, setCategory] = useState('LIFECYCLE'); const [search, setSearch] = useState(''); const [query, setQuery] = useState('');
  const [cursors, setCursors] = useState<(number | null)[]>([null]); const [refresh, setRefresh] = useState(0);
  const [page, setPage] = useState<EventPage | null>(null); const [error, setError] = useState(''); const [busy, setBusy] = useState(false);
  const [sequence, setSequence] = useState<number | null>(null); const [detail, setDetail] = useState<ReplayView['decisions'][number] | null>(null); const [detailError, setDetailError] = useState('');
  useEffect(() => { const timer = setTimeout(() => setQuery(search.trim()), 300); return () => clearTimeout(timer); }, [search]);
  useEffect(() => { setCursors([null]); setSequence(null); setPage(null); }, [selected, category, query]);
  useEffect(() => {
    if (!active) return; const abort = new AbortController();
    readRuns(date, abort.signal).then(value => { if (!abort.signal.aborted) setRuns(value.filter(run => run.jobId === id)); }).catch(() => {});
    return () => abort.abort();
  }, [id, date, active, refresh, runId]);
  const before = cursors.at(-1) ?? null;
  useEffect(() => {
    if (!active || !selected) return; const abort = new AbortController(); setBusy(true); setError('');
    readEventPage(id, selected, category, query, before, abort.signal).then(value => { if (!abort.signal.aborted) { setPage(value); setSequence(null); } })
      .catch(e => { if (!abort.signal.aborted) setError(errorMessage(e)); }).finally(() => { if (!abort.signal.aborted) setBusy(false); });
    return () => abort.abort();
  }, [id, selected, category, query, before, refresh, active]);
  useEffect(() => {
    setDetail(null); setDetailError(''); if (sequence === null || !active) return; const abort = new AbortController();
    readEventEvidence(id, selected, sequence, abort.signal).then(value => { if (!abort.signal.aborted) setDetail(value); })
      .catch(e => { if (!abort.signal.aborted) setDetailError(errorMessage(e)); }); return () => abort.abort();
  }, [id, selected, sequence, active]);
  return <section className="panel" aria-label="Full run decision journal"><div className="panel-title"><div><p className="eyebrow">DURABLE HISTORY</p><h2>Full run decision journal</h2></div><button onClick={() => { setCursors([null]); setRefresh(n => n + 1); }}>Refresh latest events</button></div>
    <p className="helper">Browse all committed events, including rejected candidates. Pages stay frozen while you inspect them; refresh explicitly for new events.</p>
    <div className="toolbar"><label>Journal run <select value={selected} onChange={e => setOverride(e.target.value)}>{selected && !runs.some(run => run.runId === selected) && <option value={selected}>{selected}</option>}{runs.map(run => <option key={run.runId} value={run.runId}>{run.preset} · {run.runId}</option>)}</select></label>
      <label>Journal class <select value={category} onChange={e => setCategory(e.target.value)}><option value="LIFECYCLE">Orders and fills</option><option value="">All decisions</option><option value="DATA_READINESS">Data readiness</option><option value="STRICT_ENGINE">Strict Engine</option><option value="CANDLE_RESEARCH">Candle research</option><option value="CANDLE_POSITION_POLICY">Position policy</option><option value="PAPER_CONTROL">Entry controls</option></select></label>
      <label>Search full journal <input type="search" value={search} onChange={e => setSearch(e.target.value)} placeholder="Contract, action or reason" /></label></div>
    {error && <p role="alert" className="error">{error}</p>}{busy && <p role="status">Loading journal page…</p>}
    {!selected && <p className="replay-empty">A strategy run is required for the paper journal. Acquisition logs are available in Run evidence.</p>}
    <div className="table-scroll replay-journal-table" aria-busy={busy}><table><thead><tr><th>Time / sequence</th><th>Contract / class</th><th>Action / reasons</th><th>Evidence</th></tr></thead><tbody>{page?.runId === selected && page.rows.map(row => <tr key={row.sequence}><td>{ist(row.timeUtc)}<small>#{row.sequence}</small></td><th>{row.contractId || 'Session'}<small>{row.category}</small></th><td>{row.action}<small>{row.reasonsJson}</small></td><td><button disabled={busy} aria-expanded={sequence === row.sequence} onClick={() => setSequence(sequence === row.sequence ? null : row.sequence)}>Inspect event #{row.sequence}</button></td></tr>)}</tbody></table></div>
    {page?.rows.length === 0 && !busy && <p className="replay-empty">No matching committed events.</p>}
    {sequence !== null && <div className="replay-evidence-detail"><h3>Event #{sequence} · full decision evidence</h3>{detailError && <p role="alert">{detailError} Older journals may not have event-to-batch mappings; use their full report.</p>}
      {detail ? <><p>{detail.contractId} · {detail.action} · {detail.reasons.join(' · ')}</p>{detail.rank != null && <p>Eligible rank {detail.rank} · spread {num(detail.rankingMetrics?.spreadPercent ?? null)}% · round-trip friction {num(detail.rankingMetrics?.roundTripFriction ?? null)} · strike distance {num(detail.rankingMetrics?.strikeDistance ?? null)}. Rank is not a probability.</p>}<div className="table-scroll"><table><thead><tr><th>Gate</th><th>Outcome</th><th>Evidence</th><th>Observed / threshold</th></tr></thead><tbody>{detail.gates?.map((gate, i) => <tr key={i}><th>{gate.code}</th><td>{typeof gate.outcome === 'number' ? ['PASS', 'FAIL', 'UNKNOWN', 'NOT_EVALUATED'][gate.outcome] : gate.outcome}</td><td>{gate.detail}</td><td>{num(gate.observed)} / {num(gate.threshold)}</td></tr>)}</tbody></table></div></> : !detailError && <p>Loading evidence…</p>}</div>}
    <div className="replay-pagination"><span>Page {cursors.length} · up to 25 events per page</span><div className="button-group"><button disabled={busy || cursors.length === 1} onClick={() => setCursors(value => value.slice(0, -1))}>Newer events</button><button disabled={busy || !page?.nextBeforeSequence} onClick={() => setCursors(value => [...value, page!.nextBeforeSequence])}>Older events</button></div></div>
    {selected && <a href={`/api/replay/jobs/${encodeURIComponent(id)}/evidence?runId=${encodeURIComponent(selected)}`}>Download complete run evidence (.jsonl.gz)</a>}
  </section>;
}

export function RunEvidencePanel({ id, date, active, currentRun }: { id: string; date: string; active: boolean; currentRun: string | null }) {
  const [diagnostics, setDiagnostics] = useState<RunDiagnostics | null>(null); const [runs, setRuns] = useState<RunInfo[]>([]); const [refresh, setRefresh] = useState(0); const [error, setError] = useState('');
  const [left, setLeft] = useState(''); const [right, setRight] = useState(''); const [comparison, setComparison] = useState<RunComparison | null>(null); const [comparing, setComparing] = useState(false);
  const [comparisonError, setComparisonError] = useState(''); const [severity, setSeverity] = useState('');
  const [compareRequest, setCompareRequest] = useState<{ left: string; right: string } | null>(null);
  useEffect(() => {
    if (!active) return; const abort = new AbortController();
    Promise.all([readDiagnostics(id, abort.signal), readRuns(date, abort.signal)]).then(([value, available]) => {
      if (abort.signal.aborted) return; setDiagnostics(value); setRuns(available); setError('');
      setLeft(previous => previous || value.runs.find(run => run.runId === currentRun && run.complete)?.runId || value.runs.find(run => run.complete)?.runId || '');
    }).catch(e => { if (!abort.signal.aborted) setError(errorMessage(e)); }); return () => abort.abort();
  }, [id, date, active, refresh]);
  useEffect(() => {
    setComparison(null); setComparisonError(''); setComparing(false); setCompareRequest(null);
  }, [left, right]);
  useEffect(() => {
    if (!compareRequest || !active) return; const abort = new AbortController(); setComparisonError(''); setComparing(true);
    compareRuns(id, compareRequest.left, compareRequest.right, abort.signal).then(value => { if (!abort.signal.aborted) setComparison(value); })
      .catch(e => { if (!abort.signal.aborted) setComparisonError(errorMessage(e)); }).finally(() => { if (!abort.signal.aborted) setComparing(false); });
    return () => abort.abort();
  }, [compareRequest, id, active]);
  const local = diagnostics?.runs ?? []; const chosen = left || local.find(run => run.runId === currentRun && run.complete)?.runId || local.find(run => run.complete)?.runId || currentRun || '';
  const operations = diagnostics?.operations.filter(row => !severity || row.severity === severity) ?? [];
  return <><section className="panel" aria-label="Run diagnostics"><div className="panel-title"><div><p className="eyebrow">REPRODUCIBLE RESEARCH</p><h2>Run evidence and operational logs</h2></div><button onClick={() => setRefresh(value => value + 1)}>Refresh run evidence</button></div>
    <p className="helper">Logs include acquisition attempts, errors, controls and committed steps. Complete evidence includes frozen input/configuration fingerprints, gate records and paper state, even for cancelled runs.</p>
    {error && <p role="alert">Run diagnostics unavailable: {error}</p>}
    {diagnostics?.initializing && <p role="status">Initializing durable replay storage and indexes. Preparation continues in the background; refresh for logs when ready.</p>}
    <div className="replay-run-list">{local.map(run => <div key={run.runId}><div><strong>{run.preset} · {run.complete ? 'Completed' : 'Partial / paused'}</strong><small>{run.runId}</small><span>{runPolicyLabel(run)}</span><span>{run.trades} trades · estimated net ₹{num(run.realizedNetPnl)}</span><span>{run.qualified === true ? 'Historical evidence qualified' : run.qualified === false ? 'Unqualified diagnostic outcomes' : 'Historical qualification not established'}</span></div>
      <a href={`/api/replay/jobs/${encodeURIComponent(id)}/evidence?runId=${encodeURIComponent(run.runId)}`}>Download evidence</a></div>)}</div>
    <a href={`/api/replay/jobs/${encodeURIComponent(id)}/operations`}>Download all acquisition/runtime logs (.jsonl.gz)</a>
    <label>Log severity <select value={severity} onChange={e => setSeverity(e.target.value)}><option value="">All levels</option><option>Warning</option><option>Error</option><option>Information</option></select></label>
    <div className="table-scroll replay-log-table"><table><thead><tr><th>Wall / simulated time</th><th>Run / level</th><th>Event</th><th>Details</th></tr></thead><tbody>{operations.map(row => <tr key={row.sequence}><td>{ist(row.wallTimeUtc)}<small>Replay {ist(row.simulatedTimeUtc)}</small></td><td>{row.run === 0 ? 'Acquisition' : row.run} · {row.severity}</td><th>{row.code}</th><td><details><summary>Inspect log details</summary><pre>{row.detailJson}</pre></details></td></tr>)}</tbody></table></div><p className="helper">Latest 100 operations; the log download contains the complete history.</p>
  </section><section className="panel" aria-label="Compare replay runs"><h2>Compare decisions on the same frozen dataset</h2><p className="helper">Only completed runs with identical dataset hashes are compared. Unqualified runs support diagnostic comparison; their outcomes cannot establish strategy performance. Changes in code, strategy configuration and execution preset remain explicit.</p>
    <div className="toolbar"><label>Baseline run <select value={chosen} onChange={e => setLeft(e.target.value)}>{!local.length && <option value="">No runs yet</option>}{local.map(run => <option key={run.runId} value={run.runId}>{run.preset} · {runPolicyLabel(run)} · {run.complete ? 'Completed' : 'Partial'} · {run.runId}</option>)}</select></label>
      <label>Comparison run <select value={right} onChange={e => setRight(e.target.value)}><option value="">Choose a completed run</option>{runs.filter(run => run.complete && run.runId !== chosen).map(run => <option key={run.runId} value={run.runId}>{run.date} · {run.preset} · {runPolicyLabel(run)} · {run.runId}</option>)}</select></label>
      <button disabled={!chosen || !right || comparing || !local.some(run => run.runId === chosen && run.complete)} onClick={() => setCompareRequest({ left: chosen, right })}>Compare runs</button></div>
    {comparing && <p role="status">Comparing durable decision histories…</p>}{comparisonError && <p role="alert">{comparisonError}</p>}
    {comparison && <><div className="replay-metrics"><div><span>Baseline estimated net</span><strong>₹{num(comparison.left.realizedNetPnl)}</strong></div><div><span>Comparison estimated net</span><strong>₹{num(comparison.right.realizedNetPnl)}</strong></div><div><span>Configuration / code changed</span><strong>{comparison.configurationChanged ? 'Yes' : 'No'}</strong></div></div>
      {(comparison.left.qualified !== true || comparison.right.qualified !== true) && <p className="replay-warnings">Data qualification is incomplete or unestablished. These are diagnostic model outcomes.</p>}
      <p>{comparison.differences.length === 0 ? 'No action/reason differences found.' : `${comparison.differences.length}${comparison.hasMore ? '+' : ''} action/reason differences${comparison.hasMore ? ' (first 100 shown; download both journals for all evidence)' : ''}.`}</p>
      <div className="table-scroll"><table><thead><tr><th>Time / contract</th><th>Class</th><th>Baseline</th><th>Comparison</th></tr></thead><tbody>{comparison.differences.map((row, i) => <tr key={i}><th>{ist(row.timeUtc)}<small>{row.contractId}</small></th><td>{row.category}</td><td>{row.leftAction ?? 'No corresponding event'}<small>{row.leftReasons}</small></td><td>{row.rightAction ?? 'No corresponding event'}<small>{row.rightReasons}</small></td></tr>)}</tbody></table></div></>}
  </section></>;
}
