import type { ReplayDirectionalEvidence } from './replayApi';
import { ist, num } from './format';

export function ReplayDirectionalEvidencePanel({ evidence }: { evidence: ReplayDirectionalEvidence | null | undefined }) {
  return <section className="panel" aria-label="Rule-based directional evidence">
    <div className="panel-title"><div><p className="eyebrow">RULE-BASED DIRECTION</p><h2>Directional assessment</h2></div>
      <span className="mode-badge">{evidence?.status ?? 'Awaiting evidence'}</span></div>
    {evidence ? <>
      <p>{evidence.meetsDirectionalRules ? 'Directional rules passed' : 'Directional rules not qualified'} · completed period {ist(evidence.barEndUtc)} · {evidence.version}</p>
      <dl className="replay-evidence-list">
        <div><dt>Close − opening high / low (points)</dt><dd>{num(evidence.closeMinusOpeningHigh)} / {num(evidence.closeMinusOpeningLow)}</dd></div>
        <div><dt>Close − approximate VWAP (points)</dt><dd>{num(evidence.closeMinusVwap)}</dd></div>
        <div><dt>Past 1 / 5 minute return</dt><dd>{num(evidence.return1MinutePercent)}% / {num(evidence.return5MinutePercent)}%</dd></div>
        <div><dt>Confirming / opposing / neutral members</dt><dd>{evidence.confirmingCount ?? 'Unavailable'} / {evidence.opposingCount ?? 'Unavailable'} / {evidence.neutralCount ?? 'Unavailable'}</dd></div>
        <div><dt>Top three confirming members' absolute weight</dt><dd>{num(evidence.topThreeConfirmingWeightPercent)}%</dd></div>
        <div><dt>Optional minimum confirming count</dt><dd>{evidence.minimumConfirmingConstituents ?? 'Disabled'}</dd></div>
      </dl>
      {evidence.reasons.length > 0 && <p className="helper">{evidence.reasons.join(' · ')}</p>}
      <p className="helper">A directional assessment is separate from option entry qualification. Future path outcomes are available in completed run reports.</p>
    </> : <p className="helper">Step or play the session to inspect available rule evidence.</p>}
  </section>;
}
