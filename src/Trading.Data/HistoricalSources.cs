using Trading.Domain;

namespace Trading.Data;

/// <summary>
/// What historical acquisition needs from outside the process: minute candles, the
/// exchange's dated contract file and published bank inputs. Production adapts the
/// Kite and NSE clients; tests supply an in-memory adapter.
/// </summary>
public interface IHistoricalSources
{
    Task<byte[]> CandlesAsync(HistoricalInstrument instrument, DateOnly date, TimeOnly open, TimeOnly close,
        Action<KiteRequestAttempt>? onAttempt, CancellationToken token);
    Task<HistoricalAcquisitionPlan?> ContractsAsync(DateOnly date, TimeOnly open, TimeOnly close, CancellationToken token);
    Task<HistoricalAcquisitionPlan> BankInputsAsync(HistoricalAcquisitionPlan plan, DateOnly date, int maximumSnapshotAgeDays, CancellationToken token);
}

public sealed class ProviderHistoricalSources(HistoricalAcquisitionStore store, KiteHistoricalClient kite,
    NseHistoricalContractClient exchange, PublishedBankInputsClient bankInputs) : IHistoricalSources
{
    public Task<byte[]> CandlesAsync(HistoricalInstrument instrument, DateOnly date, TimeOnly open, TimeOnly close,
        Action<KiteRequestAttempt>? onAttempt, CancellationToken token) =>
        kite.HistoricalAsync(instrument.InstrumentToken, date, open, close, token, onAttempt);
    public Task<HistoricalAcquisitionPlan?> ContractsAsync(DateOnly date, TimeOnly open, TimeOnly close, CancellationToken token) =>
        store.ResolveNseAsync(exchange, date, open, close, token);
    public Task<HistoricalAcquisitionPlan> BankInputsAsync(HistoricalAcquisitionPlan plan, DateOnly date, int maximumSnapshotAgeDays, CancellationToken token) =>
        store.EnrichBanksAsync(plan, bankInputs, date, token, maximumSnapshotAgeDays);
}
