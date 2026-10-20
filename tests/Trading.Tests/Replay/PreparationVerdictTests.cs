using Trading.Data;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class PreparationVerdictTests
{
    private static readonly TimeOnly Open = new(9, 15), Close = new(9, 36);

    private static PreparationVerdict Evaluate(ReplayFixture fixture, bool allOptionsCovered = true, TimeOnly? open = null,
        IReadOnlyList<string>? planWarnings = null) =>
        PreparationVerdict.Evaluate(fixture.Configuration, fixture.Engine,
            new HistoricalAcquisitionPlan(fixture.ReadManifest(), allOptionsCovered, "fixture", planWarnings ?? []),
            fixture.ManifestPath, ReplayFixture.Date, open ?? Open, Close);

    [Fact]
    public void A_fully_covered_qualified_day_is_ready_without_warnings()
    {
        using var fixture = new ReplayFixture();

        var verdict = Evaluate(fixture);

        Assert.True(verdict.Qualification.Qualified);
        Assert.Empty(verdict.Warnings);
        Assert.True(verdict.Ready);
    }

    [Fact]
    public void A_plan_that_does_not_cover_every_option_disables_candle_research_and_is_not_ready()
    {
        using var fixture = new ReplayFixture();

        var verdict = Evaluate(fixture, allOptionsCovered: false);

        Assert.False(verdict.Replay.EnableCandleResearch);
        Assert.Contains("CANDLE_RESEARCH_DISABLED", verdict.Qualification.Issues);
        Assert.False(verdict.Ready);
    }

    [Fact]
    public void A_special_session_needs_a_date_specific_profile_so_strategies_and_research_are_disabled()
    {
        using var fixture = new ReplayFixture();

        var verdict = Evaluate(fixture, open: new(9, 20));

        Assert.Contains("SPECIAL_SESSION_REQUIRES_DATE_SPECIFIC_STRATEGY_PROFILE", verdict.Warnings);
        Assert.False(verdict.Replay.EnableCandleResearch);
        Assert.All(verdict.Engine.Strategies, strategy => Assert.False(strategy.Enabled));
        Assert.False(verdict.Ready);
    }

    [Fact]
    public void A_day_without_an_applicable_fee_schedule_warns_that_fees_are_unavailable_for_research()
    {
        using var fixture = new ReplayFixture();
        fixture.Configuration = fixture.Configuration with { FeeSchedules = [] };

        var verdict = Evaluate(fixture);

        Assert.Contains("FEES_UNAVAILABLE_FOR_RESEARCH", verdict.Warnings);
        Assert.False(verdict.Ready);
    }

    [Fact]
    public void An_option_series_without_candles_is_counted_as_a_warning()
    {
        using var fixture = new ReplayFixture();
        File.Delete(Path.Combine(fixture.Root, "call.json"));

        var verdict = Evaluate(fixture);

        Assert.Contains("OPTION_SERIES_UNAVAILABLE:1", verdict.Warnings);
        Assert.False(verdict.Ready);
    }

    [Fact]
    public void A_missing_bank_nifty_profile_is_rejected()
    {
        using var fixture = new ReplayFixture();
        var plan = new HistoricalAcquisitionPlan(fixture.ReadManifest(), true, "fixture", []);
        var engine = fixture.Engine with { Strategies = [] };

        Assert.Throws<InvalidDataException>(() =>
            PreparationVerdict.Evaluate(fixture.Configuration, engine, plan, fixture.ManifestPath, ReplayFixture.Date, Open, Close));
    }
}
