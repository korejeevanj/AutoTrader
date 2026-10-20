using Trading.Domain;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class HistoricalReadinessTests
{
    [Fact]
    public void A_future_required_bar_error_cannot_invalidate_a_decision_before_that_bar_is_available()
    {
        using var fixture = new ReplayFixture();
        fixture.Configuration = fixture.Configuration with { PublicationLag = TimeSpan.FromSeconds(1) };
        var data = fixture.Load();
        var bad = data.Candles.Single(bar => bar.InstrumentId == "future" && bar.StartUtc == new DateTimeOffset(2026,10,9,4,1,0,TimeSpan.Zero));
        data = data with { Candles = data.Candles.Where(bar => bar.Id != bad.Id).ToArray(),
            Issues = [new("future", "CONFLICTING_DUPLICATES", "Quarantined later completed interval", bad.StartUtc)] };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.True(Assert.Single(result.Readiness!, status => status.TimeUtc == bad.StartUtc.AddSeconds(1)).Qualified);
        Assert.False(Assert.Single(result.Readiness!, status => status.TimeUtc == bad.EndUtc.AddSeconds(1)).Qualified);
    }

    [Fact]
    public void Unused_constituent_definitions_do_not_create_required_bank_evidence()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        var unused = data.Manifest.Instruments.Single(item => item.Id == "bank-00") with
            { Id = "unused-bank", InstrumentToken = 999991, Verified = false, File = "unused-bank.json" };
        data = data with { Manifest = data.Manifest with { Instruments = data.Manifest.Instruments.Append(unused).ToArray() },
            Issues = [new(unused.Id, "FILE_MISSING", "Not a member of the supplied point-in-time weights")] };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.True(result.Complete); Assert.Single(result.Trades);
    }

    [Fact]
    public void A_departed_bank_is_not_required_after_its_weight_snapshot_ends()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        var boundary = new DateTimeOffset(2026,10,9,4,2,0,TimeSpan.Zero);
        var before = data.Manifest.Weights[0] with { EffectiveToUtc = boundary };
        var after = before with { Version = "dated-replacement", EffectiveFromUtc = boundary, EffectiveToUtc = null,
            Members = before.Members.Where(member => member.InstrumentId != "bank-13")
                .Select(member => member.InstrumentId == "bank-00" ? member with { WeightPercent = 16m } : member).ToArray() };
        data = data with { Manifest = data.Manifest with { Weights = [before, after] },
            Candles = data.Candles.Where(bar => bar.InstrumentId != "bank-13" || bar.StartUtc < boundary).ToArray() };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.True(result.Complete); Assert.Single(result.Trades);
    }

    [Fact]
    public void An_option_conflict_before_the_entry_window_does_not_poison_later_clean_current_evidence()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        var bad = data.Candles.First(bar => bar.InstrumentId == "call");
        data = data with { Candles = data.Candles.Where(bar => bar.Id != bad.Id).ToArray(),
            Issues = [new("call", "CONFLICTING_DUPLICATES", "Quarantined option minute before entry decisions", bad.StartUtc)] };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.All(result.Readiness!, status => Assert.Equal(0, status.UnknownOptions));
        Assert.True(result.Complete);
    }

    [Fact]
    public void A_missing_cash_index_definition_cannot_silently_remove_a_required_input()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        data = data with { Manifest = data.Manifest with { Instruments = data.Manifest.Instruments.Where(item => item.Role != HistoricalInstrumentRole.CashIndex).ToArray() },
            Candles = data.Candles.Where(bar => bar.InstrumentId != "index").ToArray() };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Contains("CASH_INDEX_UNAVAILABLE", result.CompletionIssues);
    }

    [Fact]
    public void Unqualified_option_metadata_cannot_supply_a_substantiated_expiry_exclusion()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        data = data with { Manifest = data.Manifest with { Instruments = data.Manifest.Instruments.Select(item => item.Id == "call"
            ? item with { Verified = false, Expiry = ReplayFixture.Date } : item).ToArray() } };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Contains("SELECTION_EVIDENCE_INCOMPLETE", result.CompletionIssues);
    }

    [Fact]
    public void Dated_quality_failures_do_not_contaminate_an_independent_clean_session()
    {
        using var fixture = new ReplayFixture(ReplayFixture.Date, ReplayFixture.Date.AddDays(3)); var data = fixture.Load();
        var bad = data.Candles.First(bar => bar.InstrumentId == "call");
        data = data with { Candles = data.Candles.Where(bar => bar.Id != bad.Id).ToArray(),
            Issues = [new("call", "CONFLICTING_DUPLICATES", "Quarantined prior-session conflict", bad.StartUtc)] };
        var sessions = new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions;
        Assert.True(sessions[1].Complete);
        Assert.All(sessions[1].Readiness!, status => Assert.Equal(0, status.UnknownOptions));
    }

    [Fact]
    public void Later_option_observations_do_not_repair_an_earlier_unknown_selection()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        var cutoff = new DateTimeOffset(2026, 10, 9, 4, 1, 0, TimeSpan.Zero); // 09:31 IST
        data = data with { Candles = data.Candles.Where(bar => bar.InstrumentId != "put" || bar.StartUtc >= cutoff).ToArray() };
        var session = new ReplayRunner(data, fixture.Configuration, fixture.Engine).CreateSession(ReplayFixture.Date, "base");
        while (session.CurrentTimeUtc is null || session.CurrentTimeUtc < cutoff) session.Step();
        var earlier = session.Result.Readiness!.ToArray();
        Assert.All(earlier, status => Assert.Equal(1, status.UnknownOptions));
        session.RunToEnd();
        Assert.True(session.Result.Readiness![^1].Qualified);
        Assert.False(session.Result.Complete);
        Assert.All(earlier, status => Assert.Equal(1, status.UnknownOptions));
    }

    [Fact]
    public void Observed_zero_volume_is_a_substantiated_candle_policy_exclusion_and_zero_trades_are_valid()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        data = data with { Candles = data.Candles.Select(bar => bar.InstrumentId is "call" or "put" ? bar with { Volume = 0m } : bar).ToArray() };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.True(result.Complete);
        Assert.Empty(result.Trades);
        Assert.All(result.Readiness!, status => { Assert.Equal(2, status.ExcludedOptions); Assert.Equal(0, status.UnknownOptions); });
    }

    [Theory]
    [InlineData(HistoricalAvailabilityMethod.HttpLastModified, false)]
    [InlineData(HistoricalAvailabilityMethod.OfficialPublication, true)]
    [InlineData(HistoricalAvailabilityMethod.ContemporaneousCapture, true)]
    public void Publication_qualification_distinguishes_modification_headers_from_dated_evidence(HistoricalAvailabilityMethod method, bool qualified)
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        var weights = data.Manifest.Weights[0];
        data = data with { Manifest = data.Manifest with { Weights = [weights with
            { PublicationEvidence = weights.PublicationEvidence! with { Method = method } }] } };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.Equal(qualified, result.Complete);
    }

    [Fact]
    public void A_known_membership_change_ends_the_snapshot_validity_and_blocks_confirmation()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        data = data with { Manifest = data.Manifest with { Weights = [data.Manifest.Weights[0] with
            { EffectiveToUtc = new(2026, 10, 9, 4, 0, 0, TimeSpan.Zero) }] } }; // 09:30 IST
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Empty(result.Trades);
        Assert.Contains("WEIGHTS_UNAVAILABLE_OR_INVALID", result.CompletionIssues);
    }

    [Fact]
    public void Future_publication_evidence_cannot_qualify_a_past_snapshot()
    {
        using var fixture = new ReplayFixture(); var data = fixture.Load();
        var weights = data.Manifest.Weights[0];
        data = data with { Manifest = data.Manifest with { Weights = [weights with
            { PublicationEvidence = weights.PublicationEvidence! with { EstablishedAtUtc = ReplayFixture.MetadataTime.AddMonths(1) } }] } };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Contains("WEIGHTS_PUBLICATION_UNQUALIFIED", result.CompletionIssues);
    }

    [Fact]
    public void A_missing_bank_prefix_is_incomplete_even_when_an_import_issue_list_is_absent()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var missing = data.Candles.First(bar => bar.InstrumentId == "bank-13");
        data = data with { Candles = data.Candles.Where(bar => bar.Id != missing.Id).ToArray() };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Contains("REQUIRED_SERIES_INCOMPLETE", result.CompletionIssues);
        Assert.Empty(result.Trades);
    }

    [Theory]
    [InlineData("CALENDAR_UNVERIFIED")]
    [InlineData("CALENDAR_PUBLICATION_UNQUALIFIED")]
    [InlineData("OPTION_UNIVERSE_UNQUALIFIED")]
    public void Complete_candles_do_not_qualify_a_day_without_calendar_and_full_universe_evidence(string missing)
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        data = data with { Manifest = data.Manifest with { CalendarVerified = missing != "CALENDAR_UNVERIFIED",
            IncludesAllOptions = missing != "OPTION_UNIVERSE_UNQUALIFIED",
            CalendarPublicationEvidence = missing == "CALENDAR_PUBLICATION_UNQUALIFIED" ? null : data.Manifest.CalendarPublicationEvidence } };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Contains(missing, result.CompletionIssues);
    }

    [Fact]
    public void Verified_weights_without_defensible_publication_evidence_are_diagnostic_research_only()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        data = data with { Manifest = data.Manifest with { Weights = [data.Manifest.Weights[0] with { PublicationEvidence = null }] } };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Contains("WEIGHTS_PUBLICATION_UNQUALIFIED", result.CompletionIssues);
        Assert.Single(result.Trades); // Preserve the labelled research path, not a fully qualified result.
    }

    [Theory]
    [InlineData(45, true)]
    [InlineData(46, false)]
    public void Carried_forward_weights_have_a_forty_five_calendar_day_limit(int age, bool qualified)
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        data = data with { Manifest = data.Manifest with { Weights = [data.Manifest.Weights[0] with
            { SnapshotDate = ReplayFixture.Date.AddDays(-age), WeightConvention = "LATEST_PUBLISHED_MONTH_END_CARRIED_FORWARD" }] } };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.Equal(qualified, result.Complete);
        if (qualified) Assert.Single(result.Trades);
        else
        {
            Assert.Empty(result.Trades);
            Assert.Contains("WEIGHTS_SNAPSHOT_TOO_OLD", result.CompletionIssues);
        }
    }

    [Fact]
    public void An_unknown_option_does_not_turn_observed_subset_results_into_a_complete_experiment()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        data = data with { Candles = data.Candles.Where(bar => bar.InstrumentId != "put").ToArray() };
        var result = new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd();
        var session = Assert.Single(result.Sessions);
        Assert.False(session.Complete);
        Assert.Contains("SELECTION_EVIDENCE_INCOMPLETE", session.CompletionIssues);
        Assert.Contains(session.Decisions, row => row.Class == "DATA_READINESS" && row.Reasons.Contains("SELECTION_EVIDENCE_INCOMPLETE"));
        var output = Path.Combine(fixture.Root, "report");
        ReplayReportWriter.Write(output, result);
        Assert.Contains("0 complete sessions", File.ReadAllText(Path.Combine(output, "summary.md")));
        Assert.Contains("aggregate net unavailable", File.ReadAllText(Path.Combine(output, "summary.md")));
    }
}
