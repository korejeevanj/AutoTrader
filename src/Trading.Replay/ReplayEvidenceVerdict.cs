using Trading.Data;

namespace Trading.Replay;

/// <summary>
/// What a job's historical evidence amounts to right now, decided once from one snapshot so every
/// presentation agrees: whether preparation is running, whether input evidence is qualified, the
/// readiness wording and the labels. Evidence from an earlier attempt never qualifies a running
/// preparation, and a price preview never claims strategy evidence.
/// </summary>
public sealed record ReplayEvidenceVerdict(string Label, string JobLabel, bool Preparing, string Readiness, int Issues,
    HistoricalReadinessSummary? Qualification)
{
    /// <summary>Readiness of the raw evidence, before a running preparation is taken into account.</summary>
    public static string RawReadiness(ReplayJobSnapshot snapshot) =>
        !snapshot.Job.CanReplay ? "Not ready" : snapshot.PriceOnly ? "Price preview only"
            : snapshot.Qualification?.Qualified != true ? "Incomplete — diagnostic replay only" : "Qualified historical inputs — candle model only";

    public static ReplayEvidenceVerdict From(ReplayJobSnapshot snapshot)
    {
        var job = snapshot.Job;
        var preparing = snapshot.Acquiring || job.State.IsPreparing();
        var pricePreview = snapshot.PriceOnly;
        var qualification = pricePreview || preparing ? null : snapshot.Qualification;
        var readiness = preparing ? "Acquisition in progress — evidence awaiting validation" : RawReadiness(snapshot);
        var label = job.State == ReplayJobState.MarketClosed ? "Not applicable — market closed"
            : job.State == ReplayJobState.AuthenticationRequired ? "Evidence blocked — authentication required"
            : job.State == ReplayJobState.NoData ? "Evidence unavailable"
            : pricePreview ? job.PreparationComplete ? "Prepared strategy inputs await Restart replay" : preparing ? "Strategy input preparation in progress" : "Strategy inputs unavailable"
            : qualification is null ? "Evidence awaiting validation" : qualification.Qualified ? "Evidence qualified" : "Evidence incomplete";
        var jobLabel = job.State != ReplayJobState.Completed ? job.State.ToString()
            : pricePreview ? job.PreparationComplete ? "Price preview completed — restart to apply prepared inputs" : "Price preview completed — strategy inputs unavailable"
            : qualification is null ? "Playback completed — checking evidence"
            : qualification.Qualified ? "Playback completed — input evidence qualified" : "Playback completed — evidence incomplete";
        var totals = snapshot.AttemptEvidence.Totals;
        return new(label, jobLabel, preparing, readiness, totals.Sparse + totals.Empty + totals.Failed + totals.Invalid + (qualification?.Issues.Count ?? 0), qualification);
    }
}
