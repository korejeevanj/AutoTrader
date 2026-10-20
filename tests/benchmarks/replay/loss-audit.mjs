// Read-only ledger inspection. No provider requests or replay-state changes.
import { DatabaseSync } from 'node:sqlite';
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { gunzipSync } from 'node:zlib';

const database = resolve(process.argv[2] ?? 'data/replay/paper-replay.sqlite');
const date = process.argv[3] ?? '2026-10-14';
const db = new DatabaseSync(database, { readOnly: true });
const rows = db.prepare('SELECT run_id, input_hash, time_utc, snapshot_json FROM paper_runs WHERE instr(snapshot_json, ?) > 0 ORDER BY run_id').all(date);
const paise = value => Math.round(value * 100);
const reports = [];
for (const row of rows) {
  const snapshot = JSON.parse(row.snapshot_json);
  const paper = snapshot.Paper;
  const trades = snapshot.Trades ?? [];
  const closed = trades.filter(trade => trade.NetPnl != null);
  const gross = closed.reduce((sum, trade) => sum + paise(trade.GrossPnl), 0);
  const fees = closed.reduce((sum, trade) => sum + paise(trade.EntryFees.Total) + paise(trade.ExitFees.Total), 0);
  const net = closed.reduce((sum, trade) => sum + paise(trade.NetPnl), 0);
  for (const trade of closed) {
    assert.equal(paise((trade.ExitPrice - trade.EntryPrice) * trade.Units), paise(trade.GrossPnl), `Gross fill reconciliation: ${trade.Id}`);
    assert.equal(paise(trade.GrossPnl) - paise(trade.EntryFees.Total) - paise(trade.ExitFees.Total), paise(trade.NetPnl), `Net fee reconciliation: ${trade.Id}`);
  }
  if (paper) assert.equal(net, paise(paper.RealizedNetPnl), `Realized ledger reconciliation: ${row.run_id}`);
  const lifecycle = db.prepare("SELECT time_utc,category,action,contract_id,setup_id,reasons_json FROM paper_events WHERE run_id=? AND action IN ('HYPOTHETICAL_INTENT_ACCEPTED','HYPOTHETICAL_INTENT_CANCELED','HYPOTHETICAL_FILLED','HYPOTHETICAL_EXIT_FILLED','PAUSE_NEW_ENTRIES','ENABLE_NEW_ENTRIES') ORDER BY sequence").all(row.run_id)
    .map(event => ({ ...event, reasons_json: JSON.parse(event.reasons_json) }));
  const manifest = db.prepare('SELECT manifest_json FROM paper_run_manifests WHERE run_id=?').get(row.run_id);
  const frozen = manifest ? JSON.parse(manifest.manifest_json) : null;
  const details = process.argv.includes('--details');
  const policyTimes = details ? db.prepare("SELECT DISTINCT time_utc FROM paper_events WHERE run_id=? AND category='CANDLE_POSITION_POLICY' ORDER BY time_utc").all(row.run_id).map(event => event.time_utc) : [];
  const times = details ? [...new Set([...lifecycle.map(event => event.time_utc), ...policyTimes])].sort() : [];
  const batches = [];
  for (const time of times) {
    const event = db.prepare('SELECT sequence FROM paper_events WHERE run_id=? AND time_utc=? LIMIT 1').get(row.run_id, time);
    const batch = db.prepare('SELECT b.payload_gzip FROM paper_batches b JOIN paper_batch_ranges r ON b.run_id=r.run_id AND b.batch_id=r.batch_id WHERE b.run_id=? AND ? BETWEEN r.first_sequence AND r.last_sequence').get(row.run_id, event.sequence);
    if (!batch) continue;
    const step = JSON.parse(gunzipSync(batch.payload_gzip).toString('utf8'));
    const selected = new Set(trades.map(trade => trade.ContractId));
    const decisionSummary = decision => ({ contract: decision.ContractId, class: decision.Class, action: decision.Action,
      rank: decision.Rank, metrics: decision.RankingMetrics, reasons: decision.Reasons,
      gates: Object.fromEntries(decision.Gates.filter(gate => ['CONSTITUENT_CONFIRMATION', 'CONSTITUENT_BREADTH', 'RESEARCH_MODELLED_RISK',
        'RESEARCH_MODELLED_SPREAD', 'RESEARCH_CASH', 'NET_LIQUIDATION', 'PEAK_NET_LIQUIDATION', 'PROFIT_EXIT_POLICY',
        'GROSS_CASH_STOP', 'NET_PROFIT_TARGET', 'NET_PROFIT_TRAIL_THRESHOLD'].includes(gate.Code)).map(gate => [gate.Code, gate.Observed ?? gate.Outcome])) });
    batches.push({ timeUtc: step.TimeUtc, features: step.Features.map(feature => {
      const value = feature.DirectionalEvidence;
      return { index: feature.IndexId, setupId: feature.SetupId, status: value?.Status, close: value?.Close,
        openingHigh: feature.OpeningHigh, openingLow: feature.OpeningLow, vwap: feature.Vwap,
        aboveWeight: feature.AboveWeight, belowWeight: feature.BelowWeight, missingWeight: feature.MissingWeight,
        confirmingCount: value?.ConfirmingCount, opposingCount: value?.OpposingCount,
        topThreeConfirmingWeight: value?.TopThreeConfirmingWeightPercent, return5MinutePercent: value?.Return5MinutePercent,
        ...(process.argv.includes('--constituents') ? { constituents: value?.Constituents.map(member => ({ id: member.Id, weight: member.WeightPercent, close: member.LastPrice, vwap: member.Vwap, state: member.State })) } : {}) };
    }),
      selectedDecisions: step.Decisions.filter(decision => selected.has(decision.ContractId)).map(decisionSummary),
      topCandidates: step.Decisions.filter(decision => decision.Class === 'CANDLE_RESEARCH' && decision.Rank != null)
        .sort((left, right) => left.Rank - right.Rank).slice(0, 5).map(decisionSummary) });
  }
  reports.push({ runId: row.run_id, timeUtc: row.time_utc, inputHash: row.input_hash,
    complete: snapshot.Complete, qualified: snapshot.Qualified,
    reconciliation: 'PASS: fill gross, individual net and aggregate realized ledger',
    gross: gross / 100, fees: fees / 100, net: net / 100, paper, trades, lifecycle,
    manifestFields: frozen ? Object.keys(frozen) : [],
    ...(details ? { engineConfiguration: frozen?.engineConfiguration, replayConfiguration: frozen?.replayConfiguration,
      warnings: frozen?.warnings, issueCounts: Object.fromEntries([...new Set((frozen?.issues ?? []).map(issue => issue.Code))]
        .map(code => [code, frozen.issues.filter(issue => issue.Code === code).length])), batches } : {}) });
}
db.close();
console.log(JSON.stringify({ date, runs: reports }, null, 2));
