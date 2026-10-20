using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Trading.Api;
using Trading.Data;
using Trading.Domain;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class AttemptEvidenceTests
{
    [Fact]
    public async Task Normalized_validity_windows_drive_intervals_and_refetch_while_obsolete_attempts_are_rejected()
    {
        using var fixture = new ReplayFixture();
        var boundary = new DateTimeOffset(2026, 10, 9, 3, 50, 0, TimeSpan.Zero);
        var manifest = fixture.ReadManifest();
        manifest = manifest with { Instruments = manifest.Instruments.Select(item => item.Id is "call" or "put" ? item with { ValidFromUtc = boundary } : item).ToArray() };
        fixture.WriteManifest(manifest);
        foreach (var id in new[] { "call", "put" })
        {
            var path = Path.Combine(fixture.Root, id + ".json");
            var document = JsonNode.Parse(await File.ReadAllBytesAsync(path))!; var rows = document["data"]!["candles"]!.AsArray();
            for (var i = 0; i < 5; i++) rows.RemoveAt(0);
            if (id == "call") rows.RemoveAt(5); // 09:25 IST
            await File.WriteAllTextAsync(path, document.ToJsonString());
        }
        var dataset = fixture.Load();
        var progress = manifest.Instruments.Select(item => new HistoricalInstrumentAcquisition(item.Id, item.InstrumentToken, item.Role.ToString(),
            item.Id is "call" or "put" ? "Partial" : "Complete", dataset.Files.Single(file => file.InstrumentId == item.Id).AcceptedRows, true, null)).ToArray();
        var evidence = new AcquisitionAttemptEvidence(2, ReplayFixture.Date).WithProgress(2, progress).WithDataset(2, dataset);
        var call = Assert.Single(evidence.Instruments, row => row.Id == "call");
        Assert.Equal(16, call.ExpectedMinutes); Assert.Equal(1, call.MissingMinutes); Assert.Equal("Sparse", call.State);
        var gap = Assert.Single(call.MissingIntervals);
        Assert.Equal(boundary.AddMinutes(5), gap.StartUtc); Assert.Equal(boundary.AddMinutes(6), gap.EndUtc);
        Assert.Equal("Complete", Assert.Single(evidence.Instruments, row => row.Id == "put").State);
        Assert.Equal(new[] { "call" }, evidence.RefetchTargets(false));
        Assert.Empty(evidence.RefetchTargets(true));
        Assert.Throws<InvalidOperationException>(() => evidence.WithDataset(1, dataset));
        Assert.Throws<InvalidOperationException>(() => evidence.WithProgress(3, progress));
        Assert.True(evidence.Validated); Assert.Equal(2, evidence.Number);
    }

    [Fact]
    public async Task An_all_empty_refresh_publishes_current_unavailable_coverage_instead_of_the_retained_dataset()
    {
        await using var fixture = new HistoricalApiFixture();
        foreach (var pair in fixture.Handler.Data) fixture.Handler.Data[pair.Key] = Response(pair.Value, "sparse");
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        foreach (var pair in fixture.Handler.Data) fixture.Handler.Data[pair.Key] = Response(pair.Value, "empty");
        await fixture.Command(job.Id, "refreshGaps");
        await fixture.Wait(job.Id, view => view.Job.State == "NoData" && view.Job.Attempt == 2 && !view.Job.PreparingData);
        var health = (await fixture.Client.GetFromJsonAsync<ReplayDataHealth>($"/api/replay/jobs/{job.Id}/data-health"))!;
        Assert.Equal(2, health.Attempt); Assert.Equal(18, health.Empty); Assert.Equal(0, health.Sparse);
        Assert.All(health.Instruments, row => { Assert.Equal("Unavailable", row.State); Assert.Equal(21, row.MissingMinutes); });
        Assert.False(health.Qualification!.Qualified);
    }

    [Theory]
    [InlineData("empty", "complete", "AwaitingValidation", "Complete")]
    [InlineData("sparse", "empty", "Unavailable", "Unavailable")]
    [InlineData("invalid", "complete", "AwaitingValidation", "Complete")]
    public async Task Refresh_health_never_attributes_previous_coverage_to_new_response_progress(string before, string after,
        string intermediateState, string finalState)
    {
        await using var fixture = new HistoricalApiFixture();
        var full = fixture.Handler.Data[12656898];
        fixture.Handler.Data[12656898] = Response(full, before);
        fixture.Handler.Data[12657154] = Response(full, "empty");
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        fixture.Handler.Data[12656898] = Response(full, after);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Override = async (request, token) =>
        {
            var id = long.Parse(request.RequestUri!.AbsolutePath.Split('/')[3], System.Globalization.CultureInfo.InvariantCulture);
            if (id == 12657154) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Handler.Data[id]) };
        };
        try
        {
            await fixture.Command(job.Id, "refreshGaps");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var health = (await fixture.Client.GetFromJsonAsync<ReplayDataHealth>($"/api/replay/jobs/{job.Id}/data-health"))!;
            Assert.Equal(2, health.Attempt);
            var row = Assert.Single(health.Instruments, item => item.Id == "call");
            Assert.Equal(after == "empty" ? 0 : 21, row.Rows);
            Assert.Equal(intermediateState, row.State);
            Assert.Null(row.ExpectedMinutes); Assert.Null(row.MissingMinutes);
            Assert.Empty(row.Issues); Assert.Empty(row.MissingIntervals); Assert.Null(health.Qualification);
        }
        finally { release.TrySetResult(); }
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete && view.Job.Attempt == 2 && !view.Job.PreparingData);
        var final = (await fixture.Client.GetFromJsonAsync<ReplayDataHealth>($"/api/replay/jobs/{job.Id}/data-health"))!;
        Assert.Equal(finalState, Assert.Single(final.Instruments, item => item.Id == "call").State);
    }

    private static byte[] Response(byte[] full, string state)
    {
        var document = JsonNode.Parse(full)!;
        var rows = document["data"]!["candles"]!.AsArray();
        switch (state)
        {
            case "empty": rows.Clear(); break;
            case "sparse": rows.RemoveAt(0); break;
            case "invalid": var conflicting = rows[0]!.DeepClone().AsArray(); conflicting[4] = 201m; rows.Add(conflicting); break;
        }
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }
}
