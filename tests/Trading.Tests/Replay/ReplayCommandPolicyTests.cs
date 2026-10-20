using Trading.Engine;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplayCommandPolicyTests
{
    private static ReplayJobFacts Ready() => new() { State = ReplayJobState.Ready, CanReplay = true, HasSession = true, CanPublish = true,
        HasPaperState = true, HasJournalRun = true };

    private static ReplayCommandDecision Decide(ReplayJobFacts facts, ReplayCommand command) => ReplayCommandPolicy.Decide(facts, command);
    private static void Rejected(string message, ReplayCommandDecision decision) =>
        Assert.Equal(message, Assert.IsType<ReplayCommandDecision.Rejected>(decision).Message);

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(0)] [InlineData(-1)] [InlineData(100001)]
    public void A_speed_outside_zero_to_one_hundred_thousand_is_rejected_for_every_command(double speed) =>
        Rejected("Playback speed must be positive and at most 100000.", Decide(Ready(), new ReplayCommand.Pause { Speed = speed }));

    [Fact]
    public void A_speed_at_the_upper_bound_is_valid() => Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready(), new ReplayCommand.Step { Speed = 100000 }));

    [Fact]
    public void An_unavailable_operational_log_blocks_everything_except_pause_and_cancel()
    {
        var facts = Ready() with { LogUnavailable = true };

        Assert.IsType<ReplayCommandDecision.StorageUnavailable>(Decide(facts, new ReplayCommand.Step()));
        Assert.IsType<ReplayCommandDecision.StorageUnavailable>(Decide(facts, new ReplayCommand.Retry()));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(facts, new ReplayCommand.Pause()));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(facts, new ReplayCommand.Cancel()));
        Assert.IsType<ReplayCommandDecision.NoOp>(Decide(facts, new ReplayCommand.CancelPreparation())); // idle job: not blocked, nothing to cancel
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(facts with { State = ReplayJobState.Downloading }, new ReplayCommand.CancelPreparation()));
    }

    [Fact]
    public void Publishing_blocks_everything_except_cancel_and_the_log_check_comes_first()
    {
        var facts = Ready() with { Publishing = true, State = ReplayJobState.Reporting };

        Rejected("Wait for report publication before changing replay state.", Decide(facts, new ReplayCommand.Pause()));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(facts, new ReplayCommand.Cancel()));
        Assert.IsType<ReplayCommandDecision.StorageUnavailable>(Decide(facts with { LogUnavailable = true }, new ReplayCommand.Restart()));
    }

    [Fact]
    public void A_pending_journal_commit_allows_only_stepping_playing_pausing_and_cancelling()
    {
        var facts = Ready() with { JournalPending = true };
        const string message = "A paper batch is awaiting durable commit. Retry Step before changing inputs or paper controls.";

        foreach (ReplayCommand allowed in new ReplayCommand[] { new ReplayCommand.Step(), new ReplayCommand.Play(), new ReplayCommand.Run(), new ReplayCommand.Pause(), new ReplayCommand.Cancel() })
            Assert.IsType<ReplayCommandDecision.Proceed>(Decide(facts, allowed));
        foreach (ReplayCommand blocked in new ReplayCommand[] { new ReplayCommand.Restart(), new ReplayCommand.PauseEntries(), new ReplayCommand.SetSpeed(), new ReplayCommand.Publish(), new ReplayCommand.RunAllPresets() })
            Rejected(message, Decide(facts, blocked));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Cancel_and_pause_on_a_finished_job_with_reports_change_nothing(bool pause)
    {
        var facts = Ready() with { State = ReplayJobState.Completed, ReportsAvailable = true };
        ReplayCommand command = pause ? new ReplayCommand.Pause() : new ReplayCommand.Cancel();

        Assert.IsType<ReplayCommandDecision.NoOp>(Decide(facts, command));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(facts with { Acquiring = true }, command));
    }

    [Fact]
    public void Retry_during_preparation_is_a_no_op_but_during_reporting_it_is_rejected()
    {
        foreach (var state in new[] { ReplayJobState.Queued, ReplayJobState.Resolving, ReplayJobState.Downloading, ReplayJobState.Validating })
            Assert.IsType<ReplayCommandDecision.NoOp>(Decide(Ready() with { State = state }, new ReplayCommand.Retry()));
        Rejected("Wait for report publication before retrying.", Decide(Ready() with { State = ReplayJobState.Reporting }, new ReplayCommand.Retry()));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready() with { State = ReplayJobState.Failed, CanReplay = false }, new ReplayCommand.Retry()));
    }

    [Fact]
    public void Retry_while_acquisition_is_still_stopping_proceeds_so_the_module_can_defer_it() =>
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready() with { State = ReplayJobState.Failed, Acquiring = true }, new ReplayCommand.Retry()));

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Refetching_needs_a_stopped_job_and_matching_targets(bool failedOnly)
    {
        ReplayCommand command = failedOnly ? new ReplayCommand.RetryFailed() : new ReplayCommand.RefreshGaps();
        var withTargets = Ready() with { FailedTargets = 1, GapTargets = 1 };

        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(withTargets, command));
        const string running = "Pause playback and wait for acquisition/reporting before retrying failed requests.";
        Rejected(running, Decide(withTargets with { Acquiring = true }, command));
        Rejected(running, Decide(withTargets with { State = ReplayJobState.Playing }, command));
        Rejected(running, Decide(withTargets with { State = ReplayJobState.Reporting }, command));
        Rejected("There are no matching acquisition gaps. Empty responses require Refresh coverage gaps, not Retry failed requests.",
            Decide(Ready() with { FailedTargets = failedOnly ? 0 : 3, GapTargets = failedOnly ? 3 : 0 }, command));
    }

    [Fact]
    public void Cancelling_preparation_is_a_no_op_when_nothing_is_preparing()
    {
        Assert.IsType<ReplayCommandDecision.NoOp>(Decide(Ready(), new ReplayCommand.CancelPreparation()));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready() with { State = ReplayJobState.Downloading }, new ReplayCommand.CancelPreparation()));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready() with { Acquiring = true, PriceOnly = true, SessionStarted = true }, new ReplayCommand.CancelPreparation()));
    }

    [Fact]
    public void Entry_controls_need_an_engine_backed_run()
    {
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready(), new ReplayCommand.PauseEntries()));
        Rejected("Engine-backed replay is required.", Decide(Ready() with { PriceOnly = true }, new ReplayCommand.EnableEntries()));
        Rejected("Engine-backed replay is required.", Decide(Ready() with { HasJournalRun = false }, new ReplayCommand.PauseEntries()));
    }

    [Fact]
    public void A_profit_exit_policy_needs_a_paused_engine_backed_run_without_open_exposure()
    {
        var policy = new ProfitExitConfiguration();
        Rejected("Supply the profit-exit policy and net rupee settings.", Decide(Ready(), new ReplayCommand.SetProfitExit(null)));
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready(), new ReplayCommand.SetProfitExit(policy)));
        const string state = "Pause a fully prepared strategy replay before changing its profit-exit policy.";
        foreach (var blocked in new[] { Ready() with { CanReplay = false }, Ready() with { PriceOnly = true }, Ready() with { HasSession = false },
            Ready() with { Acquiring = true }, Ready() with { State = ReplayJobState.Playing } })
            Rejected(state, Decide(blocked, new ReplayCommand.SetProfitExit(policy)));
        Rejected("Engine-backed paper state is required.", Decide(Ready() with { HasPaperState = false }, new ReplayCommand.SetProfitExit(policy)));
        Rejected("Close open/pending paper positions before changing policy, or explicitly restart the replay first.",
            Decide(Ready() with { HasPaperExposure = true }, new ReplayCommand.SetProfitExit(policy)));
    }

    [Fact]
    public void Playback_commands_need_a_ready_dataset_and_are_blocked_while_reporting()
    {
        Rejected("Wait for this report to finish publishing.", Decide(Ready() with { State = ReplayJobState.Reporting }, new ReplayCommand.Step()));
        Rejected("Dataset is not ready for replay.", Decide(Ready() with { CanReplay = false }, new ReplayCommand.Play()));
        Rejected("Dataset is not ready for replay.", Decide(Ready() with { HasSession = false }, new ReplayCommand.Restart()));
        foreach (ReplayCommand command in new ReplayCommand[] { new ReplayCommand.Step(), new ReplayCommand.Play(), new ReplayCommand.Run(), new ReplayCommand.Restart(), new ReplayCommand.RunAllPresets() })
            Assert.IsType<ReplayCommandDecision.Proceed>(Decide(Ready(), command));
    }

    [Fact]
    public void Publishing_a_report_needs_a_finished_requested_scope() =>
        Rejected("Commit and finish the requested replay scope before publishing reports.", Decide(Ready() with { CanPublish = false }, new ReplayCommand.Publish()));

    [Fact]
    public void An_all_preset_comparison_needs_a_paused_engine_backed_replay()
    {
        const string message = "Pause an engine-backed replay before starting an explicit all-preset comparison.";
        Rejected(message, Decide(Ready() with { PriceOnly = true }, new ReplayCommand.RunAllPresets()));
        Rejected(message, Decide(Ready() with { State = ReplayJobState.Playing }, new ReplayCommand.RunAllPresets()));
    }

    [Fact]
    public void Setting_the_speed_is_always_allowed_once_the_job_exists() =>
        Assert.IsType<ReplayCommandDecision.Proceed>(Decide(new ReplayJobFacts { State = ReplayJobState.Failed }, new ReplayCommand.SetSpeed()));
}
