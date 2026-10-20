using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public sealed record HistoricalOptionEvidence(string InstrumentId, string State, string Reason);
public sealed record HistoricalRequiredEvidence(string InstrumentId, string State, string Reason, DateTimeOffset? MissingFromUtc = null);
public sealed record HistoricalReadinessSummary(bool Qualified, int DecisionTimes, int IncompleteDecisionTimes,
    int MaximumUnknownOptions, DateTimeOffset? FirstIncompleteAtUtc, IReadOnlyList<string> Issues);
public sealed record HistoricalDecisionReadiness(DateTimeOffset TimeUtc, string IndexId,
    int DeclaredOptions, int UsableOptions, int ExcludedOptions, int UnknownOptions,
    IReadOnlyList<string> Issues, IReadOnlyList<HistoricalOptionEvidence> Options,
    IReadOnlyList<HistoricalRequiredEvidence>? RequiredInputs = null)
{
    public bool Qualified => Issues.Count == 0;
}

// A supplied clock and immutable input catalogue are the only sources of evidence.
// Preparation and replay use the same rules; a later candle never repairs an earlier decision.
public sealed class HistoricalReadiness(HistoricalDataset dataset, ReplayConfiguration settings, EngineConfiguration engine)
{
    private readonly Dictionary<string, HistoricalCandle[]> bars = dataset.Candles.GroupBy(bar => bar.InstrumentId)
        .ToDictionary(group => group.Key, group => group.OrderBy(bar => bar.AvailableAtUtc).ToArray(), StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<DateTimeOffset, HistoricalCandle[]>> periods = dataset.Candles.GroupBy(bar => bar.InstrumentId)
        .ToDictionary(group => group.Key, group => group.GroupBy(bar => bar.StartUtc).ToDictionary(period => period.Key, period => period.ToArray()), StringComparer.Ordinal);
    private readonly Dictionary<string, HistoricalInstrument> definitions = dataset.Manifest.Instruments.ToDictionary(item => item.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, HistoricalQualityIssue[]> invalid = dataset.Issues
        .Where(issue => issue.Code is not ("MISSING_MINUTE" or "NO_OPTION_CANDLES" or "SPARSE_OPTION_CANDLES" or "OPTION_FILE_UNAVAILABLE"))
        .GroupBy(issue => issue.InstrumentId).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    public HistoricalDecisionReadiness At(DateOnly date, IndexStrategyConfiguration profile, DateTimeOffset time)
    {
        var options = dataset.Manifest.Instruments.Where(item => item.IndexId == profile.IndexId
            && item.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put).OrderBy(item => item.Id, StringComparer.Ordinal).Select(item =>
        {
            if (!item.Verified || item.AvailableAtUtc > time || item.ProviderMappingVerified == false
                || item.Expiry is null || item.LotSize is null or <= 0 || item.TickSize is null or <= 0m || item.Strike is null or <= 0m
                || item.ValidFromUtc >= item.ValidToUtc
                || string.IsNullOrWhiteSpace(item.DefinitionVersion))
                return new HistoricalOptionEvidence(item.Id, "Unknown", "OPTION_IDENTITY_UNQUALIFIED");
            if (time < item.ValidFromUtc || time >= item.ValidToUtc)
                return new HistoricalOptionEvidence(item.Id, "Excluded", "OUTSIDE_CONTRACT_VALIDITY");
            if (item.Expiry < date || profile.ExcludeExpiryDay && item.Expiry == date)
                return new HistoricalOptionEvidence(item.Id, "Excluded", "EXPIRY_POLICY");
            var candle = Current(item.Id, time);
            if (candle is null || invalid.GetValueOrDefault(item.Id)?.Any(issue => issue.StartUtc is null || issue.StartUtc == candle.StartUtc) == true)
                return new HistoricalOptionEvidence(item.Id, "Unknown", "CURRENT_OPTION_CANDLE_UNAVAILABLE_OR_INVALID");
            return candle.Volume == 0m ? new HistoricalOptionEvidence(item.Id, "Excluded", "OBSERVED_ZERO_VOLUME")
                : new HistoricalOptionEvidence(item.Id, "Usable", "CURRENT_COMPLETED_CANDLE");
        }).ToArray();
        var issues = new List<string>();
        if (!settings.EnableCandleResearch) issues.Add("CANDLE_RESEARCH_DISABLED");
        if (dataset.Manifest.Instruments.Count(item => item.IndexId == profile.IndexId && item.Role == HistoricalInstrumentRole.CashIndex) != 1)
            issues.Add("CASH_INDEX_UNAVAILABLE");
        if (!dataset.Manifest.CalendarVerified || string.IsNullOrWhiteSpace(dataset.Manifest.CalendarSource)) issues.Add("CALENDAR_UNVERIFIED");
        if (!EvidenceQualified(dataset.Manifest.CalendarPublicationEvidence, time)) issues.Add("CALENDAR_PUBLICATION_UNQUALIFIED");
        if (dataset.Manifest.IncludesAllOptions != true || dataset.Manifest.UniverseAvailableAtUtc > time) issues.Add("OPTION_UNIVERSE_UNQUALIFIED");
        issues.AddRange(WeightIssues(date, profile, time));
        var weight = dataset.Manifest.Weights.FirstOrDefault(item => item.IndexId == profile.IndexId && item.AvailableAtUtc <= time
            && item.EffectiveFromUtc <= time && (item.EffectiveToUtc is null || time < item.EffectiveToUtc));
        if (weight is not null && !PublicationQualified(weight, time)) issues.Add("WEIGHTS_PUBLICATION_UNQUALIFIED");
        if (!settings.FeeSchedules.Any(schedule => schedule.Applies(time, profile.Risk.FeeProfileId, dataset.Manifest.IsSynthetic))) issues.Add("FEES_UNAVAILABLE");
        var requiredIds = dataset.Manifest.Instruments.Where(item => item.IndexId == profile.IndexId
                && item.Role is HistoricalInstrumentRole.CashIndex or HistoricalInstrumentRole.Futures)
            .Select(item => item.Id).Concat(weight?.Members.Select(member => member.InstrumentId) ?? []).Distinct(StringComparer.Ordinal);
        var required = requiredIds.Order(StringComparer.Ordinal).Select(id => RequiredPrefix(id, date, profile.IndexId, time)).ToArray();
        if (required.Any(item => item.State != "Complete")) issues.Add("REQUIRED_SERIES_INCOMPLETE");
        if (options.Length == 0) issues.Add("OPTION_UNIVERSE_UNAVAILABLE");
        if (options.Any(option => option.State == "Unknown")) issues.Add("SELECTION_EVIDENCE_INCOMPLETE");
        return new(time, profile.IndexId, options.Length, options.Count(option => option.State == "Usable"),
            options.Count(option => option.State == "Excluded"), options.Count(option => option.State == "Unknown"), issues.AsReadOnly(), options, required);
    }

    private HistoricalRequiredEvidence RequiredPrefix(string id, DateOnly date, string indexId, DateTimeOffset time)
    {
        if (!definitions.TryGetValue(id, out var definition)) return new(id, "Unknown", "REQUIRED_IDENTITY_UNQUALIFIED");
        var midnight = Trading.Data.HistoricalAcquisitionStore.Midnight(date, dataset.Manifest.Indices.Single(index => index.Id == indexId).TimeZone);
        var finish = time.Subtract(settings.PublicationLag);
        if (definition.ContinuousTradingClose is { } close && midnight.Add(close.ToTimeSpan()) < finish) finish = midnight.Add(close.ToTimeSpan());
        if (!definition.Verified || definition.ProviderMappingVerified == false || definition.AvailableAtUtc > time
            || definition.ValidFromUtc > midnight.Add(dataset.Manifest.MarketOpen.ToTimeSpan()) || finish > definition.ValidToUtc
            || string.IsNullOrWhiteSpace(definition.DefinitionVersion)) return new(id, "Unknown", "REQUIRED_IDENTITY_UNQUALIFIED");
        for (var start = midnight.Add(dataset.Manifest.MarketOpen.ToTimeSpan()); start.AddMinutes(1) <= finish; start = start.AddMinutes(1))
        {
            var found = periods.GetValueOrDefault(id)?.GetValueOrDefault(start) ?? [];
            if (found.Length != 1 || found[0].AvailableAtUtc > time || found[0].EndUtc != start.AddMinutes(1))
                return new(id, "Unknown", "REQUIRED_PREFIX_GAP", start);
        }
        return HasInvalidData(id, date, finish) ? new(id, "Unknown", "REQUIRED_SOURCE_INVALID") : new(id, "Complete", "COMPLETED_PREFIX_AVAILABLE");
    }

    private bool HasInvalidData(string id, DateOnly date, DateTimeOffset time)
    {
        var midnight = Trading.Data.HistoricalAcquisitionStore.Midnight(date, dataset.Manifest.Indices[0].TimeZone);
        return invalid.GetValueOrDefault(id)?.Any(issue => issue.StartUtc is null
            || issue.StartUtc >= midnight && issue.StartUtc < midnight.AddDays(1) && issue.StartUtc.Value.AddMinutes(1) <= time) == true;
    }

    private static bool EvidenceQualified(HistoricalPublicationEvidence? evidence, DateTimeOffset time) => evidence is not null
        && evidence.Method is HistoricalAvailabilityMethod.OfficialPublication or HistoricalAvailabilityMethod.ContemporaneousCapture
        && !string.IsNullOrWhiteSpace(evidence.Source) && evidence.EstablishedAtUtc <= time
        && evidence.Sha256 is { Length: 64 }
        && evidence.Sha256.All(Uri.IsHexDigit);
    private static bool PublicationQualified(HistoricalWeightSet weights, DateTimeOffset time) => EvidenceQualified(weights.PublicationEvidence, time)
        && weights.PublicationEvidence!.EstablishedAtUtc <= weights.AvailableAtUtc;

    public IReadOnlyList<string> WeightIssues(DateOnly date, IndexStrategyConfiguration profile, DateTimeOffset time)
    {
        var weights = dataset.Manifest.Weights.Where(item => item.IndexId == profile.IndexId && item.AvailableAtUtc <= time
            && item.EffectiveFromUtc <= time && (item.EffectiveToUtc is null || time < item.EffectiveToUtc)).ToArray();
        if (weights.Length != 1 || !weights[0].Verified || string.IsNullOrWhiteSpace(weights[0].Source)
            || weights[0].Members.Count == 0 || weights[0].Members.Any(member => member.WeightPercent is null or <= 0m)
            || weights[0].Members.Select(member => member.InstrumentId).Distinct(StringComparer.Ordinal).Count() != weights[0].Members.Count
            || Math.Abs(weights[0].Members.Sum(member => member.WeightPercent!.Value) - 100m) > profile.WeightTotalTolerancePercent)
            return ["WEIGHTS_UNAVAILABLE_OR_INVALID"];
        var snapshot = weights[0].SnapshotDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(weights[0].EffectiveFromUtc,
            TimeZoneInfo.FindSystemTimeZoneById(dataset.Manifest.Indices.Single(index => index.Id == profile.IndexId).TimeZone)).DateTime);
        if (snapshot > date) return ["WEIGHTS_SNAPSHOT_IN_FUTURE"];
        return date.DayNumber - snapshot.DayNumber > settings.MaximumWeightSnapshotAgeDays ? ["WEIGHTS_SNAPSHOT_TOO_OLD"] : [];
    }

    public IReadOnlyList<HistoricalDecisionReadiness> Day(DateOnly date) => engine.Strategies
        .Where(profile => profile.Enabled && dataset.Manifest.Indices.Any(index => index.Id == profile.IndexId))
        .SelectMany(profile => DecisionTimes(date, profile).Select(time => At(date, profile, time))).ToArray();

    public HistoricalReadinessSummary Summary(DateOnly date)
    {
        var statuses = Day(date); var incomplete = statuses.Where(status => !status.Qualified).ToArray();
        var sessionIssues = SessionIssues(date);
        return new(statuses.Count > 0 && incomplete.Length == 0 && sessionIssues.Count == 0, statuses.Count, incomplete.Length,
            statuses.Count == 0 ? 0 : statuses.Max(status => status.UnknownOptions), incomplete.MinBy(status => status.TimeUtc)?.TimeUtc,
            (statuses.Count == 0 ? new[] { "NO_ENABLED_STRATEGY_DECISIONS" } : statuses.SelectMany(status => status.Issues))
                .Concat(sessionIssues).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<string> SessionIssues(DateOnly date)
    {
        foreach (var profile in engine.Strategies.Where(profile => profile.Enabled && dataset.Manifest.Indices.Any(index => index.Id == profile.IndexId)))
        {
            var midnight = Trading.Data.HistoricalAcquisitionStore.Midnight(date, dataset.Manifest.Indices.Single(index => index.Id == profile.IndexId).TimeZone);
            var finish = midnight.Add(dataset.Manifest.MarketClose.ToTimeSpan()).Add(settings.PublicationLag);
            var ids = dataset.Manifest.Instruments.Where(item => item.IndexId == profile.IndexId
                    && item.Role is HistoricalInstrumentRole.CashIndex or HistoricalInstrumentRole.Futures).Select(item => item.Id);
            if (ids.Any(id => RequiredPrefix(id, date, profile.IndexId, finish).State != "Complete")) return ["REQUIRED_SESSION_SERIES_INCOMPLETE"];
            foreach (var weights in dataset.Manifest.Weights.Where(weights => weights.IndexId == profile.IndexId
                && weights.AvailableAtUtc < finish.Subtract(settings.PublicationLag) && weights.EffectiveFromUtc < finish.Subtract(settings.PublicationLag)
                && (weights.EffectiveToUtc is null || weights.EffectiveToUtc > midnight.Add(dataset.Manifest.MarketOpen.ToTimeSpan()))))
            {
                var memberEnd = weights.EffectiveToUtc is { } end && end.Add(settings.PublicationLag) < finish ? end.Add(settings.PublicationLag) : finish;
                if (weights.Members.Any(member => RequiredPrefix(member.InstrumentId, date, profile.IndexId, memberEnd).State != "Complete"))
                    return ["REQUIRED_SESSION_SERIES_INCOMPLETE"];
            }
        }
        return [];
    }

    public IEnumerable<DateTimeOffset> DecisionTimes(DateOnly date, IndexStrategyConfiguration profile)
    {
        var index = dataset.Manifest.Indices.Single(item => item.Id == profile.IndexId);
        var midnight = Trading.Data.HistoricalAcquisitionStore.Midnight(date, index.TimeZone);
        var end = midnight.Add(profile.EntryEnd.ToTimeSpan());
        for (var time = midnight.Add(profile.EntryStart.ToTimeSpan()).Add(settings.PublicationLag); time < end; time = time.Add(profile.BarInterval))
            yield return time;
    }

    private HistoricalCandle? Current(string id, DateTimeOffset time)
    {
        if (!bars.TryGetValue(id, out var values)) return null;
        var lo = 0; var hi = values.Length - 1; HistoricalCandle? found = null;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (values[mid].AvailableAtUtc <= time) { found = values[mid]; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found is not null && found.EndUtc <= time && time - found.EndUtc < TimeSpan.FromMinutes(1) ? found : null;
    }
}

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
