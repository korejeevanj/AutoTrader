using Microsoft.Data.Sqlite;
using Trading.Data;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class DurableReplaySessionTests
{
    [Fact]
    public void Failed_entry_control_rolls_back_and_recovery_replays_committed_controls_including_before_the_first_step()
    {
        using var fixture = new ReplayFixture();
        var runner = new ReplayRunner(fixture.Load(), fixture.Configuration, fixture.Engine);
        var path = Path.Combine(fixture.Root, "paper.sqlite");
        var journal = new ReplayEvidenceJournal(path);
        var run = new ReplayEvidenceRun(Guid.NewGuid().ToString("N"), 1, "base", "frozen-inputs",
            new(2026, 10, 9, 3, 45, 0, TimeSpan.Zero), "BANKNIFTY");
        var session = new DurableReplaySession(runner.CreateSession(ReplayFixture.Date, "base"), journal, run);
        session.SetPaperEntriesPaused(true);
        var beforeFirstStep = new DurableReplaySession(runner.CreateSession(ReplayFixture.Date, "base"), new(path), run);
        Assert.Null(beforeFirstStep.CurrentTimeUtc);
        Assert.True(beforeFirstStep.PaperEntriesPaused);

        session.Step(); session.Resume();
        Execute(path, "CREATE TRIGGER fail_control BEFORE INSERT ON paper_batches WHEN NEW.batch_id LIKE 'control-%' BEGIN SELECT RAISE(FAIL,'test control failure'); END;");
        Assert.Throws<SqliteException>(() => session.SetPaperEntriesPaused(false));
        Assert.True(session.PaperEntriesPaused);
        Assert.True(session.IsPaused);
        Execute(path, "DROP TRIGGER fail_control;");
        session.SetPaperEntriesPaused(false);
        session.Step();

        var recovered = new DurableReplaySession(runner.CreateSession(ReplayFixture.Date, "base"), new(path), run);
        Assert.Equal(session.CurrentTimeUtc, recovered.CurrentTimeUtc);
        Assert.False(recovered.PaperEntriesPaused);
        Assert.True(recovered.IsPaused);
        var controls = journal.Read(run.Id, 1000, "PAPER_CONTROL");
        Assert.Equal(new[] { "PAUSE_NEW_ENTRIES", "ENABLE_NEW_ENTRIES" }, controls.Select(row => row.Action));
        Assert.Equal(run.SessionOpenUtc, controls[0].TimeUtc);
        Assert.Throws<InvalidDataException>(() => new DurableReplaySession(runner.CreateSession(ReplayFixture.Date, "base"),
            new(path), run with { InputHash = "different-inputs" }));
    }

    [Fact]
    public void A_failed_commit_retains_the_exact_step_and_its_history_until_retry_then_recovers_without_duplicate_fills()
    {
        using var fixture = new ReplayFixture();
        var runner = new ReplayRunner(fixture.Load(), fixture.Configuration, fixture.Engine);
        var path = Path.Combine(fixture.Root, "paper.sqlite");
        var journal = new ReplayEvidenceJournal(path);
        var run = new ReplayEvidenceRun(Guid.NewGuid().ToString("N"), 1, "base", "frozen-inputs",
            new(2026, 10, 9, 3, 45, 0, TimeSpan.Zero), "BANKNIFTY");
        var session = new DurableReplaySession(runner.CreateSession(ReplayFixture.Date, "base"), journal, run);
        for (var i = 0; i < 16; i++) session.Step();
        Assert.Empty(session.Result.Decisions);
        Execute(path, "CREATE TRIGGER fail_batch BEFORE INSERT ON paper_batches BEGIN SELECT RAISE(FAIL,'test commit failure'); END;");
        Assert.Throws<SqliteException>(() => session.Step());
        var pending = session.LastStep;
        Assert.NotNull(pending);
        Assert.True(session.HasPendingCommit);
        Assert.True(session.IsPaused);
        Assert.Single(session.VisibleTrades);
        Assert.NotEmpty(session.Result.Decisions);
        Assert.Throws<InvalidOperationException>(() => session.ReleaseCommittedDecisionHistory());
        Assert.Throws<InvalidOperationException>(() => session.SetPaperEntriesPaused(true));
        Assert.False(session.PaperEntriesPaused);

        Execute(path, "DROP TRIGGER fail_batch;");
        Assert.Same(pending, session.Step());
        Assert.Equal(pending.TimeUtc, session.CurrentTimeUtc);
        Assert.Equal(17, session.StepNumber);
        Assert.False(session.HasPendingCommit);
        Assert.Empty(session.Result.Decisions);

        var recovered = new DurableReplaySession(runner.CreateSession(ReplayFixture.Date, "base"), new(path), run);
        Assert.Equal(session.CurrentTimeUtc, recovered.CurrentTimeUtc);
        Assert.Equal(session.PaperState!.Cash, recovered.PaperState!.Cash);
        Assert.Equal(session.PaperState.PaidFees, recovered.PaperState.PaidFees);
        Assert.Equal(session.PaperState.AcceptedAttempts, recovered.PaperState.AcceptedAttempts);
        Assert.Equal(Assert.Single(session.PaperState.Positions).Id, Assert.Single(recovered.PaperState.Positions).Id);
        Assert.Single(recovered.VisibleTrades);
        Assert.Equal(17, recovered.StepNumber);
        Assert.True(recovered.IsPaused);
        recovered.Step();
        Assert.True(recovered.CurrentTimeUtc > pending.TimeUtc);
        Assert.Single(journal.Read(run.Id, 1000), row => row.Action == "HYPOTHETICAL_FILLED");
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
