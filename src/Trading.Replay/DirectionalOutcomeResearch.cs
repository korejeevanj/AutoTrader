using Trading.Analytics;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

// Evaluator-only evidence. Never attached to a replay step or supplied back to Engine.
internal sealed record DirectionalOutcome(DateOnly TradingDate, string PresetId, string IndexId,
    DateTimeOffset DecisionTimeUtc, string? SetupId, string Status, bool MeetsDirectionalRules,
    int? ConfirmingCount, DateTimeOffset LabelStartUtc, DateTimeOffset HorizonEndUtc,
    decimal? ReferencePrice, decimal? UpperBarrier, decimal? LowerBarrier,
    string FixedHorizonOutcome, decimal? HorizonReturnPercent, DateTimeOffset? FixedHorizonAvailableAtUtc,
    string FirstTouchOutcome, DateTimeOffset? TouchIntervalStartUtc, DateTimeOffset? TouchIntervalEndUtc,
    DateTimeOffset? FirstTouchAvailableAtUtc, string? PolicyExitReason, decimal? PolicyNetPnl,
    bool? PolicyExitedBeforeHorizon);

internal static class DirectionalOutcomeResearch
{
    public static IReadOnlyList<DirectionalOutcome> Evaluate(ReplayRunResult run, ReplayConfiguration configuration)
    {
        var dataset = run.CapturedDataset!;
        var settings = configuration.DirectionalResearch;
        var result = new List<DirectionalOutcome>();
        var series = dataset.Candles.GroupBy(bar => bar.InstrumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.GroupBy(bar => bar.StartUtc)
                .ToDictionary(minutes => minutes.Key, minutes => minutes.Distinct().ToArray()), StringComparer.Ordinal);
        foreach (var session in run.Sessions)
        {
            var through = session.Equity.LastOrDefault()?.TimeUtc;
            foreach (var row in session.Features.Where(row => row.IsEntryDecision))
            {
                var evidence = row.DirectionalEvidence;
                // With publication lag, discard the already-partial minute rather than use its future OHLC.
                var start = new DateTimeOffset(row.TimeUtc.UtcTicks - row.TimeUtc.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
                if (start < row.TimeUtc) start = start.AddMinutes(1);
                var end = start.AddMinutes(settings.HorizonMinutes);
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(dataset.Manifest.Indices.Single(index => index.Id == row.IndexId).TimeZone);
                var close = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(session.TradingDate.ToDateTime(dataset.Manifest.MarketClose), timezone));
                var contract = dataset.Manifest.Instruments.SingleOrDefault(item => item.Id == evidence?.FuturesInstrumentId);
                var validReference = evidence?.Close is > 0m && evidence.BarEndUtc <= row.TimeUtc
                    && evidence.EvidenceAvailableAtUtc <= row.TimeUtc && contract is { Verified: true }
                    && contract.AvailableAtUtc <= row.TimeUtc && contract.ValidFromUtc <= row.TimeUtc && row.TimeUtc < contract.ValidToUtc
                    && !evidence.Gates.Any(gate => gate.Code is "DIRECTIONAL_BAR_STALE" or "DIRECTIONAL_BARS_UNAVAILABLE");
                var upper = validReference ? evidence!.Close * (1m + settings.BarrierPercent / 100m) : null;
                var lower = validReference ? evidence!.Close * (1m - settings.BarrierPercent / 100m) : null;
                var fixedOutcome = validReference ? "PENDING" : "UNAVAILABLE";
                var touchOutcome = fixedOutcome;
                decimal? terminalReturn = null;
                DateTimeOffset? fixedAvailable = null, touchAvailable = null, touchStart = null, touchEnd = null;
                if (validReference)
                {
                    var censored = end > close || end > contract!.ValidToUtc;
                    var completePath = true;
                    HistoricalCandle? terminal = null;
                    for (var time = start; time < end; time = time.AddMinutes(1))
                    {
                        if (time >= close || time.AddMinutes(1) > contract!.ValidToUtc)
                        { completePath = false; if (touchOutcome == "PENDING") touchOutcome = "CENSORED"; break; }
                        if (through is null || time.AddMinutes(1).Add(configuration.PublicationLag) > through)
                        { completePath = false; break; }
                        var matches = series.GetValueOrDefault(contract!.Id)?.GetValueOrDefault(time) ?? [];
                        if (matches.Length != 1 || matches[0].AvailableAtUtc > through)
                        {
                            completePath = false;
                            // Distinguish an internal missing minute (later observations exist) from
                            // the end of the captured series. Partial replay still remains pending.
                            var laterObserved = series.GetValueOrDefault(contract.Id)?.Values.SelectMany(minutes => minutes)
                                .Any(observation => observation.StartUtc > time && observation.StartUtc < close
                                    && observation.AvailableAtUtc <= through) == true;
                            var truncatedTail = matches.Length == 0 && !laterObserved && through >= close.Add(configuration.PublicationLag);
                            if (truncatedTail) censored = true;
                            if (touchOutcome == "PENDING") touchOutcome = truncatedTail ? "CENSORED" : "UNRESOLVED";
                            break;
                        }
                        var bar = matches[0];
                        terminal = bar;
                        if (touchOutcome != "PENDING") continue;
                        // A gapped opening can establish the first observed touch, never an exact-barrier fill.
                        if (bar.Open >= upper) touchOutcome = "UP_FIRST";
                        else if (bar.Open <= lower) touchOutcome = "DOWN_FIRST";
                        else if (bar.High >= upper && bar.Low <= lower) touchOutcome = "AMBIGUOUS";
                        else if (bar.High >= upper) touchOutcome = "UP_FIRST";
                        else if (bar.Low <= lower) touchOutcome = "DOWN_FIRST";
                        if (touchOutcome != "PENDING")
                        {
                            touchStart = bar.StartUtc; touchEnd = bar.Open >= upper || bar.Open <= lower ? bar.StartUtc : bar.EndUtc;
                            touchAvailable = bar.AvailableAtUtc; // deliberately conservative, even for opening touches
                        }
                    }
                    if (completePath && terminal is not null)
                    {
                        terminalReturn = DirectionalAnalytics.ReturnPercent(terminal.Close, evidence!.Close);
                        fixedOutcome = terminalReturn > settings.FlatThresholdPercent ? "UP"
                            : terminalReturn < -settings.FlatThresholdPercent ? "DOWN" : "FLAT";
                        fixedAvailable = terminal.AvailableAtUtc;
                        if (touchOutcome == "PENDING") { touchOutcome = "TIMEOUT"; touchAvailable = terminal.AvailableAtUtc; }
                    }
                    else fixedOutcome = censored ? "CENSORED" : through < end.Add(configuration.PublicationLag) ? "PENDING" : "UNRESOLVED";
                }
                var setupId = evidence?.SetupId ?? row.SetupId;
                var trade = string.IsNullOrEmpty(setupId) ? null : session.Trades.SingleOrDefault(trade => trade.SetupId == setupId);
                result.Add(new(session.TradingDate, session.PresetId, row.IndexId, row.TimeUtc, setupId,
                    evidence?.Status.ToString() ?? "UNAVAILABLE", evidence?.MeetsDirectionalRules == true, evidence?.ConfirmingCount,
                    start, end, validReference ? evidence!.Close : null, upper, lower,
                    fixedOutcome, terminalReturn, fixedAvailable, touchOutcome, touchStart, touchEnd, touchAvailable,
                    trade?.ExitReason, trade?.NetPnl, trade?.ExitKnownAtUtc is { } exited ? exited < end : null));
            }
        }
        return result.AsReadOnly();
    }
}
