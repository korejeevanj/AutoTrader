using Trading.Data;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplayEvidenceVerdictTests
{
    private static readonly DateOnly Day = new(2026, 10, 16);
    private static readonly HistoricalReadinessSummary Qualified = new(true, 4, 0, 0, null, []);
    private static readonly HistoricalReadinessSummary Unqualified = new(false, 4, 4, 1, null, ["SELECTION_EVIDENCE_INCOMPLETE", "WEIGHTS_PUBLICATION_UNQUALIFIED", "CALENDAR_UNVERIFIED"]);

    private static ReplayJobSnapshot Snapshot(ReplayJobState state, HistoricalReadinessSummary? qualification = null, bool acquiring = false,
        bool priceOnly = false, bool canReplay = true, bool preparationComplete = true, IEnumerable<HistoricalInstrumentAcquisition>? rows = null) =>
        new(new ReplayJob("job", Day, false, "fingerprint", state, "message", "TradingDay", null, [], [], 1, 1, canReplay, "mode",
                DateTimeOffset.UnixEpoch, PreparationComplete: preparationComplete),
            acquiring, 1, priceOnly, null, true, false, null, null, null, 0, false, null, [], [], [], null, null, null, false, null, [],
            new AcquisitionAttemptEvidence(1, Day, rows), qualification);

    [Theory]
    [InlineData(ReplayJobState.MarketClosed, "Not applicable — market closed")]
    [InlineData(ReplayJobState.AuthenticationRequired, "Evidence blocked — authentication required")]
    [InlineData(ReplayJobState.NoData, "Evidence unavailable")]
    public void Terminal_states_without_evidence_say_why(ReplayJobState state, string label)
    {
        var verdict = ReplayEvidenceVerdict.From(Snapshot(state, Qualified, canReplay: false));

        Assert.Equal(label, verdict.Label);
        Assert.Equal(state.ToString(), verdict.JobLabel);
    }

    [Fact]
    public void Evidence_is_held_back_while_a_preparation_is_running_whatever_an_earlier_attempt_established()
    {
        var verdict = ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Downloading, Qualified, acquiring: true, canReplay: false, preparationComplete: false));

        Assert.True(verdict.Preparing);
        Assert.Null(verdict.Qualification);
        Assert.Equal("Evidence awaiting validation", verdict.Label);
        Assert.Equal("Acquisition in progress — evidence awaiting validation", verdict.Readiness);
    }

    [Fact]
    public void A_failed_state_shown_before_the_worker_has_stopped_still_counts_as_preparing() =>
        Assert.True(ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Failed, acquiring: true, canReplay: false)).Preparing);

    [Theory]
    [InlineData(ReplayJobState.Queued)] [InlineData(ReplayJobState.Resolving)] [InlineData(ReplayJobState.Downloading)] [InlineData(ReplayJobState.Validating)]
    public void The_preparing_states_count_as_preparing_even_before_the_worker_picks_the_job_up(ReplayJobState state) =>
        Assert.True(ReplayEvidenceVerdict.From(Snapshot(state, canReplay: false)).Preparing);

    [Fact]
    public void Qualified_input_evidence_is_reported_as_qualified_for_the_candle_model()
    {
        var verdict = ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Ready, Qualified));

        Assert.Equal("Evidence qualified", verdict.Label);
        Assert.Equal("Qualified historical inputs — candle model only", verdict.Readiness);
        Assert.Same(Qualified, verdict.Qualification);
        Assert.False(verdict.Preparing);
    }

    [Fact]
    public void Unqualified_evidence_is_incomplete_and_its_issues_are_counted_with_the_coverage_gaps()
    {
        var rows = new[] { new HistoricalInstrumentAcquisition("a", 1, "Call", "Failed", 0, false, "ERR"), new HistoricalInstrumentAcquisition("b", 2, "Put", "Unavailable", 0, false, "EMPTY_RESPONSE") };

        var verdict = ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.ReadyWithWarnings, Unqualified, rows: rows));

        Assert.Equal("Evidence incomplete", verdict.Label);
        Assert.Equal("Incomplete — diagnostic replay only", verdict.Readiness);
        Assert.Equal(1 + 1 + 3, verdict.Issues); // one failed, one empty, three qualification issues
    }

    [Fact]
    public void A_job_that_cannot_replay_yet_is_not_ready() =>
        Assert.Equal("Not ready", ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Failed, canReplay: false)).Readiness);

    [Theory]
    [InlineData(true, "Playback completed — input evidence qualified")]
    [InlineData(false, "Playback completed — evidence incomplete")]
    public void A_completed_playback_names_its_evidence_outcome(bool qualified, string label) =>
        Assert.Equal(label, ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Completed, qualified ? Qualified : Unqualified)).JobLabel);

    [Fact]
    public void A_completed_playback_without_evidence_is_still_being_checked() =>
        Assert.Equal("Playback completed — checking evidence", ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Completed)).JobLabel);

    [Fact]
    public void An_incomplete_job_label_is_just_its_state() =>
        Assert.Equal("Paused", ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Paused, Qualified)).JobLabel);

    [Theory]
    [InlineData(true, false, false, "Prepared strategy inputs await Restart replay")]
    [InlineData(false, true, false, "Strategy input preparation in progress")]
    [InlineData(false, false, false, "Strategy inputs unavailable")]
    public void A_price_preview_never_claims_strategy_evidence_and_explains_where_its_strategy_inputs_stand(bool complete, bool preparing, bool _, string label)
    {
        var verdict = ReplayEvidenceVerdict.From(Snapshot(preparing ? ReplayJobState.Downloading : ReplayJobState.ReadyWithWarnings, Qualified,
            acquiring: preparing, priceOnly: true, preparationComplete: complete));

        Assert.Equal(label, verdict.Label);
        Assert.Null(verdict.Qualification);
        Assert.Equal(preparing ? "Acquisition in progress — evidence awaiting validation" : "Price preview only", verdict.Readiness);
    }

    [Theory]
    [InlineData(true, "Price preview completed — restart to apply prepared inputs")]
    [InlineData(false, "Price preview completed — strategy inputs unavailable")]
    public void A_completed_price_preview_says_whether_prepared_inputs_await_a_restart(bool complete, string label) =>
        Assert.Equal(label, ReplayEvidenceVerdict.From(Snapshot(ReplayJobState.Completed, Qualified, priceOnly: true, preparationComplete: complete)).JobLabel);
}
