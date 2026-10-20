using Trading.Domain;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplaySessionCapabilityTests
{
    [Fact]
    public void Only_engine_backed_sessions_claim_the_paper_capability()
    {
        Assert.False(typeof(IPaperReplaySession).IsAssignableFrom(typeof(PriceOnlyReplaySession)));
        Assert.True(typeof(IPaperReplaySession).IsAssignableFrom(typeof(CandleReplaySession)));
        Assert.True(typeof(IPaperReplaySession).IsAssignableFrom(typeof(DurableReplaySession)));
    }

    [Fact]
    public void A_price_only_run_answers_neutral_paper_views_and_has_no_durable_evidence()
    {
        using var files = new ReplayFixture(); var dataset = files.Load();
        var priceOnly = dataset with { Manifest = dataset.Manifest with { Instruments =
            dataset.Manifest.Instruments.Where(item => item.Role == HistoricalInstrumentRole.CashIndex).ToArray() } };
        var lifecycle = new ReplayRunLifecycle(Guid.NewGuid().ToString("N"), ReplayFixture.Date,
            new(Path.Combine(files.Root, "paper.sqlite")), TimeProvider.System);

        lifecycle.Open(new(priceOnly, files.Configuration, files.Engine, "price-only", [], []), 0);
        lifecycle.SetPaperEntriesPaused(false);

        Assert.True(lifecycle.PriceOnly);
        Assert.Null(lifecycle.PaperState);
        Assert.Empty(lifecycle.VisibleTrades);
        Assert.Empty(lifecycle.VisibleConstituents("BANKNIFTY"));
        Assert.True(lifecycle.PaperEntriesPaused);
        Assert.Null(lifecycle.JournalRunId);
        Assert.False(lifecycle.HasPendingCommit);
    }
}
