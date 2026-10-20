using Trading.Domain;

namespace Trading.Data;

public sealed record AcquisitionInstrumentEvidence(string Id, string Role, string State, int Rows, bool Cached, string? Error,
    int? ExpectedMinutes, int? MissingMinutes, IReadOnlyList<HistoricalQualityIssue> Issues, IReadOnlyList<HistoricalCoverageGap> MissingIntervals);
public sealed record AcquisitionCoverageTotals(int Total, int Processed, int Complete, int Sparse, int Empty, int Failed,
    int Pending, int Invalid, int Cached, int RequiredAffected);

/// <summary>
/// Immutable attempt-scoped progress and normalized coverage. New progress never
/// borrows a preceding attempt's dataset; normalization and refetch share this owner.
/// Qualification is supplied separately by HistoricalReadiness in the replay module.
/// </summary>
public sealed class AcquisitionAttemptEvidence
{
    private readonly IReadOnlyList<HistoricalInstrumentAcquisition> progress;
    public int Number { get; }
    public DateOnly Date { get; }
    public bool Validated { get; }
    public IReadOnlyList<AcquisitionInstrumentEvidence> Instruments { get; }
    public AcquisitionCoverageTotals Totals { get; }

    public AcquisitionAttemptEvidence(int number, DateOnly date, IEnumerable<HistoricalInstrumentAcquisition>? progress = null)
        : this(number, date, progress ?? [], null) { }

    private AcquisitionAttemptEvidence(int number, DateOnly date, IEnumerable<HistoricalInstrumentAcquisition> progress, HistoricalDataset? dataset)
    {
        Number = number; Date = date; this.progress = Array.AsReadOnly(progress.ToArray()); Validated = dataset is not null;
        if (dataset is not null && (!dataset.Manifest.TradingDates.Contains(date) || this.progress.Any(row =>
            !dataset.Manifest.Instruments.Any(instrument => instrument.Id == row.Id && instrument.InstrumentToken == row.Token && instrument.Role.ToString() == row.Role))))
            throw new InvalidDataException("Normalized coverage must describe this attempt's dated instruments.");
        var observations = dataset?.Candles.GroupBy(bar => bar.InstrumentId).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var definitions = dataset?.Manifest.Instruments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Instruments = Array.AsReadOnly(this.progress.Select(row =>
        {
            var analysis = dataset is null || definitions?.GetValueOrDefault(row.Id) is not { } instrument ? null
                : HistoricalCoverageAnalysis.ForSession(dataset.Manifest, instrument, date, observations!.GetValueOrDefault(row.Id) ?? []);
            var issues = dataset?.Issues.Where(issue => issue.InstrumentId == row.Id).ToArray() ?? [];
            var state = row.State is "Failed" or "Waiting" ? row.State : analysis?.Coverage.State
                ?? (row.State is "Complete" or "Partial" ? "AwaitingValidation" : row.State);
            if (state is not ("Failed" or "Waiting" or "Unavailable" or "AwaitingValidation")
                && issues.Any(issue => issue.Code is not ("SPARSE_OPTION_CANDLES" or "NO_OPTION_CANDLES" or "OPTION_FILE_UNAVAILABLE" or "MISSING_MINUTE"))) state = "Invalid";
            return new AcquisitionInstrumentEvidence(row.Id, row.Role, state, row.Rows, row.Cached, row.Error,
                analysis?.Coverage.ExpectedMinutes, analysis?.Coverage.MissingMinutes, Array.AsReadOnly(issues), analysis?.MissingIntervals ?? []);
        }).ToArray());
        Totals = new(Instruments.Count, this.progress.Count(row => row.State != "Waiting"), Instruments.Count(row => row.State == "Complete"),
            Instruments.Count(row => row.State is "Sparse" or "Partial"), Instruments.Count(row => row.State == "Unavailable"),
            Instruments.Count(row => row.State == "Failed"), Instruments.Count(row => row.State is "Waiting" or "AwaitingValidation"),
            Instruments.Count(row => row.State == "Invalid"), Instruments.Count(row => row.Cached),
            Instruments.Count(row => row.Role is "Futures" or "Constituent" && (row.State != "Complete" || row.Issues.Count > 0)));
    }

    public AcquisitionAttemptEvidence WithProgress(int attempt, IEnumerable<HistoricalInstrumentAcquisition> rows)
    { RequireAttempt(attempt); return new(Number, Date, rows, null); }
    public AcquisitionAttemptEvidence WithDataset(int attempt, HistoricalDataset dataset)
    { RequireAttempt(attempt); return new(Number, Date, progress, dataset); }
    public HashSet<string> RefetchTargets(bool failedOnly)
    {
        if (failedOnly) return progress.Where(row => row.State == "Failed").Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var targets = Instruments.Where(row => row.State is "Failed" or "Sparse" or "Partial" or "Unavailable" or "Invalid")
            .Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        if (!Validated) targets.UnionWith(progress.Where(row => row.State == "Partial").Select(row => row.Id));
        return targets;
    }
    private void RequireAttempt(int attempt)
    {
        if (attempt != Number) throw new InvalidOperationException("Progress and normalization must belong to the current acquisition attempt.");
    }
}
