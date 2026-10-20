import { useEffect, useMemo, useState } from 'react';
import { ist, num } from './format';
import type { PaperLogRow, ReplayView } from './replayApi';

type Decision = ReplayView['decisions'][number];
const lifecycle = (action: string) => /INTENT_ACCEPTED|INTENT_CANCELED|FILLED|PAUSE_NEW_ENTRIES|ENABLE_NEW_ENTRIES/.test(action);
const highlight = (row: Decision) => lifecycle(row.action) || ['ENTER', 'EXIT', 'WATCH_EXIT', 'HYPOTHETICAL_ELIGIBLE'].includes(row.action);
const readable = (value: string) => value.replaceAll('_', ' ').toLowerCase();

function Pager({ page, pages, setPage, count, label = 'grouped results' }: { page: number; pages: number; setPage: (page: number) => void; count: number; label?: string }) {
  return <div className="replay-pagination"><span>{count} {label} · Page {page + 1} of {Math.max(1, pages)}</span>
    <div className="button-group"><button disabled={page === 0} onClick={() => setPage(page - 1)}>Previous page</button>
      <button disabled={page + 1 >= pages} onClick={() => setPage(page + 1)}>Next page</button></div></div>;
}

export function ReplayDecisionPanel({ decisions, events = [] }: { decisions: Decision[]; events?: PaperLogRow[] }) {
  const [scope, setScope] = useState('highlights');
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(0);
  const [size, setSize] = useState(10);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [selectedContract, setSelectedContract] = useState('');
  const grouped = useMemo(() => {
    const groups = new Map<string, Decision[]>();
    const search = query.trim().toLowerCase();
    const persisted: Decision[] = events.filter(row => lifecycle(row.action)).map(row => {
      let reasons: string[] = [];
      try { const value: unknown = JSON.parse(row.reasonsJson); if (Array.isArray(value)) reasons = value.filter((item): item is string => typeof item === 'string'); } catch { reasons = [row.reasonsJson]; }
      return { contractId: row.contractId, class: row.category, action: row.action, reasons, timeUtc: row.timeUtc, setupId: row.setupId, indexId: row.indexId };
    });
    const seen = new Set<string>();
    for (const row of scope === 'highlights' ? [...decisions, ...persisted] : decisions) {
      if (scope === 'highlights' && !highlight(row)) continue;
      if (scope === 'blocked' && !['SKIP', 'INSUFFICIENT_DATA', 'REJECTED', 'NOT_EVALUATED', 'DATA_EVIDENCE_INCOMPLETE'].includes(row.action)) continue;
      if (search && !`${row.contractId} ${row.action} ${row.reasons.join(' ')}`.toLowerCase().includes(search)) continue;
      const identity = JSON.stringify([row.timeUtc, row.class, row.action, row.contractId]);
      if (seen.has(identity)) continue; seen.add(identity);
      const key = JSON.stringify([row.timeUtc, row.class, row.action, row.setupId, row.reasons]);
      const group = groups.get(key) ?? []; group.push(row); groups.set(key, group);
    }
    return [...groups].map(([key, rows]) => ({ key, rows, first: rows[0]! }))
      .sort((a, b) => (b.first.timeUtc ? Date.parse(b.first.timeUtc) : 0) - (a.first.timeUtc ? Date.parse(a.first.timeUtc) : 0));
  }, [decisions, events, scope, query]);
  useEffect(() => { setPage(0); setExpanded(null); }, [scope, query, size]);
  const pages = Math.ceil(grouped.length / size);
  const current = Math.min(page, Math.max(0, pages - 1));
  const blockers = useMemo(() => {
    const counts = new Map<string, number>();
    for (const row of decisions) if (!highlight(row)) for (const reason of row.reasons) counts.set(reason, (counts.get(reason) ?? 0) + 1);
    return [...counts].sort((a, b) => b[1] - a[1]).slice(0, 3);
  }, [decisions]);
  return <section className="panel replay-decision-panel" aria-label="Replay decisions">
    <div className="panel-title"><div><p className="eyebrow">DECISION FEED</p><h2>Signals and trading decisions</h2></div><span className="tag">{decisions.length} recent assessments</span></div>
    <p className="helper">Repeated outcomes are grouped. Detailed evidence is opened on demand; full evaluations remain in the durable journal.</p>
    {blockers.length > 0 && <div className="replay-reason-chips" aria-label="Recent blocking reasons">{blockers.map(([reason, count]) =>
      <button key={reason} onClick={() => { setScope('blocked'); setQuery(reason); }}>{readable(reason)} <b>{count}</b></button>)}</div>}
    <div className="toolbar"><label>Decision view <select value={scope} onChange={event => setScope(event.target.value)}>
      <option value="highlights">Signals &amp; trades</option><option value="all">All assessments</option><option value="blocked">Blocked / missing evidence</option></select></label>
      <label>Search decisions <input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Contract, action or reason" /></label>
      <label>Decision page size <select value={size} onChange={event => setSize(Number(event.target.value))}><option value={10}>10</option><option value={25}>25</option><option value={50}>50</option></select></label></div>
    {grouped.length === 0 ? <div className="replay-feed-empty"><strong>{scope === 'highlights' ? 'No new signal or trade in the recent snapshot' : 'No matching decisions'}</strong>
      <p>Use the blocking-reason chips or All assessments to inspect why the Engine is waiting.</p></div>
      : <div className="replay-decision-feed">{grouped.slice(current * size, (current + 1) * size).map(group => {
        const row = group.first;
        const contracts = [...new Set(group.rows.map(item => item.contractId))];
        const detail = group.rows.find(item => item.contractId === selectedContract) ?? row;
        return <article className="replay-decision-card" key={group.key}>
          <div className="replay-decision-heading"><time>{ist(row.timeUtc ?? null)}</time><span className={`replay-action ${highlight(row) ? 'important' : ''}`}>{readable(row.action)}</span>
            <span className="tag">{row.class}</span><span>{contracts.length === 1 ? row.contractId || 'Session' : `${contracts.length} contracts`}</span>
            <button aria-expanded={expanded === group.key} onClick={() => { setExpanded(expanded === group.key ? null : group.key); setSelectedContract(row.contractId); }}>Inspect decision</button></div>
          <p>{row.reasons.map(readable).join(' · ')}</p>
          {expanded === group.key && <div className="replay-decision-evidence">
            {contracts.length > 1 && <label>Evidence contract <select value={detail.contractId} onChange={event => setSelectedContract(event.target.value)}>{contracts.map(id => <option key={id} value={id}>{id}</option>)}</select></label>}
            <p className="helper">{detail.indexId} · {detail.contractId} · setup {detail.setupId ?? 'not triggered'}</p>
            {detail.rank != null && <p>Eligible rank {detail.rank} (not a probability). Model spread {num(detail.rankingMetrics?.spreadPercent ?? null)}% · friction {num(detail.rankingMetrics?.roundTripFriction ?? null)} · strike distance {num(detail.rankingMetrics?.strikeDistance ?? null)}</p>}
            <p className="helper">Reason codes: {detail.reasons.join(' · ')}</p>
            <div className="table-scroll"><table><thead><tr><th>Gate</th><th>Outcome</th><th>Evidence</th><th>Observed / limit</th></tr></thead><tbody>{detail.gates?.map((gate, i) => <tr key={i}><th>{gate.code}</th>
              <td>{typeof gate.outcome === 'number' ? ['PASS', 'FAIL', 'UNKNOWN', 'NOT_EVALUATED'][gate.outcome] : gate.outcome}</td><td>{gate.detail}</td><td>{num(gate.observed)} / {num(gate.threshold)}</td></tr>)}</tbody></table></div>
          </div>}
        </article>;
      })}</div>}
    <Pager page={current} pages={pages} setPage={setPage} count={grouped.length} />
  </section>;
}

