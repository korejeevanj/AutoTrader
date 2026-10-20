import { useId } from 'react';
import type { SnapshotEvidence, OptionRow, QuoteState } from './model';
import { count, ist, money, num, percent } from './format';

export function QuoteBadge({ state }: { state: QuoteState | 'partial' }) {
  return <span className={`quote-badge ${state === 'fresh' ? 'positive' : state === 'partial' || state === 'missing' ? 'warning' : 'negative'}`}>{state === 'fresh' ? '● Fresh fixture' : `● ${state}`}</span>;
}
export function UnavailablePanel({ title, message, className = '' }: { title: string; message: string; className?: string }) {
  const heading = useId();
  return <section className={`panel ${className}`} aria-labelledby={heading}><div className="panel-title"><h2 id={heading}>{title}</h2><span className="tag">Unavailable</span></div><p className="helper">{message}</p></section>;
}
export function ConstituentPanel({ snapshot: s, label, weighted = true }: { snapshot: SnapshotEvidence; label: string; weighted?: boolean }) {
  const heading = useId();
  const rows = s.constituents ?? s.banks;
  const title = /participation$/i.test(label) ? label : `${label.replace(/s$/, '')} participation`;
  return <section className="panel" aria-labelledby={heading}><div className="panel-title"><div><p className="eyebrow">02 / CONFIRMATION</p><h2 id={heading}>{title}</h2></div><span className="tag">{weighted ? 'Illustrative weights' : 'Weights unavailable'}</span></div>
    {!weighted && <p className="helper">Weighted participation unavailable: this catalog index has weightedConstituents disabled. Constituent price observations remain available; no weighted confirmation is implied.</p>}
    <div className="participation"><span className="positive">↑ Above VWAP <strong>{percent(s.participation.aboveVwapWeightPercent)}</strong></span><span className="negative">↓ Below <strong>{percent(s.participation.belowVwapWeightPercent)}</strong></span><span className="warning">Missing weight <strong>{percent(s.participation.missingWeightPercent)}</strong></span></div>
    <p className="helper">{s.participation.coverageLabel} · Missing inputs are not renormalized.</p>
    <div className="table-scroll" tabIndex={0} role="region" aria-label={`${label} observations`}><table><thead><tr><th>Constituent</th><th>Weight</th><th>Last</th><th>VWAP ≈</th><th>Change</th><th>Relation</th><th>State</th></tr></thead><tbody>{rows.map(b => <tr key={b.id}><th scope="row">{b.name}</th><td>{percent(b.weightPercent)}</td><td>{num(b.lastPrice)}</td><td>{num(b.vwap)}</td><td className={b.changePercent === null ? '' : b.changePercent >= 0 ? 'positive' : 'negative'}>{b.changePercent === null ? 'Unavailable' : `${b.changePercent >= 0 ? '↑' : '↓'} ${percent(b.changePercent)}`}</td><td>{b.aboveVwap === null ? 'Unavailable' : b.aboveVwap ? 'Above' : 'Below'}</td><td><QuoteBadge state={b.state} /></td></tr>)}</tbody></table></div>
    <p className="helper">Fixture constituent observations · no current constituent or weight claim.</p>
  </section>;
}
export function OptionsPanel({ options, selected, onSelect, busy }: { options: OptionRow[]; selected: string | null; onSelect: (id: string) => void; busy: boolean }) {
  return <section className="panel options-panel" aria-labelledby="options-heading"><div className="panel-title"><div><p className="eyebrow">04 / CONTRACT WORKSPACE</p><h2 id="options-heading">Option candidates</h2></div><span className="tag">{options.length} synthetic contracts</span></div>
    <p className="helper">Illustrative decisions only. WATCH is not a recommendation; no score or probability.{busy && ' Saving plan: contract selection is temporarily paused.'}</p>
    <div className="table-scroll candidate-scroll" tabIndex={0} role="region" aria-label="Synthetic option observations"><table><thead><tr><th>Contract / select</th><th>Expiry</th><th>Lot / tick</th><th>Last</th><th>Bid / ask</th><th>Spread</th><th>Depth bid / ask</th><th>Age / fixture</th><th>Decision / reasons</th></tr></thead><tbody>{options.map(o => <tr key={o.contract.id} className={selected === o.contract.id ? 'selected-row' : ''}><th scope="row"><button id={`option-${o.contract.id}`} disabled={busy} aria-label={`Select ${o.contract.symbol}`} aria-pressed={selected === o.contract.id} onClick={() => onSelect(o.contract.id)} className="contract-button">{o.contract.symbol}<small>{o.contract.exchange} · {o.contract.optionType} · strike {num(o.contract.strike)}</small></button></th><td>{o.contract.expiry}</td><td>{count(o.contract.lotSize)} / {num(o.contract.tickSize)}</td><td>{num(o.quote.lastPrice)}</td><td>{num(o.quote.bid)} / {num(o.quote.ask)}</td><td>{num(o.spread)}</td><td>{count(o.quote.bidQuantity)} / {count(o.quote.askQuantity)}</td><td><QuoteBadge state={o.quote.state} /><small>{o.quoteAgeSeconds === null ? 'Age unavailable' : `${num(o.quoteAgeSeconds)}s`}</small></td><td><span className={o.decision.action === 'WATCH' ? 'warning' : 'muted'}>{o.decision.action}</span><small>{o.decision.reasonCodes.join(' · ') || 'No reasons supplied'}</small></td></tr>)}</tbody></table></div>
  </section>;
}
export function PositionPanel({ snapshot: s }: { snapshot: SnapshotEvidence }) {
  const heading = useId();
  const help = useId();
  const p = s.position;
  return <section className="panel position-panel" aria-labelledby={heading}><div className="panel-title"><div><p className="eyebrow">03 / PAPER MONITOR</p><h2 id={heading}>Preset position</h2></div><span className={`tag ${p.state === 'unpriced' ? 'negative' : ''}`}>{p.state}</span></div>
    <p className={p.state === 'unpriced' ? 'negative' : 'muted'}>{p.message}</p>
    {p.state !== 'none' && <><p>{s.options.find(o => o.contract.id === p.contractId)?.contract.symbol ?? p.contractId ?? 'Contract unavailable'}</p><dl className="metric-grid"><div><dt>Whole lots / units</dt><dd>{count(p.lots)} / {count(p.units)}</dd></div><div><dt>Entry / current bid</dt><dd>{money(p.entryPrice)} / {money(p.currentBid)}</dd></div><div><dt>Gross indicative P&amp;L</dt><dd className={p.grossPnl === null ? '' : p.grossPnl >= 0 ? 'positive' : 'negative'}>{money(p.grossPnl)}</dd></div><div><dt>Net P&amp;L</dt><dd>{money(p.netPnl)}</dd></div></dl></>}
    <div className="button-group"><button disabled aria-describedby={help}>Paper entry</button><button disabled aria-describedby={help}>Paper exit</button><button disabled aria-describedby={help}>Enable paper-auto</button></div>
    <p className="helper" id={help}>Paper-auto paused. Execution is not implemented; preset observations are not fills. Net P&amp;L / fees unavailable.</p>
  </section>;
}
export function QuoteDetails({ row }: { row: OptionRow }) {
  const c = row.contract, q = row.quote;
  return <><dl className="metric-grid"><div><dt>Bid / ask</dt><dd>{money(q.bid)} / {money(q.ask)}</dd></div><div><dt>Last / spread</dt><dd>{money(q.lastPrice)} / {num(row.spread)}</dd></div><div><dt>Observed depth bid / ask</dt><dd>{count(q.bidQuantity)} / {count(q.askQuantity)}</dd></div><div><dt>Quote age / fixture clock</dt><dd>{row.quoteAgeSeconds === null ? 'Unavailable' : `${num(row.quoteAgeSeconds)}s`}</dd></div></dl>
    <details><summary>Contract &amp; source timestamps</summary><dl className="detail-list"><dt>Contract ID</dt><dd>{c.id}</dd><dt>Exchange / underlying</dt><dd>{c.exchange} / {c.underlying}</dd><dt>Expiry / strike / type</dt><dd>{c.expiry} / {num(c.strike)} / {c.optionType}</dd><dt>Lot / tick size</dt><dd>{count(c.lotSize)} / {num(c.tickSize)}</dd><dt>Valid from / to</dt><dd>{ist(c.validFromUtc)} / {ist(c.validToUtc)}</dd><dt>Event time</dt><dd>{ist(q.eventTimeUtc)}</dd><dt>Received</dt><dd>{ist(q.receivedAtUtc)}</dd><dt>Last trade</dt><dd>{ist(q.lastTradeTimeUtc)}</dd></dl></details></>;
}
