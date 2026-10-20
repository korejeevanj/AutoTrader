using System.Runtime.CompilerServices;
using Trading.Domain;
using Trading.Replay;

namespace Trading.Api;

public sealed partial class HistoricalReplayService
{
    // Candle series are derived once per dataset, never per poll.
    private sealed record ChartSeries(IReadOnlyDictionary<string, ReplayUiBar[]> Bars, ReplayChartInstrument[] Choices);
    private static readonly ConditionalWeakTable<HistoricalDataset, ChartSeries> ChartSeriesByDataset = [];

    private static ChartSeries SeriesOf(HistoricalDataset? data)
    {
        if (data is null) return new(new Dictionary<string, ReplayUiBar[]>(), []);
        return ChartSeriesByDataset.GetValue(data, dataset =>
        {
            var bars = dataset.Candles.GroupBy(bar => bar.InstrumentId).ToDictionary(group => group.Key,
                group => group.Select(bar => new ReplayUiBar(bar.Id, bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc, bar.Open, bar.High, bar.Low, bar.Close)).ToArray(), StringComparer.Ordinal);
            return new ChartSeries(bars, dataset.Manifest.Instruments.Where(item => bars.ContainsKey(item.Id))
                .Select(item => new ReplayChartInstrument(item.Id, item.Role.ToString())).ToArray());
        });
    }

    public Task<HistoricalReplayView?> ViewAsync(string id, string? instrumentId = null)
    {
        if (jobs.Snapshot(id) is not { } snapshot) return Task.FromResult<HistoricalReplayView?>(null);
        var view = views.TryGetValue(id, out var cached) && ReferenceEquals(cached.Snapshot, snapshot) ? cached.View : BuildView(snapshot);
        views[id] = (snapshot, view);
        if (instrumentId is not null)
        {
            if (!view.Series.TryGetValue(instrumentId, out var series)) return Task.FromResult<HistoricalReplayView?>(null);
            view = view with { Bars = series.Where(bar => view.SimulatedTimeUtc is { } now && bar.AvailableAtUtc <= now && bar.EndUtc <= now).ToArray(), ChartInstrumentId = instrumentId,
                ChartLabel = instrumentId + (view.ChartInstruments?.Any(item => item.Id == instrumentId && item.Role == "CashIndex") == true ? " — index prices (no traded-volume VWAP)" : " — historical candles") };
        }
        return Task.FromResult<HistoricalReplayView?>(view);
    }

    private HistoricalReplayView BuildView(ReplayJobSnapshot snapshot)
    {
        var time = snapshot.SimulatedTimeUtc;
        var data = snapshot.Dataset;
        var series = SeriesOf(data);
        var future = snapshot.PriceOnly ? data?.Manifest.Instruments.FirstOrDefault(item => item.Role == HistoricalInstrumentRole.CashIndex)
            : data?.Manifest.Instruments.FirstOrDefault(item => item.Role == HistoricalInstrumentRole.Futures);
        future ??= data?.Manifest.Instruments.FirstOrDefault(item => item.Role == HistoricalInstrumentRole.Futures)
            ?? data?.Manifest.Instruments.FirstOrDefault(item => item.Role == HistoricalInstrumentRole.CashIndex);
        var bars = time is { } asOf && future is not null && series.Bars.TryGetValue(future.Id, out var values)
            ? values.Where(bar => bar.AvailableAtUtc <= asOf && bar.EndUtc <= asOf).ToArray() : [];
        var latest = snapshot.LastStep?.Features.LastOrDefault();
        var engine = snapshot.Engine;
        var replay = snapshot.Replay;
        var profile = engine?.Strategies.SingleOrDefault(profile => profile.IndexId == "BANKNIFTY");
        var preset = replay?.Presets[snapshot.PresetIndex];
        var directional = latest?.DirectionalEvidence;
        var directionalView = directional is null ? null : new ReplayUiDirectionalEvidence(directional.Version,
            directional.Status.ToString(), directional.AsOfUtc, directional.BarEndUtc, directional.EvidenceAvailableAtUtc,
            directional.MeetsDirectionalRules, directional.CloseMinusOpeningHigh, directional.CloseMinusOpeningLow,
            directional.CloseMinusVwap, directional.Return1MinutePercent, directional.Return5MinutePercent,
            directional.ConfirmingCount, directional.OpposingCount, directional.NeutralCount, directional.TopThreeConfirmingWeightPercent,
            profile?.MinimumConfirmingConstituents, directional.Gates.Where(gate => gate.Outcome is Trading.Engine.GateOutcome.FAIL or Trading.Engine.GateOutcome.UNKNOWN)
                .Select(gate => gate.Code).Distinct(StringComparer.Ordinal).ToArray());
        return new HistoricalReplayView(HistoricalReplayJob.From(snapshot.Job, preparingData: snapshot.Acquiring), time, snapshot.Paused, snapshot.Complete,
            bars, latest is null ? null : new(latest.OpeningHigh, latest.OpeningLow, latest.Vwap, latest.AboveWeight,
                 latest.BelowWeight, latest.MissingWeight, latest.Direction?.ToString(), latest.SetupId, directionalView),
            snapshot.Constituents,
            snapshot.Decisions.Select(decision => new ReplayUiDecision(decision.ContractId, decision.Class, decision.Action, decision.Reasons,
                decision.TimeUtc, decision.IndexId, decision.SetupId, decision.Gates, decision.Rank, decision.RankingMetrics)).ToArray(),
            snapshot.Trades.Select(trade => new ReplayUiTrade(trade.Id, trade.ContractId,
                trade.Units, trade.EntryTimeUtc, trade.EntryPrice, trade.ExitKnownAtUtc, trade.ExitPrice, trade.NetPnl, trade.ExitReason, trade.EvidenceClass,
                trade.EntryFees, trade.ExitFees, trade.GrossPnl, trade.ProfitExit)).ToArray(),
            snapshot.LastStep?.OpenPositions ?? 0, snapshot.LastStep?.PendingEntries ?? 0, snapshot.LastStep?.NetSessionPnl,
            snapshot.PriceOnly || profile is null || preset is null ? null : new(engine!.Version, preset.Id, profile.Risk.GrossCashStop,
                profile.Risk.MaximumNetRisk, profile.MaximumCandidates, preset.FullSpreadTicks, preset.AdverseSlippageTicks, profile.Position.ProfitExit),
            snapshot.ReportFiles, snapshot.PriceOnly ? "PriceOnly" : "Strategy",
            future is null ? "Historical price context" : future.Role == HistoricalInstrumentRole.CashIndex
                ? "Bank Nifty index — historical prices (no traded-volume VWAP)" : future.Id + (future.Verified ? " — historical candles" : " — historical mapping unverified"), future?.Id,
            series.Choices, snapshot.Paper, snapshot.PaperRunId, snapshot.Timing, snapshot.Revision,
            snapshot.JournalPending, snapshot.FailureNotice,
            snapshot.PriceOnly || preset is null ? null : new(preset.Id, snapshot.PresetIndex + 1, replay!.Presets.Count, snapshot.AllPresets), serverInstanceId) { Series = series.Bars };
    }
}
