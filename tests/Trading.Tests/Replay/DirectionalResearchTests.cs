using System.Diagnostics;
using System.Text.Json;
using Trading.Engine;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class DirectionalResearchTests
{
    [Fact]
    public async Task Actual_cli_compares_one_breadth_rule_on_identical_sessions_and_retains_both_reports()
    {
        using var fixture = new ReplayFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "engine.json"), fixture.Engine.ToJson());
        File.WriteAllText(Path.Combine(fixture.Root, "replay.json"), (fixture.Configuration with { EngineProfile = "engine.json" }).ToJson());
        var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            $"../../../../../src/Trading.Replay.Cli/bin/{buildConfiguration}/net10.0/Trading.Replay.Cli.dll"));
        var output = Path.Combine(fixture.Root, "comparison");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { cli, "compare-rules", "--dataset", fixture.ManifestPath, "--config", Path.Combine(fixture.Root, "replay.json"),
            "--minimum-confirming-constituents", "11", "--out", output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "comparison.json")));
        var pair = Assert.Single(document.RootElement.GetProperty("Sessions").EnumerateArray());
        Assert.True(pair.GetProperty("QualifiedPair").GetBoolean());
        Assert.Equal(1, pair.GetProperty("BaselineTrades").GetInt32());
        Assert.Equal(0, pair.GetProperty("VariantTrades").GetInt32());
        Assert.Equal(34m, pair.GetProperty("NetDifference").GetDecimal());
        Assert.True(File.Exists(Path.Combine(output, "baseline", "directional-outcomes.json")));
        Assert.Contains("CONSTITUENT_BREADTH", File.ReadAllText(Path.Combine(output, "variant", "decisions.csv")));
    }

    [Fact]
    public void Replay_exports_as_of_directional_evidence_and_separate_future_outcomes_for_every_entry_decision()
    {
        using var fixture = new ReplayFixture();
        var result = new ReplayRunner(fixture.Load(), fixture.Configuration, fixture.Engine).RunToEnd();
        var output = Path.Combine(fixture.Root, "research");
        ReplayReportWriter.Write(output, result);
        Assert.True(File.Exists(Path.Combine(output, "directional-evidence.csv")));
        using var outcomes = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "directional-outcomes.json")));
        var rows = outcomes.RootElement.GetProperty("Rows").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length); // 09:30, 09:31, 09:32, 09:33, including no setup
        var signal = Assert.Single(rows, row => row.GetProperty("Status").GetString() == "UP");
        Assert.Equal("FLAT", signal.GetProperty("FixedHorizonOutcome").GetString());
        Assert.Equal("DOWN_FIRST", signal.GetProperty("FirstTouchOutcome").GetString()); // 54,990 low crosses 54,992.49
        Assert.Equal(0m, signal.GetProperty("HorizonReturnPercent").GetDecimal());
        Assert.Equal(10, signal.GetProperty("ConfirmingCount").GetInt32());
        Assert.Equal("SESSION_EXIT", signal.GetProperty("PolicyExitReason").GetString());
        Assert.Equal(-34m, signal.GetProperty("PolicyNetPnl").GetDecimal());
        Assert.True(signal.GetProperty("PolicyExitedBeforeHorizon").GetBoolean());
        Assert.DoesNotContain("FixedHorizonOutcome", JsonSerializer.Serialize(result.Sessions[0].Features));
    }

    [Theory]
    [InlineData("up", "UP_FIRST")]
    [InlineData("down", "DOWN_FIRST")]
    [InlineData("both", "AMBIGUOUS")]
    [InlineData("gap", "UNRESOLVED")]
    [InlineData("quiet", "TIMEOUT")]
    public void First_touch_preserves_touch_order_ambiguity_and_missing_path(string scenario, string expected)
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var signalTime = new DateTimeOffset(2026, 10, 9, 4, 1, 0, TimeSpan.Zero); // 09:31 IST
        data = data with { Candles = data.Candles.Where(bar => !(scenario == "gap" && bar.InstrumentId == "future"
                && bar.StartUtc == signalTime))
            .Select(bar => bar.InstrumentId != "future" || bar.StartUtc < signalTime ? bar
                : bar with { High = scenario is "up" or "both" ? 55060m : 55030m,
                    Low = scenario is "down" or "both" ? 54980m : 55000m }).ToArray() };
        var result = new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd();
        var output = Path.Combine(fixture.Root, "research");
        ReplayReportWriter.Write(output, result);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "directional-outcomes.json")));
        var signal = Assert.Single(document.RootElement.GetProperty("Rows").EnumerateArray(),
            row => row.GetProperty("DecisionTimeUtc").GetDateTimeOffset() == signalTime);
        Assert.Equal(expected, signal.GetProperty("FirstTouchOutcome").GetString());
        if (scenario == "gap") Assert.Equal("UNRESOLVED", signal.GetProperty("FixedHorizonOutcome").GetString());
        if (scenario == "both") Assert.Equal(signalTime.AddMinutes(1), signal.GetProperty("FirstTouchAvailableAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public void Incomplete_replay_exports_pending_labels_without_reading_the_captured_future()
    {
        using var fixture = new ReplayFixture();
        var runner = new ReplayRunner(fixture.Load(), fixture.Configuration, fixture.Engine);
        var session = runner.CreateSession(ReplayFixture.Date, "base");
        var stop = new DateTimeOffset(2026, 10, 9, 4, 1, 0, TimeSpan.Zero);
        while (session.CurrentTimeUtc is null || session.CurrentTimeUtc < stop) session.Step();
        var output = Path.Combine(fixture.Root, "partial");
        ReplayReportWriter.Write(output, runner.CaptureResults([session.Result]));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "directional-outcomes.json")));
        var signal = Assert.Single(document.RootElement.GetProperty("Rows").EnumerateArray(),
            row => row.GetProperty("DecisionTimeUtc").GetDateTimeOffset() == stop);
        Assert.Equal("PENDING", signal.GetProperty("FirstTouchOutcome").GetString());
        Assert.Equal("PENDING", signal.GetProperty("FixedHorizonOutcome").GetString());
        Assert.Equal(JsonValueKind.Null, signal.GetProperty("HorizonReturnPercent").ValueKind);
    }

    [Fact]
    public void Publication_lag_excludes_the_partial_decision_minute_and_censors_the_short_session_tail()
    {
        using var fixture = new ReplayFixture();
        fixture.Configuration = fixture.Configuration with { PublicationLag = TimeSpan.FromSeconds(1) };
        var data = fixture.Load();
        var partialMinute = new DateTimeOffset(2026, 10, 9, 4, 1, 0, TimeSpan.Zero);
        data = data with { Candles = data.Candles.Select(bar => bar.InstrumentId == "future" && bar.StartUtc == partialMinute
            ? bar with { High = 100000m } : bar).ToArray() };
        var result = new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd();
        var output = Path.Combine(fixture.Root, "lag");
        ReplayReportWriter.Write(output, result);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "directional-outcomes.json")));
        var signal = Assert.Single(document.RootElement.GetProperty("Rows").EnumerateArray(),
            row => row.GetProperty("DecisionTimeUtc").GetDateTimeOffset() == partialMinute.AddSeconds(1));
        Assert.Equal(partialMinute.AddMinutes(1), signal.GetProperty("LabelStartUtc").GetDateTimeOffset());
        Assert.Equal("DOWN_FIRST", signal.GetProperty("FirstTouchOutcome").GetString());
        Assert.Equal("CENSORED", signal.GetProperty("FixedHorizonOutcome").GetString());
    }

    [Fact]
    public void A_touch_in_the_last_valid_minute_is_retained_when_the_horizon_ends_at_contract_expiry()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var lastMinute = new DateTimeOffset(2026, 10, 9, 4, 5, 0, TimeSpan.Zero); // 09:35
        data = data with
        {
            Manifest = data.Manifest with { Instruments = data.Manifest.Instruments.Select(item => item.Id == "future"
                ? item with { ValidToUtc = lastMinute.AddMinutes(1) } : item).ToArray() },
            Candles = data.Candles.Select(bar => bar.InstrumentId != "future" || bar.StartUtc < lastMinute.AddMinutes(-4) ? bar
                : bar.StartUtc == lastMinute ? bar with { Open = 55060m, High = 55070m, Low = 55020m }
                : bar with { Low = 55000m }).ToArray()
        };
        var result = new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd();
        var output = Path.Combine(fixture.Root, "expiry"); ReplayReportWriter.Write(output, result);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "directional-outcomes.json")));
        var signal = Assert.Single(document.RootElement.GetProperty("Rows").EnumerateArray(), row => row.GetProperty("Status").GetString() == "UP");
        Assert.Equal("UP_FIRST", signal.GetProperty("FirstTouchOutcome").GetString());
        Assert.Equal(lastMinute, signal.GetProperty("TouchIntervalStartUtc").GetDateTimeOffset());
    }

    [Fact]
    public void A_truncated_capture_tail_is_censored_instead_of_treated_as_an_internal_gap()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var cutoff = new DateTimeOffset(2026, 10, 9, 4, 3, 0, TimeSpan.Zero);
        data = data with { Candles = data.Candles.Where(bar => bar.InstrumentId != "future" || bar.StartUtc < cutoff)
            .Select(bar => bar.InstrumentId == "future" && bar.StartUtc >= cutoff.AddMinutes(-2) ? bar with { Low = 55000m } : bar).ToArray() };
        var result = new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd();
        var output = Path.Combine(fixture.Root, "tail"); ReplayReportWriter.Write(output, result);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "directional-outcomes.json")));
        var signal = Assert.Single(document.RootElement.GetProperty("Rows").EnumerateArray(), row => row.GetProperty("Status").GetString() == "UP");
        Assert.Equal("CENSORED", signal.GetProperty("FixedHorizonOutcome").GetString());
        Assert.Equal("CENSORED", signal.GetProperty("FirstTouchOutcome").GetString());
    }

    [Fact]
    public void Comparison_aggregates_alternative_execution_presets_separately_and_excludes_incomplete_pairs()
    {
        using var fixture = new ReplayFixture();
        var replay = fixture.Configuration with { Presets = [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }] };
        var data = fixture.Load();
        var complete = ReplayRuleComparison.Run(data, replay, fixture.Engine, 11);
        var output = Path.Combine(fixture.Root, "separate-presets"); ReplayReportWriter.WriteComparison(output, complete);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "comparison.json")));
        var groups = document.RootElement.GetProperty("AggregatesByPreset").EnumerateArray().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal(34m, Assert.Single(groups, row => row.GetProperty("PresetId").GetString() == "base").GetProperty("PairedNetDifference").GetDecimal());
        Assert.Equal(79m, Assert.Single(groups, row => row.GetProperty("PresetId").GetString() == "adverse").GetProperty("PairedNetDifference").GetDecimal());
        var missing = data with { Candles = data.Candles.Where(bar => bar.InstrumentId != "bank-13").ToArray() };
        var incomplete = ReplayRuleComparison.Run(missing, replay, fixture.Engine, 11);
        var incompleteOutput = Path.Combine(fixture.Root, "incomplete-pairs"); ReplayReportWriter.WriteComparison(incompleteOutput, incomplete);
        using var diagnostic = JsonDocument.Parse(File.ReadAllText(Path.Combine(incompleteOutput, "comparison.json")));
        Assert.All(diagnostic.RootElement.GetProperty("Sessions").EnumerateArray(), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("NetDifference").ValueKind));
        Assert.All(diagnostic.RootElement.GetProperty("AggregatesByPreset").EnumerateArray(), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("PairedNetDifference").ValueKind));
    }
}
