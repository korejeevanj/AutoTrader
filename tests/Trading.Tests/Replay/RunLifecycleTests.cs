using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Trading.Api;
using Trading.Data;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class RunLifecycleTests
{
    [Fact]
    public void A_failed_comparison_scope_write_restores_the_previous_scope_and_leaves_the_run_paused()
    {
        using var fixture = new ReplayFixture(); var path = Path.Combine(fixture.Root, "paper.sqlite");
        var lifecycle = new ReplayRunLifecycle(Guid.NewGuid().ToString("N"), ReplayFixture.Date, new(path), TimeProvider.System);
        lifecycle.Open(new(fixture.Load(), fixture.Configuration with { Presets = [new(), new() { Id = "adverse" }] },
            fixture.Engine, "frozen-fixture", [], []), 0);
        lifecycle.Resume(); Assert.False(lifecycle.IsPaused);
        Execute(path, "CREATE TRIGGER fail_scope BEFORE UPDATE ON paper_run_openings BEGIN SELECT RAISE(FAIL,'test scope write failure'); END;");
        Assert.Throws<SqliteException>(() => lifecycle.PreparePlayback(ReplayPlaybackRequest.AllPresets));
        Assert.False(lifecycle.AllPresets); Assert.True(lifecycle.IsPaused);
    }

    [Fact]
    public void Refetch_recovery_adopts_its_staged_new_run_instead_of_the_prior_compatible_run_or_an_extra_replacement()
    {
        using var fixture = new ReplayFixture(); var path = Path.Combine(fixture.Root, "paper.sqlite");
        var journal = new ReplayEvidenceJournal(path); var job = Guid.NewGuid().ToString("N");
        var inputs = new ReplayRunInputs(fixture.Load(), fixture.Configuration, fixture.Engine, "same-frozen-inputs", [], []);
        var first = new ReplayRunLifecycle(job, ReplayFixture.Date, journal, TimeProvider.System);
        first.Open(inputs, 0); var previous = first.Number; first.AcknowledgeCheckpoint();
        Execute(path, "CREATE TRIGGER fail_refetch_manifest BEFORE INSERT ON paper_run_manifests BEGIN SELECT RAISE(FAIL,'test refetch manifest failure'); END;");
        Assert.Throws<SqliteException>(() => first.Open(inputs, previous, new(previous, "base", false, NewRunRequired: true)));
        Assert.Equal(previous + 1, first.Number);
        Execute(path, "DROP TRIGGER fail_refetch_manifest;");
        var restored = new ReplayRunLifecycle(job, ReplayFixture.Date, journal, TimeProvider.System);
        restored.Open(inputs, previous, new(previous, "base", false, NewRunRequired: true));
        Assert.Equal(first.Number, restored.Number); Assert.Equal(first.JournalRunId, restored.JournalRunId);
        Assert.Null(restored.CurrentTimeUtc); Assert.False(restored.ManifestPending);
    }

    [Theory]
    [InlineData("refreshGaps")]
    [InlineData("retryFailed")]
    public async Task Explicit_refetch_starts_a_fresh_run_even_when_the_source_evidence_is_unchanged(string action)
    {
        await using var fixture = new HistoricalApiFixture();
        if (action == "retryFailed") fixture.Handler.Data.TryRemove(12656898, out _);
        else
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(fixture.Handler.Data[12656898])!;
            json["data"]!["candles"]!.AsArray().RemoveAt(0);
            fixture.Handler.Data[12656898] = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        await fixture.Command(job.Id, "run"); var before = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        var requests = fixture.Handler.Requests.Count;
        await fixture.Command(job.Id, action);
        var after = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && view.Job.Attempt == 2 && !view.Job.PreparingData);
        Assert.True(after.Job.Run > before.Job.Run); Assert.NotEqual(before.PaperRunId, after.PaperRunId);
        Assert.Null(after.SimulatedTimeUtc); Assert.False(after.Job.ReportsAvailable);
        Assert.Equal(requests + 1, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Opening_acknowledgement_cleanup_failure_does_not_stop_preparation_playback_or_the_worker()
    {
        await using var fixture = new HistoricalApiFixture();
        var path = Path.Combine(fixture.Root, "paper-replay.sqlite"); var journal = new ReplayEvidenceJournal(path); journal.Initialize();
        Execute(path, "CREATE TRIGGER fail_ack BEFORE DELETE ON paper_run_openings BEGIN SELECT RAISE(FAIL,'test acknowledgement cleanup failure'); END;");
        fixture.Start(); var first = await fixture.Prepare();
        await fixture.Wait(first.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        await fixture.Command(first.Id, "run"); var done = await fixture.Wait(first.Id, view => view.Job.ReportsAvailable);
        Assert.Single(done.Trades); Assert.NotEmpty(journal.PendingRunOpenings(first.Id));
        var second = await fixture.Prepare(refresh: true);
        await fixture.Wait(second.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        Execute(path, "DROP TRIGGER fail_ack;");
        await fixture.Command(second.Id, "step");
        await fixture.Command(first.Id, "setSpeed");
        Assert.Empty(journal.PendingRunOpenings(first.Id)); Assert.Empty(journal.PendingRunOpenings(second.Id));
    }

    [Fact]
    public void Run_opening_identity_and_frozen_bytes_commit_atomically_and_acknowledge_only_a_published_manifest()
    {
        using var fixture = new ReplayFixture(); var path = Path.Combine(fixture.Root, "paper.sqlite");
        var journal = new ReplayEvidenceJournal(path); journal.Initialize();
        var jobId = Guid.NewGuid().ToString("N"); var id = ReplayEvidenceRun.IdFor(jobId, 1, "base");
        const string manifest = "{\"inputHash\":\"frozen-fixture\"}";
        Execute(path, "CREATE TRIGGER fail_opening BEFORE INSERT ON paper_run_openings BEGIN SELECT RAISE(FAIL,'test opening failure'); END;");
        Assert.Throws<SqliteException>(() => journal.StageRunOpening(jobId, id, "frozen-fixture", manifest, false));
        Assert.Null(journal.Checkpoint(id)); Assert.Empty(journal.PendingRunOpenings(jobId));
        Execute(path, "DROP TRIGGER fail_opening;");
        journal.StageRunOpening(jobId, id, "frozen-fixture", manifest, false);
        Assert.Throws<InvalidDataException>(() => journal.StageRunOpening(jobId, id, "frozen-fixture", "{\"changed\":true}", true));
        Assert.Equal(manifest, Assert.Single(journal.PendingRunOpenings(jobId)).ManifestJson);
        journal.AcknowledgeRunOpening(jobId, id);
        Assert.Single(journal.PendingRunOpenings(jobId));
        journal.RecordManifest(id, jobId, manifest); journal.AcknowledgeRunOpening(jobId, id);
        Assert.Empty(journal.PendingRunOpenings(jobId));
        Assert.Equal(manifest, journal.Run(jobId, id)!.ManifestJson);
    }

    [Fact]
    public async Task Staged_restart_recovers_its_identity_and_frozen_manifest_when_the_host_checkpoint_also_failed()
    {
        var clock = new PublicationClock();
        await using var fixture = new HistoricalApiFixture { Clock = clock }; fixture.Start();
        var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        var database = Path.Combine(fixture.Root, "paper-replay.sqlite");
        var checkpoint = Path.Combine(fixture.Root, "jobs", job.Id, "job.json");
        clock.UtcNow = clock.UtcNow.AddHours(1); var openedAt = clock.UtcNow;
        Execute(database, "CREATE TRIGGER fail_manifest BEFORE INSERT ON paper_run_manifests BEGIN SELECT RAISE(FAIL,'test manifest failure'); END;");
        try
        {
            File.SetAttributes(checkpoint, FileAttributes.ReadOnly);
            using var failed = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("restart"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }
        finally { File.SetAttributes(checkpoint, FileAttributes.Normal); Execute(database, "DROP TRIGGER fail_manifest;"); }
        Assert.Equal(ready.Job.Run, HistoricalAcquisitionStore.Read<HistoricalReplayJob>(checkpoint)!.Run);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.Equal(ready.Job.Run + 1, recovered.Job.Run); Assert.Null(recovered.SimulatedTimeUtc);
        var journal = new ReplayEvidenceJournal(database);
        using var manifest = System.Text.Json.JsonDocument.Parse(journal.Run(job.Id, recovered.PaperRunId!)!.ManifestJson);
        Assert.Equal(openedAt, manifest.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Empty(journal.Read(recovered.PaperRunId!)); Assert.Empty(journal.PendingRunOpenings(job.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_reports_recover_when_the_final_job_checkpoint_was_interrupted(bool restartServer)
    {
        var clock = new PublicationClock();
        await using var fixture = new HistoricalApiFixture { Clock = clock }; fixture.Start();
        var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        var checkpoint = Path.Combine(fixture.Root, "jobs", job.Id, "job.json");
        var report = Path.Combine(fixture.Root, "jobs", job.Id, "reports", "run-" + ready.Job.Run, "complete.json");
        var interrupted = 0;
        clock.BeforeRead = () =>
        {
            if (File.Exists(report) && Interlocked.CompareExchange(ref interrupted, 1, 0) == 0)
                File.SetAttributes(checkpoint, FileAttributes.ReadOnly);
        };
        try
        {
            await fixture.Command(job.Id, "run");
            await fixture.Wait(job.Id, view => view.Job.State == "Failed" && view.Job.ReportsAvailable);
        }
        finally { clock.BeforeRead = null; File.SetAttributes(checkpoint, FileAttributes.Normal); }
        var summary = Path.Combine(Path.GetDirectoryName(report)!, "summary.md");
        var hash = KiteCandleImporter.Hash(await File.ReadAllBytesAsync(summary));
        var requests = fixture.Handler.Requests.Count;
        if (restartServer) await fixture.Restart();
        await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.Equal("Completed", recovered.Job.State); Assert.True(recovered.Job.ReportsAvailable);
        Assert.Equal(ready.PaperRunId, recovered.PaperRunId); Assert.Single(recovered.Trades);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        Assert.Equal(hash, KiteCandleImporter.Hash(await File.ReadAllBytesAsync(summary)));
    }

    private sealed class PublicationClock : TimeProvider
    {
        public Action? BeforeRead { get; set; }
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { BeforeRead?.Invoke(); return UtcNow; }
    }

    [Fact]
    public async Task First_open_manifest_failure_remains_retryable_on_the_same_prepared_run()
    {
        await using var fixture = new HistoricalApiFixture();
        var path = Path.Combine(fixture.Root, "paper-replay.sqlite");
        var journal = new ReplayEvidenceJournal(path); journal.Initialize();
        Execute(path, "CREATE TRIGGER fail_first_manifest BEFORE INSERT ON paper_run_manifests BEGIN SELECT RAISE(FAIL,'test initial manifest failure'); END;");
        fixture.Start(); var job = await fixture.Prepare();
        var pending = await fixture.Wait(job.Id, view => !view.Job.PreparingData && view.DiagnosticError?.Contains("manifest", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(pending.Job.CanReplay); Assert.True(pending.Job.PreparationComplete);
        Assert.NotNull(pending.PaperRunId); Assert.Null(pending.SimulatedTimeUtc);
        Execute(path, "DROP TRIGGER fail_first_manifest;");
        await fixture.Command(job.Id, "step");
        var recovered = await fixture.View(job.Id);
        Assert.Equal(pending.PaperRunId, recovered.PaperRunId); Assert.Equal(pending.Job.Run, recovered.Job.Run);
        Assert.NotNull(recovered.SimulatedTimeUtc); Assert.True(journal.HasRun(job.Id, recovered.PaperRunId!));
    }

    [Fact]
    public async Task Implicit_restart_invalidates_previous_reports_and_publication_can_retry_without_replaying()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        await fixture.Command(job.Id, "run"); var published = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        await fixture.Command(job.Id, "step"); var next = await fixture.View(job.Id);
        Assert.Equal(published.Job.Run + 1, next.Job.Run); Assert.False(next.Job.ReportsAvailable); Assert.Empty(next.ReportFiles);
        var output = Path.Combine(fixture.Root, "jobs", job.Id, "reports", "run-" + next.Job.Run);
        await File.WriteAllTextAsync(output, "Test-owned obstruction to this run's report publication.");
        await fixture.Command(job.Id, "run");
        var failed = await fixture.Wait(job.Id, view => view.Job.State == "Failed");
        Assert.True(failed.Complete); Assert.False(failed.Job.ReportsAvailable);
        File.Delete(output); await fixture.Command(job.Id, "publish");
        var recovered = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        Assert.Equal(next.PaperRunId, recovered.PaperRunId); Assert.Single(recovered.Trades);
    }

    [Fact]
    public async Task A_failed_next_preset_manifest_preserves_the_comparison_and_retries_before_any_new_decision()
    {
        await using var fixture = new HistoricalApiFixture();
        var replay = ReplayConfiguration.FromJson(await File.ReadAllTextAsync(fixture.ReplayPath)) with
            { Presets = [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }] };
        await HistoricalAcquisitionStore.AtomicJsonAsync(fixture.ReplayPath, replay, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        var path = Path.Combine(fixture.Root, "paper-replay.sqlite");
        Execute(path, "CREATE TRIGGER fail_next_manifest BEFORE INSERT ON paper_run_manifests WHEN json_extract(NEW.manifest_json,'$.preset.Id')='adverse' BEGIN SELECT RAISE(FAIL,'test preset manifest failure'); END;");
        await fixture.Command(job.Id, "runAllPresets");
        var failed = await fixture.Wait(job.Id, view => view.Job.State == "Failed");
        Assert.Equal(2, failed.PresetProgress!.Number); Assert.True(failed.PresetProgress.AllPresets);
        Assert.Contains("manifest", failed.DiagnosticError, StringComparison.OrdinalIgnoreCase);
        var id = ReplayEvidenceRun.IdFor(job.Id, ready.Job.Run, "adverse");
        var journal = new ReplayEvidenceJournal(path);
        Assert.Null(journal.Checkpoint(id)!.TimeUtc);
        using var blocked = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("step"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Null(journal.Checkpoint(id)!.TimeUtc);
        Execute(path, "DROP TRIGGER fail_next_manifest;");
        await fixture.Command(job.Id, "play");
        var complete = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        Assert.Equal(ready.Job.Run, complete.Job.Run);
        using var results = System.Text.Json.JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{complete.Job.Run}/results.json"));
        Assert.Equal(2, results.RootElement.GetProperty("Sessions").GetArrayLength());
        Assert.True(journal.HasRun(job.Id, id));
    }

    [Fact]
    public void Manual_preset_completion_keeps_one_run_and_prevents_early_comparison_publication()
    {
        using var fixture = new ReplayFixture();
        var replay = fixture.Configuration with { Presets = [new(), new() { Id = "adverse" }] };
        var journal = new ReplayEvidenceJournal(Path.Combine(fixture.Root, "paper.sqlite"));
        var jobId = Guid.NewGuid().ToString("N");
        var lifecycle = new ReplayRunLifecycle(jobId, ReplayFixture.Date, journal, TimeProvider.System);
        lifecycle.Open(new(fixture.Load(), replay, fixture.Engine, "frozen-fixture", [], []), 0);
        var number = lifecycle.Number;
        lifecycle.PreparePlayback(ReplayPlaybackRequest.AllPresets);
        while (!lifecycle.IsComplete) lifecycle.Step();
        Assert.False(lifecycle.CanPublish);
        Assert.Throws<InvalidOperationException>(() => lifecycle.CaptureResults());
        Assert.True(lifecycle.CompletePreset());
        Assert.Equal(number, lifecycle.Number); Assert.Equal(1, lifecycle.PresetIndex); Assert.True(lifecycle.IsPaused);
        Assert.True(journal.HasRun(jobId, lifecycle.JournalRunId!));
        while (!lifecycle.IsComplete) lifecycle.Step();
        Assert.False(lifecycle.CompletePreset());
        Assert.True(lifecycle.CanPublish);
        Assert.Equal(new[] { "base", "adverse" }, lifecycle.CaptureResults().Sessions.Select(result => result.PresetId));
        Assert.Equal(2, lifecycle.CaptureResults().Sessions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_cannot_advance_a_new_run_until_its_manifest_is_durable(bool restartServer)
    {
        var clock = new PublicationClock();
        await using var fixture = new HistoricalApiFixture { Clock = clock }; fixture.Start();
        var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);
        var path = Path.Combine(fixture.Root, "paper-replay.sqlite");
        clock.UtcNow = clock.UtcNow.AddHours(1); var openedAt = clock.UtcNow;
        Execute(path, "CREATE TRIGGER fail_manifest BEFORE INSERT ON paper_run_manifests BEGIN SELECT RAISE(FAIL,'test manifest storage failure'); END;");
        using var restart = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("restart"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, restart.StatusCode);
        var run = ReplayEvidenceRun.IdFor(job.Id, ready.Job.Run + 1, "base");
        var journal = new ReplayEvidenceJournal(path);
        Assert.Null(journal.Checkpoint(run)!.TimeUtc);
        using var blocked = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("step"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Null(journal.Checkpoint(run)!.TimeUtc);
        Assert.Empty(journal.Read(run));

        Execute(path, "DROP TRIGGER fail_manifest;");
        if (restartServer)
        {
            clock.UtcNow = clock.UtcNow.AddHours(1);
            await fixture.Restart(); await fixture.Command(job.Id, "retry");
            await fixture.Wait(job.Id, view => view.Job.CanReplay);
        }
        await fixture.Command(job.Id, "step");
        var recovered = await fixture.View(job.Id);
        Assert.Equal(run, recovered.PaperRunId);
        Assert.NotNull(recovered.SimulatedTimeUtc);
        Assert.True(journal.HasRun(job.Id, run));
        Assert.Equal(recovered.SimulatedTimeUtc, journal.Checkpoint(run)!.TimeUtc);
        using var manifest = System.Text.Json.JsonDocument.Parse(journal.Run(job.Id, run)!.ManifestJson);
        Assert.Equal(openedAt, manifest.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
