using System.Text.Json;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class DirectionalReplayApiTests
{
    [Fact]
    public async Task Replay_api_exposes_only_as_of_rule_evidence_and_publishes_separate_outcome_downloads()
    {
        await using var fixture = new HistoricalApiFixture();
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete && view.Job.CanReplay);
        var signalTime = new DateTimeOffset(2026, 10, 9, 4, 1, 0, TimeSpan.Zero);
        for (var i = 0; i < 40; i++)
        {
            if ((await fixture.View(job.Id)).SimulatedTimeUtc >= signalTime) break;
            await fixture.Command(job.Id, "step");
        }
        Assert.Equal(signalTime, (await fixture.View(job.Id)).SimulatedTimeUtc);
        using var document = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}"));
        var evidence = document.RootElement.GetProperty("feature").GetProperty("directionalEvidence");
        Assert.Equal("UP", evidence.GetProperty("status").GetString());
        Assert.Equal(10, evidence.GetProperty("confirmingCount").GetInt32());
        Assert.True(evidence.GetProperty("evidenceAvailableAtUtc").GetDateTimeOffset() <= signalTime);
        Assert.False(evidence.TryGetProperty("firstTouchOutcome", out _));
        await fixture.Command(job.Id, "run");
        var finished = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        Assert.Contains("directional-outcomes.json", finished.ReportFiles);
        using var report = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{finished.Job.Run}/directional-outcomes.json"));
        Assert.Equal(4, report.RootElement.GetProperty("Rows").GetArrayLength());
        Assert.All(fixture.Handler.Requests, request => Assert.Equal("GET", request.Method));
    }
}