export function PaperEventTable({ rows }: { rows: PaperLogRow[] }) {
  const [page, setPage] = useState(0); const [search, setSearch] = useState('');
  const filtered = rows.filter(row => `${row.contractId} ${row.action} ${row.reasonsJson}`.toLowerCase().includes(search.toLowerCase()));
  const pages = Math.ceil(filtered.length / 10); const current = Math.min(page, Math.max(0, pages - 1));
  useEffect(() => setPage(0), [search]);
  const reasons = (json: string) => { try { const value: unknown = JSON.parse(json); return Array.isArray(value) ? value.filter(item => typeof item === 'string').map(readable).join(' · ') : json; } catch { return json; } };
  return <><label>Search paper events <input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Contract or lifecycle action" /></label>
    <div className="table-scroll replay-event-table"><table><thead><tr><th>Sequence / time</th><th>Contract</th><th>Class</th><th>Action / reasons</th></tr></thead>
      <tbody>{filtered.slice(current * 10, (current + 1) * 10).map(row => <tr key={`${row.runId}-${row.sequence}`}><td>#{row.sequence}<small>{ist(row.timeUtc)}</small></td><th>{row.contractId || 'Session'}</th><td>{row.category}</td><td><strong>{row.action}</strong><small>{reasons(row.reasonsJson)}</small></td></tr>)}</tbody></table></div>
    <Pager page={current} pages={pages} setPage={setPage} count={filtered.length} label="events" /></>;
}
