using Trading.Engine;

namespace Trading.Replay;

public sealed record SetupSeen(string SetupId, string AtIst, string Direction, int CandidateEvaluations, IReadOnlyList<string> ClosestMissReasons,
    IReadOnlyDictionary<string, string> ClosestMissValues);
public sealed record GateStats(int Samples, decimal Threshold, decimal Min, decimal Median, decimal Max);

// Answers "why did the replay not trade": which setups formed, which gates rejected their candidates, by how much, and the closest miss.
// Replay-only observer. It reads decisions the engine already made and never influences them.
public sealed class ReplayDiagnostics
{
    private const int MaxSamples = 4000;
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private readonly Dictionary<string, int> reasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (decimal Threshold, List<decimal> Values)> gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string At, string Direction, int Count, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, string> Values)> setups = new(StringComparer.Ordinal);
    private int evaluationsWithoutSetup, evaluationsWithSetup;

    public IReadOnlyDictionary<string, int> ReasonCounts => reasons.OrderByDescending(item => item.Value).ToDictionary(item => item.Key, item => item.Value);
    public int EvaluationsWithoutSetup => evaluationsWithoutSetup;
    public int EvaluationsWithSetup => evaluationsWithSetup;
    public IReadOnlyList<SetupSeen> Setups => setups.Select(item => new SetupSeen(item.Key, item.Value.At, item.Value.Direction, item.Value.Count,
        item.Value.Reasons, item.Value.Values)).ToArray();
    public IReadOnlyDictionary<string, GateStats> GateValues => gates.Where(item => item.Value.Values.Count != 0).ToDictionary(item => item.Key, item =>
    {
        var sorted = item.Value.Values.Order().ToArray();
        return new GateStats(sorted.Length, item.Value.Threshold, sorted[0], sorted[sorted.Length / 2], sorted[^1]);
    });

    public void Observe(DateTimeOffset now, IReadOnlyList<CandidateAssessment> candidates)
    {
        var active = candidates.Where(candidate => !string.IsNullOrEmpty(candidate.SetupId)).ToArray();
        if (active.Length == 0) { evaluationsWithoutSetup++; return; }
        evaluationsWithSetup++;
        foreach (var candidate in active)
        {
            foreach (var reason in candidate.ReasonCodes) reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            foreach (var gate in candidate.Gates.Where(gate => gate.Outcome != GateOutcome.PASS && gate.Observed is not null && gate.Threshold is not null))
            {
                if (!gates.TryGetValue(gate.Code, out var entry)) gates[gate.Code] = entry = (gate.Threshold!.Value, []);
                if (entry.Values.Count < MaxSamples) entry.Values.Add(gate.Observed!.Value);
            }
        }
        var closest = active.OrderBy(candidate => candidate.ReasonCodes.Count).First();
        var known = setups.TryGetValue(closest.SetupId, out var seen);
        var better = !known || closest.ReasonCodes.Count < seen.Reasons.Count;
        setups[closest.SetupId] = (known ? seen.At : (now + Ist).ToString("HH:mm:ss"), known ? seen.Direction : closest.Direction?.ToString() ?? "?",
            (known ? seen.Count : 0) + active.Length, better ? closest.ReasonCodes : seen.Reasons,
            better ? closest.Gates.Where(gate => gate.Outcome != GateOutcome.PASS && gate.Observed is not null)
                .ToDictionary(gate => gate.Code, gate => $"{gate.Observed} vs limit {gate.Threshold}") : seen.Values);
    }
}
