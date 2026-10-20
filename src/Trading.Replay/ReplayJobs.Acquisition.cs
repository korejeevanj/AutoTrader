using Trading.Data;
using Trading.Domain;

namespace Trading.Replay;

public sealed partial class ReplayJobs
{
    /// <summary>Records the acquisition module's progress on the job; the module owns what is acquired.</summary>
    private sealed class AcquisitionProgress(ReplayJobs host, RuntimeJob runtime) : IAcquisitionObserver
    {
        private int attempt;

        public async Task Started(AcquisitionSession session, int number)
        {
            attempt = number; var day = runtime.Job.Date;
            host.Log(runtime, "ACQUISITION_STARTED", new { attempt }, acquisition: true);
            if (runtime.DiagnosticError is not null) throw new IOException("Operational log storage is unavailable.");
            await host.Change(runtime, job =>
            {
                runtime.AttemptEvidence = new(attempt, day);
                return job with { State = ReplayJobState.Resolving, CalendarStatus = session.Status, Message = "Resolving historical identities and all option expiries/strikes.",
                    Attempt = attempt, CanReplay = false, ReportsAvailable = false, PreparationComplete = false, Instruments = [], Total = 0, Finished = 0 };
            });
        }

        public async Task PricePreviewReady(PricePreview preview, CancellationToken token)
        {
            var index = preview.Index; var cache = preview.Cache;
            await runtime.Gate.WaitAsync(token);
            try
            {
                runtime.AttemptEvidence = runtime.AttemptEvidence.WithProgress(runtime.Job.Attempt,
                    [new(index.Id, index.InstrumentToken, "CashIndex", cache.Complete ? "Complete" : "Partial", cache.Rows, true, null)])
                    .WithDataset(runtime.Job.Attempt, preview.Dataset);
                runtime.Dataset = preview.Dataset; runtime.Replay = preview.Replay; runtime.Engine = preview.Engine;
                await host.ResetSession(runtime, recoverCurrent: true);
                runtime.Job = runtime.Job with { State = ReplayJobState.ReadyWithWarnings, CanReplay = true, Mode = "PAPER + REPLAY — PRICE ONLY",
                    Message = "Price playback is ready. Looking up this day's actual NSE contract file in the background; no live-contract substitution.",
                    Warnings = ["HISTORICAL_CATALOGUE_UNAVAILABLE", "PRICE_ONLY_NO_STRATEGY_TRADES"], Total = 1, Finished = 1,
                    Instruments = [new(index.Id, index.InstrumentToken, "CashIndex", cache.Complete ? "Complete" : "Partial", cache.Rows, true, null)] };
                await host.Save(runtime);
            }
            finally { runtime.Gate.Release(); }
        }

        public Task BeforeContractLookup(CancellationToken token) => host.ArchiveCurrent(token, ignoreFailure: true);

        public async Task Progress(HistoricalAcquisitionProgress progress, HistoricalAcquisitionPlan plan)
        {
            if (progress.Validating)
            {
                await host.Change(runtime, job => job with { State = runtime.PriceOnly ? job.State : ReplayJobState.Validating, Message = "Validating candle coverage and freezing replay inputs." });
                return;
            }
            var instruments = progress.Instruments.Select(row => new ReplayInstrumentProgress(row.Id, row.Token,
                row.Role, row.State, row.Rows, row.Cached, row.Error)).ToArray();
            if (progress.Finished == 0)
                await host.Change(runtime, job =>
                {
                    runtime.AttemptEvidence = runtime.AttemptEvidence.WithProgress(attempt, progress.Instruments);
                    return job with { State = runtime.PriceOnly ? job.State : ReplayJobState.Downloading, CatalogSource = plan.Source,
                        Warnings = plan.Warnings, Instruments = instruments, Total = instruments.Length, Finished = 0,
                        Message = "Downloading essential index/futures/bank inputs first, then the full date-valid option catalogue." };
                });
            else
            {
                var item = instruments[progress.Finished - 1];
                host.Log(runtime, "INSTRUMENT_" + item.State.ToUpperInvariant(), new { attempt, item.Id, item.Role, item.Rows, item.Error, item.Cached },
                    item.State == "Failed" ? "Error" : item.State is "Unavailable" or "Partial" ? "Warning" : "Information", acquisition: true);
                if (runtime.DiagnosticError is not null) throw new IOException("Operational log storage is unavailable.");
                await host.Change(runtime, job =>
                {
                    runtime.AttemptEvidence = runtime.AttemptEvidence.WithProgress(attempt, progress.Instruments);
                    return job with { Instruments = Array.AsReadOnly(instruments), Finished = progress.Finished,
                        Message = $"Prepared {progress.Finished} of {instruments.Length} instruments; failures remain explicit." };
                });
            }
        }

        public void ProviderAttempt(HistoricalInstrument instrument, KiteRequestAttempt request) =>
            host.Log(runtime, "PROVIDER_REQUEST_ATTEMPT", new { instrument.Id, acquisitionAttempt = runtime.Job.Attempt, request.Attempt, request.StatusCode, request.ElapsedMilliseconds },
                request.StatusCode is >= 200 and < 300 ? "Information" : "Warning", acquisition: true);

        public void CacheRepaired(HistoricalCacheEntry entry) =>
            host.Log(runtime, "CACHE_SOURCE_REPAIRED", new { Id = entry.InstrumentId, entry.Sha256 }, "Warning", acquisition: true);
    }

    /// <summary>Records an acquisition that stopped before producing frozen, downloaded inputs.</summary>
    private Task Conclude(RuntimeJob runtime, AcquisitionOutcome outcome) => outcome switch
    {
        AcquisitionOutcome.MarketClosed closed => Change(runtime, job => job with { State = ReplayJobState.MarketClosed, CalendarStatus = closed.Session.Status,
            Message = closed.Session.Message, CanReplay = false }),
        AcquisitionOutcome.IndexUnavailable unavailable => Change(runtime, job =>
        {
            var index = unavailable.Index;
            ReplayInstrumentProgress[] row = [new(index.Id, index.InstrumentToken, "CashIndex", "Unavailable", 0, false, "EMPTY_RESPONSE")];
            runtime.AttemptEvidence = runtime.AttemptEvidence.WithProgress(unavailable.Attempt,
                [new(index.Id, index.InstrumentToken, "CashIndex", "Unavailable", 0, false, "EMPTY_RESPONSE")]);
            return job with { State = ReplayJobState.NoData, Message = "No selected-day index candles returned; not proof of a holiday.", CanReplay = false,
                Total = 1, Finished = 1, Instruments = row };
        }),
        AcquisitionOutcome.PricePreviewOnly => Change(runtime, job => job with { Message = "Historical prices are replayable. No dated NSE contract file was available; strategy and option coverage remain unavailable." }),
        _ => throw new InvalidOperationException("Acquired outcomes continue into validation.")
    };
}
