using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

/// <summary>
/// The outcome of preparing one historical day: the replay and engine settings after
/// the preparation gates, the loaded dataset, its qualification and every warning.
/// Hosts record this verdict; they do not re-derive it.
/// </summary>
public sealed record PreparationVerdict(ReplayConfiguration Replay, EngineConfiguration Engine, HistoricalDataset Dataset,
    HistoricalReadinessSummary Qualification, IReadOnlyList<string> Warnings, int DataErrors)
{
    public bool Ready => Warnings.Count == 0 && Qualification.Qualified;
    /// <summary>Every warning plus the qualification's own issues, as the job reports them.</summary>
    public IReadOnlyList<string> Issues => Warnings.Concat(Qualification.Issues).Distinct().ToArray();

    public static PreparationVerdict Evaluate(ReplayConfiguration replay, EngineConfiguration engine, HistoricalAcquisitionPlan plan,
        string manifestPath, DateOnly day, TimeOnly open, TimeOnly close)
    {
        var warnings = plan.Warnings.ToList();
        if (!plan.AllOptionsCovered) replay = replay with { EnableCandleResearch = false };
        var profile = engine.Strategies.SingleOrDefault(profile => profile.IndexId == "BANKNIFTY")
            ?? throw new InvalidDataException("BANKNIFTY strategy profile missing.");
        if (profile.OpeningStart != open || profile.SessionExit >= close)
        {
            warnings.Add("SPECIAL_SESSION_REQUIRES_DATE_SPECIFIC_STRATEGY_PROFILE");
            replay = replay with { EnableCandleResearch = false };
            engine = engine with { Strategies = engine.Strategies.Select(item => item with { Enabled = false }).ToArray() };
        }
        var dataset = KiteCandleImporter.Load(manifestPath, replay.PublicationLag);
        var qualification = new HistoricalReadiness(dataset, replay, engine).Summary(day);
        var optionCoverage = dataset.Coverage?.Where(row => row.Role is "Call" or "Put").ToArray() ?? [];
        if (optionCoverage.Any(row => row.State == "Unavailable")) warnings.Add($"OPTION_SERIES_UNAVAILABLE:{optionCoverage.Count(row => row.State == "Unavailable")}");
        if (optionCoverage.Any(row => row.State == "Sparse")) warnings.Add($"OPTION_SERIES_SPARSE:{optionCoverage.Count(row => row.State == "Sparse")}");
        var dataErrors = dataset.Issues.Count(issue => issue.Code is not ("SPARSE_OPTION_CANDLES" or "NO_OPTION_CANDLES" or "OPTION_FILE_UNAVAILABLE"));
        if (dataErrors != 0) warnings.Add($"DATA_QUALITY_ERRORS:{dataErrors}");
        var entry = HistoricalAcquisitionStore.Midnight(day, "Asia/Kolkata").Add(profile.EntryStart.ToTimeSpan());
        if (!replay.FeeSchedules.Any(schedule => schedule.Applies(entry, profile.Risk.FeeProfileId, dataset.Manifest.IsSynthetic)))
            warnings.Add("FEES_UNAVAILABLE_FOR_RESEARCH");
        return new(replay, engine, dataset, qualification, warnings, dataErrors);
    }
}
