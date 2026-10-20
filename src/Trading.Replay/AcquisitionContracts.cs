using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

/// <param name="RecordedCalendarStatus">The status the job already recorded; reused when a frozen acquisition is recovered.</param>
public sealed record AcquisitionRequest(string JobId, DateOnly Date, string RecordedCalendarStatus, int PreviousAttempt,
    bool Refresh, IReadOnlySet<string>? RetryOnlyFailed);

/// <summary>Historical prices that are replayable before any dated contract file exists.</summary>
public sealed record PricePreview(HistoricalInstrument Index, HistoricalCacheEntry Cache, ReplayConfiguration Replay,
    EngineConfiguration Engine, HistoricalDataset Dataset);

public abstract record AcquisitionOutcome
{
    public sealed record MarketClosed(AcquisitionSession Session) : AcquisitionOutcome;
    public sealed record IndexUnavailable(AcquisitionSession Session, int Attempt, HistoricalInstrument Index) : AcquisitionOutcome;
    public sealed record PricePreviewOnly(AcquisitionSession Session, int Attempt) : AcquisitionOutcome;
    public sealed record Acquired(AcquisitionSession Session, int Attempt, FrozenAcquisition Frozen, HistoricalAcquisitionPlan Plan,
        string ManifestPath) : AcquisitionOutcome;
}

/// <summary>What the host reacts to while a day is being frozen and downloaded.</summary>
public interface IAcquisitionObserver
{
    Task Started(AcquisitionSession session, int attempt);
    Task PricePreviewReady(PricePreview preview, CancellationToken token);
    /// <summary>Called before the dated exchange contract file is requested; the host may archive reference data.</summary>
    Task BeforeContractLookup(CancellationToken token);
    Task Progress(HistoricalAcquisitionProgress progress, HistoricalAcquisitionPlan plan);
    void ProviderAttempt(HistoricalInstrument instrument, KiteRequestAttempt attempt);
    void CacheRepaired(HistoricalCacheEntry entry);
}
