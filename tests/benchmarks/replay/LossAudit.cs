using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;
using Trading.Replay;

internal static class LossAudit
{
    public static void Run(string[] args)
    {
        string Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing {name}.");
        }
        var database = Path.GetFullPath(Option("--database"));
        var runId = Option("--run");
        var datasetPath = Path.GetFullPath(Option("--dataset"));
        var output = Path.GetFullPath(Option("--out"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Audit output must be new or empty; original run evidence is immutable.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json FROM paper_run_manifests WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", runId);
        using var manifest = JsonDocument.Parse((string?)command.ExecuteScalar() ?? throw new ArgumentException("Run manifest not found."));
        command.CommandText = "SELECT snapshot_json FROM paper_runs WHERE run_id=$run";
        using var checkpoint = JsonDocument.Parse((string)command.ExecuteScalar()!);
        var frozen = manifest.RootElement;
        var engine = EngineConfiguration.FromJson(frozen.GetProperty("engineConfiguration").GetRawText());
        var originalReplay = ReplayConfiguration.FromJson(frozen.GetProperty("replayConfiguration").GetRawText());
        var date = DateOnly.Parse(frozen.GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var presetId = frozen.GetProperty("preset").GetProperty("Id").GetString()!;
        var replay = originalReplay with { FromDate = date, ToDate = date,
            Presets = [originalReplay.Presets.Single(preset => preset.Id == presetId)] };
        var dataset = KiteCandleImporter.Load(datasetPath, replay.PublicationLag);
        var originalTrades = JsonSerializer.Deserialize<ResearchTrade[]>(checkpoint.RootElement.GetProperty("Trades"), KiteCandleImporter.JsonOptions)!;
        var originalNet = checkpoint.RootElement.GetProperty("Paper").GetProperty("RealizedNetPnl").GetDecimal();
        var profile = engine.Strategies.Single(profile => profile.IndexId == "BANKNIFTY");
        EngineConfiguration Change(string name, IndexStrategyConfiguration changed) => engine with
        {
            Version = $"{engine.Version}:loss-audit:{name}",
            Strategies = engine.Strategies.Select(item => item.IndexId == changed.IndexId ? changed : item).ToArray()
        };
        // Existing published policy alternatives, frozen before evaluation; no parameter search.
        var variants = new (string Name, EngineConfiguration Engine)[]
        {
            ("frozen-baseline", engine),
            ("fixed-net-500", Change("fixed", profile with { Position = profile.Position with
                { ProfitExit = profile.Position.ProfitExit with { Mode = ProfitExitMode.FixedNetTarget } } })),
            ("trail-existing-500-200", Change("trail", profile with { Position = profile.Position with
                { ProfitExit = profile.Position.ProfitExit with { Mode = ProfitExitMode.TrailingNetProfit } } })),
            ("breadth-count-3", Change("breadth", profile with { MinimumConfirmingConstituents = 3 })),
            ("strike-distance-first", Change("ranking", profile with
                { Ranking = [RankingCriterion.StrikeDistance, RankingCriterion.RoundTripFriction, RankingCriterion.SpreadPercentage, RankingCriterion.ObservedDepth] }))
        };
        Directory.CreateDirectory(output);
        var summaries = new List<object>();
        foreach (var variant in variants)
        {
            var elapsed = Stopwatch.StartNew();
            var runner = new ReplayRunner(dataset, replay, variant.Engine);
            var session = runner.CreateSession(date, presetId);
            var signals = new List<object>();
            var marks = new List<object>();
            var selections = new List<object>();
            while (session.Step() is { } step)
            {
                foreach (var feature in step.Features.Where(feature => feature.IsEntryDecision && feature.DirectionalEvidence?.Direction is not null))
                {
                    var evidence = feature.DirectionalEvidence!;
                    signals.Add(new { step.TimeUtc, evidence.SetupId, evidence.Status, evidence.Close, evidence.OpeningHigh, evidence.OpeningLow,
                        evidence.Vwap, evidence.AboveWeightPercent, evidence.BelowWeightPercent, evidence.MissingWeightPercent,
                        evidence.ConfirmingCount, evidence.OpposingCount, evidence.MeetsDirectionalRules });
                }
                foreach (var decision in step.Decisions.Where(decision => decision.Class == "CANDLE_POSITION_POLICY"))
                    marks.Add(new { step.TimeUtc, decision.ContractId, decision.Action, decision.Reasons,
                        Values = decision.Gates.Where(gate => gate.Code is "NET_LIQUIDATION" or "PEAK_NET_LIQUIDATION" or "NET_PROFIT_TRAIL_THRESHOLD")
                            .Select(gate => new { gate.Code, gate.Observed, gate.Threshold }).ToArray() });
                foreach (var decision in step.Decisions.Where(decision => decision.Class == "CANDLE_RESEARCH" && decision.Rank is > 0 and <= 5))
                    selections.Add(new { step.TimeUtc, decision.ContractId, decision.Rank, decision.RankingMetrics,
                        ModelledRisk = decision.Gates.SingleOrDefault(gate => gate.Code == "RESEARCH_MODELLED_RISK")?.Observed });
                // Release full-universe diagnostics after inspection; do not retain hundreds of thousands of candidate gates.
                session.ReleaseCommittedDecisionHistory();
            }
            var result = session.Result;
            if (variant.Name == "frozen-baseline")
            {
                if (result.RealizedNetPnl != originalNet || result.Trades.Count != originalTrades.Length
                    || result.Trades.Zip(originalTrades).Any(pair => pair.First.ContractId != pair.Second.ContractId
                        || pair.First.SetupId != pair.Second.SetupId || pair.First.Units != pair.Second.Units
                        || pair.First.EntryTimeUtc != pair.Second.EntryTimeUtc || pair.First.EntryPrice != pair.Second.EntryPrice
                        || pair.First.ExitKnownAtUtc != pair.Second.ExitKnownAtUtc || pair.First.ExitPrice != pair.Second.ExitPrice
                        || pair.First.NetPnl != pair.Second.NetPnl))
                    throw new InvalidDataException("Frozen baseline does not reproduce the original checkpoint; do not attribute variant differences.");
            }
            var summary = new
            {
                Name = variant.Name, result.RealizedNetPnl, result.PaidFees, result.Complete, result.CompletionIssues,
                result.PendingEntries, result.PendingExits, Trades = result.Trades,
                EngineConfiguration = variant.Engine, EngineFingerprint = new TradingDecisionEngine(variant.Engine).ConfigurationFingerprint,
                Signals = signals, PositionMarks = marks, TopCandidates = selections,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds
            };
            File.WriteAllText(Path.Combine(output, variant.Name + ".json"), JsonSerializer.Serialize(summary, KiteCandleImporter.JsonOptions));
            var compact = new { variant.Name, result.RealizedNetPnl, result.PaidFees, result.Complete,
                Trades = result.Trades.Select(trade => new { trade.ContractId, trade.EntryTimeUtc, trade.ExitKnownAtUtc,
                    trade.EntryPrice, trade.ExitPrice, trade.NetPnl, trade.ExitReason, trade.ProfitExit }).ToArray() };
            summaries.Add(compact);
            Console.WriteLine(JsonSerializer.Serialize(compact, KiteCandleImporter.JsonOptions));
        }
        var audit = new { Version = "replay-loss-audit-v1", EvidenceClass = "POST_HOC_SINGLE_DAY_DIAGNOSTICS_NOT_POLICY_VALIDATION",
            OriginalRunId = runId, OriginalNet = originalNet, FrozenBaselineReproduced = true,
            DatasetManifestHash = dataset.ManifestSha256, FrozenInputHash = frozen.GetProperty("inputHash").GetString(),
            FrozenCodeVersions = frozen.GetProperty("codeVersions").Clone(),
            CurrentCodeVersions = new[] { typeof(ReplayRunner).Assembly, typeof(TradingDecisionEngine).Assembly,
                typeof(Trading.Risk.HistoricalResearchCalculations).Assembly, typeof(Trading.Analytics.HistoricalSessionAnalytics).Assembly }
                .Select(assembly => new { assembly.GetName().Name, Mvid = assembly.ManifestModule.ModuleVersionId }).ToArray(),
            ReplayConfiguration = replay, Summaries = summaries };
        File.WriteAllText(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(audit, KiteCandleImporter.JsonOptions));
    }
}
