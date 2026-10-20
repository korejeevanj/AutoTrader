using System.Diagnostics;
using System.Text.Json;
using Trading.Engine;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplayCliTests
{
    [Fact]
    public async Task Actual_cli_loads_kite_files_and_profiles_and_writes_reconciled_reports()
    {
        using var fixture = new ReplayFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "engine.json"), fixture.Engine.ToJson());
        File.WriteAllText(Path.Combine(fixture.Root, "replay.json"), (fixture.Configuration with { EngineProfile = "engine.json" }).ToJson());
        var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            $"../../../../../src/Trading.Replay.Cli/bin/{buildConfiguration}/net10.0/Trading.Replay.Cli.dll"));
        var output = Path.Combine(fixture.Root, "cli-run");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { cli, "run", "--dataset", fixture.ManifestPath, "--config", Path.Combine(fixture.Root, "replay.json"), "--out", output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "results.json")));
        var session = Assert.Single(results.RootElement.GetProperty("Sessions").EnumerateArray());
        Assert.True(session.GetProperty("Complete").GetBoolean());
        Assert.Equal(-34m, session.GetProperty("RealizedNetPnl").GetDecimal());
        Assert.Single(session.GetProperty("Trades").EnumerateArray());
        Assert.Contains("Hypothetical candle research", File.ReadAllText(Path.Combine(output, "summary.md")));
    }

    [Fact]
    public void Editable_replay_and_dataset_examples_are_valid_templates()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var replayPath = Path.Combine(root, "src/Trading.Replay/replay.example.json");
        var replay = ReplayConfiguration.FromJson(File.ReadAllText(replayPath));
        var enginePath = Path.GetFullPath(replay.EngineProfile, Path.GetDirectoryName(replayPath)!);
        Assert.Empty(EngineConfiguration.FromJson(File.ReadAllText(enginePath)).Validate());
        var manifest = JsonSerializer.Deserialize<Trading.Domain.HistoricalDatasetManifest>(
            File.ReadAllText(Path.Combine(root, "src/Trading.Replay/dataset.example.json")), Trading.Data.KiteCandleImporter.JsonOptions)!;
        Trading.Data.KiteCandleImporter.ValidateManifest(manifest);
        Assert.Equal(6, manifest.Instruments.Count);
        Assert.All(manifest.Instruments, instrument => Assert.False(instrument.Verified));
        Assert.Single(replay.FeeSchedules); Assert.Equal(0.0015m, replay.FeeSchedules[0].SellTransactionTaxRate);
        Assert.Throws<JsonException>(() => ReplayConfiguration.FromJson("{\"FeeSchedules\":[{\"Version\":\"incomplete\",\"Source\":\"test\",\"BrokeragePerOrder\":20}]}"));
    }
}
