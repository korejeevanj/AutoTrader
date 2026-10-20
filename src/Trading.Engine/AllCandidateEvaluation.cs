namespace Trading.Engine;

public sealed partial class TradingDecisionEngine
{
    // A scan may cover a whole historical catalogue. Each assessment batch is bounded;
    // one common state/snapshot is used, and eligible survivors are ranked globally.
    public IndexEvaluationResult EvaluateAllCandidates(IndexEvaluationInput input, StrategyState? previousState = null)
    {
        var strategy = configuration.Strategies.SingleOrDefault(profile => profile.IndexId == input.Index.Id);
        if (strategy is null || input.Candidates.Count <= strategy.MaximumCandidates
            || input.Candidates.Select(candidate => candidate.Contract.Id).Distinct(StringComparer.Ordinal).Count() != input.Candidates.Count)
            return EvaluateIndex(input, previousState);
        var batches = input.Candidates.OrderBy(candidate => candidate.Contract.Id, StringComparer.Ordinal)
            .Chunk(strategy.MaximumCandidates).Select(batch => EvaluateIndex(input with { Candidates = batch }, previousState)).ToArray();
        var all = batches.SelectMany(batch => batch.Candidates).ToArray();
        var ranks = Rank(all.Where(candidate => candidate.Eligible), strategy).Select((candidate, i) => (candidate.ContractId, Rank: i + 1))
            .ToDictionary(item => item.ContractId, item => item.Rank, StringComparer.Ordinal);
        var context = batches[0].ContextGates.Append(new GateResult("BATCHED_UNIVERSE", GateOutcome.PASS,
            $"All {all.Length} candidates assessed in {batches.Length} batches; survivors ranked together.", all.Length, strategy.MaximumCandidates)).ToArray();
        return new(batches[0].State, Array.AsReadOnly(all.Select(candidate => candidate with
            { Rank = ranks.GetValueOrDefault(candidate.ContractId, 0) is var rank && rank > 0 ? rank : null }).ToArray()), Array.AsReadOnly(context), batches[0].DirectionalEvidence);
    }
}
