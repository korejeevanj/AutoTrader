using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplayJobsTests
{
    private static bool Prepared(ReplayJobSnapshot snapshot) => snapshot.Job.PreparationComplete && !snapshot.Acquiring;

    private static async Task<string> PrepareReady(ReplayJobsFixture fixture)
    {
        var job = await fixture.Jobs.PrepareAsync(ReplayFixture.Date);
        await fixture.Until(job.Job.Id, Prepared);
        return job.Job.Id;
    }

    [Fact]
    public async Task Preparing_a_completed_day_downloads_every_instrument_and_leaves_a_paused_replay_ready()
    {
        await using var fixture = new ReplayJobsFixture();

        var id = await PrepareReady(fixture); var snapshot = fixture.Jobs.Snapshot(id)!;

        Assert.True(snapshot.Job.CanReplay); Assert.True(snapshot.Paused); Assert.False(snapshot.Complete);
        Assert.Equal((18, 18, 1), (snapshot.Job.Total, snapshot.Job.Finished, snapshot.Job.Attempt));
        Assert.Equal(18, fixture.Sources.Requested.Count);
        Assert.Contains(snapshot.Job.State, new[] { ReplayJobState.Ready, ReplayJobState.ReadyWithWarnings });
    }

    [Fact]
    public async Task Preparing_the_same_day_again_returns_the_same_job()
    {
        await using var fixture = new ReplayJobsFixture(); var id = await PrepareReady(fixture);

        var again = await fixture.Jobs.PrepareAsync(ReplayFixture.Date);

        Assert.Equal(id, again.Job.Id);
        Assert.Equal(18, fixture.Sources.Requested.Count);
    }

    [Fact]
    public async Task Running_the_current_preset_plays_the_day_to_completion_and_publishes_its_reports()
    {
        await using var fixture = new ReplayJobsFixture(); var id = await PrepareReady(fixture);

        var result = await fixture.Jobs.SendAsync(id, new ReplayCommand.Run());

        Assert.IsType<ReplayCommandResult.Applied>(result);
        var done = await fixture.Until(id, snapshot => snapshot.Job.State == ReplayJobState.Completed && snapshot.Job.ReportsAvailable);
        Assert.True(done.Complete);
        Assert.NotNull(fixture.Jobs.ReportPath(id, done.Job.Run, "summary.md"));
        await fixture.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task Paced_playback_at_the_maximum_speed_also_reaches_completion()
    {
        await using var fixture = new ReplayJobsFixture(); var id = await PrepareReady(fixture);

        await fixture.Jobs.SendAsync(id, new ReplayCommand.Play { Speed = 100000 });

        await fixture.Until(id, snapshot => snapshot.Job.State == ReplayJobState.Completed && snapshot.Job.ReportsAvailable);
    }

    [Fact]
    public async Task A_step_advances_one_decision_time_and_stays_paused()
    {
        await using var fixture = new ReplayJobsFixture(); var id = await PrepareReady(fixture);

        var result = Assert.IsType<ReplayCommandResult.Applied>(await fixture.Jobs.SendAsync(id, new ReplayCommand.Step()));

        Assert.NotNull(result.Snapshot.SimulatedTimeUtc); Assert.True(result.Snapshot.Paused);
        Assert.Equal(ReplayJobState.Paused, result.Snapshot.Job.State);
    }

    [Fact]
    public async Task A_retry_that_arrives_while_a_cancelled_acquisition_is_still_stopping_resumes_it_once_the_worker_is_free()
    {
        await using var fixture = new ReplayJobsFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        fixture.Sources.BeforeCandles = async (_, _) => { if (first) { first = false; entered.SetResult(); await release.Task; } };
        var job = await fixture.Jobs.PrepareAsync(ReplayFixture.Date); var id = job.Job.Id;
        await entered.Task;

        Assert.IsType<ReplayCommandResult.Applied>(await fixture.Jobs.SendAsync(id, new ReplayCommand.Cancel()));
        var cancelled = fixture.Jobs.Snapshot(id)!;
        Assert.Equal(ReplayJobState.Cancelled, cancelled.Job.State);
        Assert.Equal(ReplayJobPhase.Acquiring | ReplayJobPhase.Stopping, cancelled.Phase); // the state says Cancelled, the worker has not let go
        Assert.False(cancelled.RetryPending);

        var retry = Assert.IsType<ReplayCommandResult.Deferred>(await fixture.Jobs.SendAsync(id, new ReplayCommand.Retry()));
        Assert.True(retry.Snapshot.RetryPending);
        Assert.Equal("Retry will resume once the current acquisition stops.", fixture.Jobs.Snapshot(id)!.Job.Message);
        release.SetResult();

        var resumed = await fixture.Until(id, Prepared);
        Assert.True(resumed.Job.CanReplay);
        Assert.False(resumed.RetryPending); Assert.Equal(ReplayJobPhase.Idle, resumed.Phase);
        // The download interrupted by the cancel was never pinned, so only that instrument is fetched again.
        var repeated = fixture.Sources.Requested.GroupBy(instrument => instrument).Where(group => group.Count() > 1).ToArray();
        Assert.Equal(18, fixture.Sources.Requested.Distinct().Count());
        Assert.Equal(1, Assert.Single(repeated).Count() - 1);
    }

    [Fact]
    public async Task Cancelling_interrupts_an_in_flight_provider_request_and_an_immediate_retry_still_prepares_the_day()
    {
        await using var fixture = new ReplayJobsFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        fixture.Sources.BeforeCandles = async (_, token) => { if (first) { first = false; entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        var job = await fixture.Jobs.PrepareAsync(ReplayFixture.Date); var id = job.Job.Id;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await fixture.Jobs.SendAsync(id, new ReplayCommand.Cancel());
        await fixture.Jobs.SendAsync(id, new ReplayCommand.Retry()); // before or after the worker stops: either way it must not be lost

        await fixture.Until(id, Prepared);
        Assert.True(fixture.Jobs.Snapshot(id)!.Job.CanReplay);
    }

    [Fact]
    public async Task Commands_that_the_job_cannot_take_yet_are_rejected_with_their_reason_without_changing_it()
    {
        await using var fixture = new ReplayJobsFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sources.BeforeCandles = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var job = await fixture.Jobs.PrepareAsync(ReplayFixture.Date); var id = job.Job.Id;
        await entered.Task;
        var before = fixture.Jobs.Snapshot(id)!.Job.State;

        Assert.Equal("Dataset is not ready for replay.", Assert.IsType<ReplayCommandResult.Rejected>(await fixture.Jobs.SendAsync(id, new ReplayCommand.Step())).Message);
        Assert.Equal("Playback speed must be positive and at most 100000.",
            Assert.IsType<ReplayCommandResult.Rejected>(await fixture.Jobs.SendAsync(id, new ReplayCommand.Pause { Speed = 0 })).Message);
        Assert.IsType<ReplayCommandResult.UnknownJob>(await fixture.Jobs.SendAsync("no-such-job", new ReplayCommand.Pause()));
        Assert.Equal(before, fixture.Jobs.Snapshot(id)!.Job.State);
        release.SetResult(); await fixture.Until(id, Prepared);
    }

    [Fact]
    public async Task Pausing_a_finished_job_whose_reports_are_published_changes_nothing()
    {
        await using var fixture = new ReplayJobsFixture(); var id = await PrepareReady(fixture);
        await fixture.Jobs.SendAsync(id, new ReplayCommand.Run());
        await fixture.Until(id, snapshot => snapshot.Job.State == ReplayJobState.Completed && snapshot.Job.ReportsAvailable);
        await fixture.Jobs.WhenIdleAsync();

        Assert.IsType<ReplayCommandResult.NoOp>(await fixture.Jobs.SendAsync(id, new ReplayCommand.Pause()));
        Assert.IsType<ReplayCommandResult.NoOp>(await fixture.Jobs.SendAsync(id, new ReplayCommand.Cancel()));
    }

    [Fact]
    public async Task After_a_restart_a_finished_job_is_listed_as_not_replayable_and_preparing_the_day_rehydrates_it()
    {
        await using var fixture = new ReplayJobsFixture(); var id = await PrepareReady(fixture);
        await fixture.Jobs.SendAsync(id, new ReplayCommand.Run());
        await fixture.Until(id, snapshot => snapshot.Job.State == ReplayJobState.Completed && snapshot.Job.ReportsAvailable);
        await fixture.Jobs.WhenIdleAsync();

        await fixture.Restart();

        var restarted = fixture.Jobs.Snapshot(id)!;
        Assert.Equal((ReplayJobState.Completed, false), (restarted.Job.State, restarted.Job.CanReplay));
        Assert.StartsWith("Server restarted.", restarted.Job.Message);
        var again = await fixture.Jobs.PrepareAsync(ReplayFixture.Date);
        Assert.Equal(id, again.Job.Id);
        var recovered = await fixture.Until(id, snapshot => snapshot.Job.CanReplay && snapshot.Job.State == ReplayJobState.Completed && snapshot.Job.ReportsAvailable);
        Assert.True(recovered.Complete);
    }

    [Fact]
    public async Task Every_published_snapshot_has_a_higher_revision_than_the_one_before_it()
    {
        await using var fixture = new ReplayJobsFixture();
        var revisions = new List<long>();
        fixture.Jobs.Changed += snapshot => { lock (revisions) revisions.Add(snapshot.Revision); };

        var id = await PrepareReady(fixture);
        await fixture.Jobs.SendAsync(id, new ReplayCommand.Step());

        lock (revisions) { Assert.True(revisions.Count > 5); Assert.Equal(revisions.Order().ToArray(), revisions.ToArray()); Assert.True(revisions.Distinct().Count() > 1); }
    }
}
