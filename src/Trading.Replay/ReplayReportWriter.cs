using System.Globalization;
using System.Text;
using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public static class ReplayReportWriter
{
    public static void WriteComparison(string outputDirectory, ReplayRuleComparisonResult result)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Replay output must be a new or empty directory; existing run evidence is immutable.");
        Directory.CreateDirectory(root);
        Write(Path.Combine(root, "baseline"), result.Baseline);
        Write(Path.Combine(root, "variant"), result.Variant);
        var paired = result.Sessions.Where(session => session.QualifiedPair).ToArray();
        var header = new
        {
            Version = "breadth-rule-comparison-v1",
            EvidenceClass = result.Baseline.CapturedDataset!.Manifest.IsSynthetic
                ? "SYNTHETIC_RULE_COMPARISON_NOT_PROFITABILITY_EVIDENCE" : "CANDLE_RESEARCH_NOT_OBSERVED_EXECUTION",
            BaselineConfiguration = EngineConfiguration.FromJson(result.Baseline.EngineConfigurationJson),
            VariantConfiguration = EngineConfiguration.FromJson(result.Variant.EngineConfigurationJson),
            QualifiedPairs = paired.Length,
            AggregatesByPreset = result.Sessions.GroupBy(session => session.PresetId).Select(group => new
            {
                PresetId = group.Key, QualifiedPairs = group.Count(session => session.QualifiedPair),
                PairedNetDifference = group.Any(session => session.QualifiedPair)
                    ? group.Where(session => session.QualifiedPair).Sum(session => session.NetDifference) : null
            }).ToArray(),
            result.Sessions
        };
        File.WriteAllText(Path.Combine(root, "comparison.json"), JsonSerializer.Serialize(header, KiteCandleImporter.JsonOptions));
        var summary = new StringBuilder("# Paired rule comparison\n\nSame frozen dataset, sessions, execution presets and exit/cost policies; the variant changes only the optional confirming-constituent count and policy version.\n\n");
        summary.AppendLine($"Evidence: **{header.EvidenceClass}**. Incomplete pairs have no qualified net difference. A zero-trade result is valid; these results do not establish predictive edge.\n");
        summary.AppendLine("| Date | Preset | Qualified pair | Baseline trades | Variant trades | Baseline diagnostic net ₹ | Variant diagnostic net ₹ | Paired net difference ₹ |\n|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var pair in result.Sessions)
            summary.AppendLine($"| {pair.TradingDate:yyyy-MM-dd} | {pair.PresetId} | {pair.QualifiedPair} | {pair.BaselineTrades} | {pair.VariantTrades} | {Number(pair.BaselineDiagnosticNet)} | {Number(pair.VariantDiagnosticNet)} | {Number(pair.NetDifference)} |");
        foreach (var aggregate in header.AggregatesByPreset)
            summary.AppendLine($"\n**{aggregate.PresetId}:** {aggregate.QualifiedPairs} qualified pairs; paired net difference ₹{Number(aggregate.PairedNetDifference)}.");
        summary.AppendLine("\nAlternative execution presets are summarized separately, not added together as portfolio performance. Full evidence, losses, fees, unresolved state and underlying outcomes are in `baseline/` and `variant/`.\n\nChoose thresholds on development periods; evaluate a frozen choice on untouched sessions. No threshold selection or statistical confidence claim is performed here.");
        File.WriteAllText(Path.Combine(root, "comparison.md"), summary.ToString());
    }

    public static void Write(string outputDirectory, ReplayRunResult result, Func<ReplaySessionResult, IEnumerable<ReplayDecisionTrace>>? decisionSource = null)
    {
        var dataset = result.CapturedDataset ?? throw new ArgumentException("Use ReplayRunner.CaptureResults to bind report provenance.", nameof(result));
        var replay = ReplayConfiguration.FromJson(result.ReplayConfigurationJson);
        var engine = EngineConfiguration.FromJson(result.EngineConfigurationJson);
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Replay output must be a new or empty directory; existing run evidence is immutable.");
        Directory.CreateDirectory(root);
        void Json(string name, object value)
        {
            // Full-universe traces can be large; don't allocate a second giant JSON string.
            using var output = File.Create(Path.Combine(root, name));
            JsonSerializer.Serialize(output, value, KiteCandleImporter.JsonOptions);
        }
        Json("run-manifest.json", new
        {
            SchemaVersion = "candle-replay-run-v1", dataset.Manifest, dataset.ManifestSha256, dataset.Files,
            NormalizedDataHash = CandleHash(dataset),
            ReplayConfiguration = replay, EngineConfiguration = engine,
            ReplayConfigurationHash = KiteCandleImporter.Hash(Encoding.UTF8.GetBytes(replay.ToJson())),
            EngineConfigurationFingerprint = new TradingDecisionEngine(engine).ConfigurationFingerprint,
            CodeVersions = new[] { typeof(ReplayRunner).Assembly, typeof(TradingDecisionEngine).Assembly,
                typeof(Trading.Analytics.HistoricalSessionAnalytics).Assembly, typeof(Trading.Risk.HistoricalResearchCalculations).Assembly,
                typeof(KiteCandleImporter).Assembly, typeof(HistoricalCandle).Assembly }
                .Select(assembly => new { assembly.GetName().Name, Mvid = assembly.ManifestModule.ModuleVersionId }),
            EvidenceClass = "CANDLE_RESEARCH_NOT_OBSERVED_EXECUTION", IndependentDailyCapital = true,
            IntrabarFillTiming = "Interval only; booked when complete candle is available",
            PositiveDelayStops = "With positive execution delay, cash stops request pending exits and use a later eligible opening; no intrabar imputation",
            Fees = "Configured proportional rates are fractions; each component rounded to paise, away from zero. Not a broker reconciliation claim."
        });
        Json("quality.json", new { dataset.Issues, dataset.Files, dataset.Coverage,
            DecisionReadiness = result.Sessions.Select(session => new { session.TradingDate, session.PresetId, Qualified = session.Complete,
                session.CompletionIssues, DecisionTimes = session.Readiness?.Count ?? 0,
                IncompleteDecisionTimes = session.Readiness?.Count(status => !status.Qualified) ?? 0,
                MaximumUnknownOptions = session.Readiness?.Select(status => status.UnknownOptions).DefaultIfEmpty(0).Max() ?? 0 }) });
        Json("normalized-candles.json", dataset.Candles);
        if (decisionSource is null) Json("results.json", result);
        else WriteJournalResults(Path.Combine(root, "results.json"), result, decisionSource);
        var directionalOutcomes = DirectionalOutcomeResearch.Evaluate(result, replay);
        Json("directional-outcomes.json", new { Configuration = replay.DirectionalResearch,
            EvidenceClass = "OFFLINE_UNDERLYING_CANDLE_LABELS_NOT_OPTION_EXPECTANCY",
            WindowConvention = "Next whole-minute boundary at/after decision; reference is the as-of completed close. Partial decision minute excluded.",
            Rows = directionalOutcomes });
        using var directional = new StreamWriter(Path.Combine(root, "directional-evidence.csv"));
        using var outcomes = new StreamWriter(Path.Combine(root, "directional-outcomes.csv"));
        directional.WriteLine("session,preset,timeUtc,index,status,meetsDirectionalRules,setup,barId,barEndUtc,evidenceAvailableAtUtc,close,openingHigh,openingLow,vwap,closeMinusOpeningHigh,closeMinusOpeningLow,closeMinusVwap,return1MinutePercent,return5MinutePercent,aboveWeightPercent,belowWeightPercent,missingWeightPercent,confirmingCount,opposingCount,neutralCount,topThreeConfirmingWeightPercent,weightsVersion,reasons");
        outcomes.WriteLine("session,preset,timeUtc,index,status,setup,labelStartUtc,horizonEndUtc,referencePrice,upperBarrier,lowerBarrier,fixedHorizonOutcome,horizonReturnPercent,fixedHorizonAvailableAtUtc,firstTouchOutcome,touchIntervalStartUtc,touchIntervalEndUtc,firstTouchAvailableAtUtc,policyExitReason,policyNetPnl,policyExitedBeforeHorizon");
        foreach (var row in directionalOutcomes) outcomes.WriteLine(Csv(row.TradingDate, row.PresetId, row.DecisionTimeUtc,
            row.IndexId, row.Status, row.SetupId, row.LabelStartUtc, row.HorizonEndUtc, row.ReferencePrice, row.UpperBarrier,
            row.LowerBarrier, row.FixedHorizonOutcome, row.HorizonReturnPercent, row.FixedHorizonAvailableAtUtc,
            row.FirstTouchOutcome, row.TouchIntervalStartUtc, row.TouchIntervalEndUtc, row.FirstTouchAvailableAtUtc,
            row.PolicyExitReason, row.PolicyNetPnl, row.PolicyExitedBeforeHorizon));
        using var features = new StreamWriter(Path.Combine(root, "features.csv"));
        using var decisions = new StreamWriter(Path.Combine(root, "decisions.csv"));
        using var trades = new StreamWriter(Path.Combine(root, "trades.csv"));
        features.WriteLine("session,preset,timeUtc,index,barId,openingHigh,openingLow,vwap,longStop,shortStop,aboveWeight,belowWeight,missingWeight,setup,direction");
        decisions.WriteLine("session,preset,timeUtc,index,contract,class,action,setup,reasons,rank,spreadPercent,roundTripFriction,strikeDistance");
        trades.WriteLine("session,preset,id,index,contract,units,entryTimeUtc,entryPrice,entryFees,exitKnownAtUtc,exitIntervalStartUtc,exitIntervalEndUtc,exitPrice,exitFees,grossPnl,netPnl,exitReason,grossStopOverrun,evidenceClass,exitFeeAttribution,profitExitMode,peakNetLiquidationPnl,profitTriggeredAtUtc,profitTriggerNetPnl,profitTriggerThreshold,profitExitShortfall");
        var priceOnly = result.Sessions.All(session => session.PresetId == "price-only");
        var summary = new StringBuilder(priceOnly ? "# Historical price playback\n\n**Price inspection only. Strategy, fills and financial performance were not evaluated.**\n\n"
            : "# Historical candle replay\n\n**Hypothetical candle research; no observed bid/ask or depth.**\n\n");
        summary.AppendLine(dataset.Manifest.IsSynthetic ? "Mode: **SYNTHETIC TEST**.\n" : "Mode: **PAPER + REPLAY — CANDLE RESEARCH**.\n");
        summary.AppendLine("Independent sessions use the configured starting capital; aggregate results are not compounded. Incomplete sessions are excluded from aggregate performance.\n");
        summary.AppendLine("Completion requires qualified reference data, complete required observations and resolved option evidence at every configured entry decision time. Empty responses are unknown, not proof of no trading. Amounts from incomplete sessions are diagnostic model outcomes only.\n");
        foreach (var profile in engine.Strategies.Where(profile => profile.Enabled && dataset.Manifest.Indices.Any(index => index.Id == profile.IndexId)))
        {
            var policy = profile.Position.ProfitExit;
            summary.AppendLine($"Profit exits ({profile.IndexId}): **{policy.Mode}** / {policy.Version}. Net target ₹{Number(policy.NetTarget)}; trailing activation ₹{Number(policy.TrailActivation)}, giveback ₹{Number(policy.TrailGiveback)}, initial floor ₹{Number(policy.MinimumLockedNetProfit)}. Only the selected mode applies; triggers do not guarantee realized profit.\n");
        }
        summary.AppendLine("| Date | Preset | Complete | Trades closed/open | Realized net ₹ | Paid fees ₹ | Observed net drawdown ₹ | Issues |\n|---|---|---|---|---:|---:|---:|---|");
        foreach (var session in result.Sessions)
        {
            foreach (var row in session.Features.Where(row => row.IsEntryDecision && row.DirectionalEvidence is not null))
            {
                var value = row.DirectionalEvidence!;
                directional.WriteLine(Csv(session.TradingDate, session.PresetId, row.TimeUtc, row.IndexId, value.Status,
                    value.MeetsDirectionalRules, value.SetupId, value.BarId, value.BarEndUtc, value.EvidenceAvailableAtUtc,
                    value.Close, value.OpeningHigh, value.OpeningLow, value.Vwap, value.CloseMinusOpeningHigh, value.CloseMinusOpeningLow,
                    value.CloseMinusVwap, value.Return1MinutePercent, value.Return5MinutePercent, value.AboveWeightPercent,
                    value.BelowWeightPercent, value.MissingWeightPercent, value.ConfirmingCount, value.OpposingCount, value.NeutralCount,
                    value.TopThreeConfirmingWeightPercent, value.ConstituentDefinitionVersion,
                    string.Join(";", value.Gates.Where(gate => gate.Outcome is GateOutcome.FAIL or GateOutcome.UNKNOWN).Select(gate => gate.Code))));
            }
            summary.AppendLine($"| {session.TradingDate:yyyy-MM-dd} | {session.PresetId} | {session.Complete} | {session.Trades.Count(trade => trade.ExitPrice is not null)}/{session.Trades.Count(trade => trade.ExitPrice is null)} | {Number(priceOnly ? null : session.RealizedNetPnl)} | {Number(priceOnly ? null : session.PaidFees)} | {Number(priceOnly ? null : session.ObservedNetDrawdown)} | {string.Join(", ", session.CompletionIssues)} |");
            foreach (var row in session.Features) features.WriteLine(Csv(session.TradingDate, session.PresetId, row.TimeUtc, row.IndexId, row.BarId,
                row.OpeningHigh, row.OpeningLow, row.Vwap, row.LongStop, row.ShortStop, row.AboveWeight, row.BelowWeight, row.MissingWeight, row.SetupId, row.Direction));
            foreach (var row in decisionSource?.Invoke(session) ?? session.Decisions) decisions.WriteLine(Csv(session.TradingDate, session.PresetId, row.TimeUtc, row.IndexId,
                row.ContractId, row.Class, row.Action, row.SetupId, string.Join(";", row.Reasons), row.Rank, row.RankingMetrics?.SpreadPercent, row.RankingMetrics?.RoundTripFriction, row.RankingMetrics?.StrikeDistance));
            foreach (var row in session.Trades) trades.WriteLine(Csv(session.TradingDate, session.PresetId, row.Id, row.IndexId, row.ContractId,
                row.Units, row.EntryTimeUtc, row.EntryPrice, row.EntryFees.Total, row.ExitKnownAtUtc, row.ExitIntervalStartUtc,
                row.ExitIntervalEndUtc, row.ExitPrice, row.ExitFees?.Total, row.GrossPnl, row.NetPnl, row.ExitReason, row.GrossStopOverrun, row.EvidenceClass, row.ExitFeeAttribution,
                row.ProfitExit?.Policy.Mode, row.ProfitExit?.PeakNetLiquidationPnl, row.ProfitExit?.TriggeredAtUtc, row.ProfitExit?.TriggerNetLiquidationPnl,
                row.ProfitExit?.TriggerThreshold, row.ProfitExit?.RealizedShortfall));
        }
        foreach (var preset in result.Sessions.Where(session => session.PresetId != "price-only").Select(session => session.PresetId).Distinct(StringComparer.Ordinal))
        {
            var eligible = result.Sessions.Where(session => session.PresetId == preset && session.Complete).ToArray();
            if (eligible.Length == 0)
            {
                summary.AppendLine($"\n**{preset}:** 0 complete sessions; qualified aggregate net unavailable. Diagnostic outcomes are listed above.");
                continue;
            }
            var closed = eligible.SelectMany(session => session.Trades).Where(trade => trade.NetPnl is not null).ToArray();
            var net = Trading.Risk.HistoricalResearchCalculations.SessionNet(new(0m, 0m), closed.Select(trade => trade.NetPnl));
            summary.AppendLine($"\n**{preset}:** {eligible.Length} complete sessions, {closed.Length} closed trades, aggregate net ₹{Number(net)}. Win rate: {(closed.Length == 0 ? "unavailable" : Number(closed.Count(trade => trade.NetPnl > 0m) * 100m / closed.Length) + "%")}.");
        }
        summary.AppendLine($"\nDataset issues: {dataset.Issues.Count}. Detailed gates, fee breakdowns and unresolved state are retained in `results.json`.");
        summary.AppendLine($"\nDirectional research ({replay.DirectionalResearch.Version}): {replay.DirectionalResearch.HorizonMinutes}-minute windows, ±{replay.DirectionalResearch.BarrierPercent.ToString(CultureInfo.InvariantCulture)}% barriers and ±{replay.DirectionalResearch.FlatThresholdPercent.ToString(CultureInfo.InvariantCulture)}% flat threshold. These are configurable research assumptions, not validated trading thresholds.");
        summary.AppendLine("\n`directional-evidence.csv` records every scheduled entry decision, including no setup and missing evidence. `directional-outcomes.json/csv` contains evaluator-only later outcomes and actual policy exits separately. Labels start at the next whole minute at/after the decision; an already-partial minute is excluded. Candle double touches are ambiguous; gaps are unresolved; truncated horizons are censored. A timeout is not an option profit prediction.");
        foreach (var group in directionalOutcomes.GroupBy(row => row.FirstTouchOutcome))
            summary.AppendLine($"- First-touch {group.Key}: {group.Count()} decision rows (includes overlapping windows and execution presets; not independent observations).");
        summary.AppendLine("\nMinute candles cannot establish quote freshness, fill probability or subsecond stop behavior. Option-risk mapping and liquidity remain unverified. No real orders are submitted.");
        File.WriteAllText(Path.Combine(root, "summary.md"), summary.ToString());
    }

    private static void WriteJournalResults(string path, ReplayRunResult result, Func<ReplaySessionResult, IEnumerable<ReplayDecisionTrace>> source)
    {
        using var output = File.Create(path);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject(); writer.WriteStartArray("Sessions");
        foreach (var session in result.Sessions)
        {
            var element = JsonSerializer.SerializeToElement(session, KiteCandleImporter.JsonOptions);
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name != "Decisions") { property.WriteTo(writer); continue; }
                writer.WriteStartArray("Decisions");
                var count = 0;
                foreach (var decision in source(session))
                {
                    JsonSerializer.Serialize(writer, decision, KiteCandleImporter.JsonOptions);
                    if (++count % 128 == 0) writer.Flush();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.Flush();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("QualityIssues"); JsonSerializer.Serialize(writer, result.QualityIssues, KiteCandleImporter.JsonOptions);
        writer.WritePropertyName("CapturedDataset"); JsonSerializer.Serialize(writer, result.CapturedDataset, KiteCandleImporter.JsonOptions);
        writer.WriteString("ReplayConfigurationJson", result.ReplayConfigurationJson); writer.WriteString("EngineConfigurationJson", result.EngineConfigurationJson);
        writer.WriteEndObject();
    }
    private static string CandleHash(HistoricalDataset dataset)
    {
        using var hash = System.Security.Cryptography.SHA256.Create();
        using var stream = new System.Security.Cryptography.CryptoStream(Stream.Null, hash, System.Security.Cryptography.CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, dataset.Candles, KiteCandleImporter.JsonOptions); stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    private static string Number(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Csv(params object?[] values) => string.Join(",", values.Select(value => "\"" +
        (value switch { null => "", DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "" }).Replace("\"", "\"\"") + "\""));
}
