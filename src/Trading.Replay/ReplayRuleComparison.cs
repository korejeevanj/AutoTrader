using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public sealed record RuleComparisonSession(DateOnly TradingDate, string PresetId, bool QualifiedPair,
    int BaselineTrades, int VariantTrades, decimal BaselineDiagnosticNet, decimal VariantDiagnosticNet,
    decimal? NetDifference, IReadOnlyList<string> BaselineIssues, IReadOnlyList<string> VariantIssues);
public sealed record ReplayRuleComparisonResult(ReplayRunResult Baseline, ReplayRunResult Variant,
    IReadOnlyList<RuleComparisonSession> Sessions);

public static class ReplayRuleComparison
{
    // The supplied policy is the baseline. Only the enabled strategies' optional breadth count changes.
    public static ReplayRuleComparisonResult Run(HistoricalDataset dataset, ReplayConfiguration replay,
        EngineConfiguration baseline, int minimumConfirmingConstituents)
    {
        if (minimumConfirmingConstituents <= 0) throw new ArgumentOutOfRangeException(nameof(minimumConfirmingConstituents));
        if (!baseline.Strategies.Any(profile => profile.Enabled && dataset.Manifest.Indices.Any(index => index.Id == profile.IndexId)))
            throw new ArgumentException("Rule comparison requires an explicitly enabled index strategy.", nameof(baseline));
        var variant = baseline with { Version = $"{baseline.Version}:breadth-{minimumConfirmingConstituents}",
            Strategies = baseline.Strategies.Select(profile => profile.Enabled
                ? profile with { MinimumConfirmingConstituents = minimumConfirmingConstituents } : profile).ToArray() };
        var first = new ReplayRunner(dataset, replay, baseline).RunToEnd();
        var second = new ReplayRunner(dataset, replay, variant).RunToEnd();
        var variants = second.Sessions.ToDictionary(session => (session.TradingDate, session.PresetId));
        var pairs = first.Sessions.Select(session =>
        {
            var other = variants[(session.TradingDate, session.PresetId)];
            var qualified = session.Complete && other.Complete;
            return new RuleComparisonSession(session.TradingDate, session.PresetId, qualified, session.Trades.Count,
                other.Trades.Count, session.RealizedNetPnl, other.RealizedNetPnl,
                qualified ? other.RealizedNetPnl - session.RealizedNetPnl : null, session.CompletionIssues, other.CompletionIssues);
        }).ToArray();
        return new(first, second, Array.AsReadOnly(pairs));
    }
}
