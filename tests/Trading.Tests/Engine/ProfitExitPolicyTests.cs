using System.Text.Json;
using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class ProfitExitPolicyTests
{
    [Fact]
    public void Missing_or_stale_net_marks_preserve_the_trail_without_inventing_a_profit_exit()
    {
        var engine = new TradingDecisionEngine(new()); var input = WithPolicy(ProfitExitMode.TrailingNetProfit);
        var peak = engine.EvaluatePosition(Mark(input, 800m));
        var unavailable = Mark(input, 1000m, 1) with { NetLiquidation = null };
        var unknown = engine.EvaluatePosition(unavailable, peak.State);
        Assert.Equal(PositionAction.WATCH_EXIT, unknown.Action);
        Assert.Equal(800m, unknown.State.PeakNetLiquidationPnl); Assert.Equal(600m, unknown.State.ProfitTrailThreshold);
        var stale = Mark(input, 100m, 2);
        stale = stale with { OptionQuote = stale.OptionQuote! with { BidAskObservedAtUtc = stale.AsOfUtc.AddSeconds(-3) } };
        Assert.Equal(PositionAction.WATCH_EXIT, engine.EvaluatePosition(stale, unknown.State).Action);
        var hit = engine.EvaluatePosition(Mark(input, 600m, 3), unknown.State);
        var missingAfterExit = Mark(input, 900m, 4) with { OptionQuote = null, NetLiquidation = null };
        Assert.Equal(PositionAction.EXIT, engine.EvaluatePosition(missingAfterExit, hit.State).Action);
    }

    [Fact]
    public void Mandatory_invalidation_has_priority_and_cash_stops_do_not_wait_for_net_fees()
    {
        var engine = new TradingDecisionEngine(new()); var input = WithPolicy(ProfitExitMode.FixedNetTarget);
        var profitable = Mark(input, 500m);
        var invalidated = engine.EvaluatePosition(profitable with { FuturesPrice = profitable.FuturesPrice! with { Price = 95m } });
        Assert.Equal("STRUCTURAL_INVALIDATION", invalidated.ReasonCodes[0]);
        Assert.Contains("NET_PROFIT_TARGET", invalidated.ReasonCodes);
        var stopped = engine.EvaluatePosition(input with { OptionQuote = input.OptionQuote! with { Bid = 192m, Ask = null }, NetLiquidation = null });
        Assert.Equal(PositionAction.EXIT, stopped.Action); Assert.Equal("GROSS_CASH_STOP", stopped.ReasonCodes[0]);
    }

    [Fact]
    public void A_new_engine_profile_cannot_change_an_existing_positions_frozen_profit_policy()
    {
        var settings = new EngineConfiguration { Strategies = [new() { IndexId = "BANKNIFTY", Position = new()
            { ProfitExit = new() { Mode = ProfitExitMode.FixedNetTarget } } }] };
        var engine = new TradingDecisionEngine(settings);
        Assert.Equal(PositionAction.HOLD, engine.EvaluatePosition(Mark(WithPolicy(ProfitExitMode.Disabled), 800m)).Action);
    }

    [Fact]
    public void Future_peak_state_cannot_be_borrowed_by_an_earlier_decision()
    {
        var engine = new TradingDecisionEngine(new()); var input = WithPolicy(ProfitExitMode.TrailingNetProfit);
        var future = engine.EvaluatePosition(Mark(input, 800m, 2));
        Assert.Throws<ArgumentException>(() => engine.EvaluatePosition(Mark(input, 500m, 1), future.State));
    }

    [Theory]
    [InlineData(0, 500, 200, 300)]
    [InlineData(500, 0, 200, 0)]
    [InlineData(500, 500, 0, 300)]
    [InlineData(500, 500, 200, 500)]
    [InlineData(500, 500, 200, -1)]
    public void Invalid_profit_budgets_are_rejected(decimal target, decimal activation, decimal giveback, decimal floor)
    {
        var settings = new EngineConfiguration { Strategies = [new() { IndexId = "BANKNIFTY", Position = new()
            { ProfitExit = new() { Mode = ProfitExitMode.TrailingNetProfit, NetTarget = target, TrailActivation = activation,
                TrailGiveback = giveback, MinimumLockedNetProfit = floor } } }] };
        Assert.NotEmpty(settings.Validate()); Assert.Throws<ArgumentException>(() => EngineConfiguration.FromJson(settings.ToJson()));
    }

    [Fact]
    public void Trailing_profit_arms_at_five_hundred_tightens_and_exits_on_a_two_hundred_giveback()
    {
        var engine = new TradingDecisionEngine(new()); var input = WithPolicy(ProfitExitMode.TrailingNetProfit);
        var below = engine.EvaluatePosition(Mark(input, 498.75m));
        var armed = engine.EvaluatePosition(Mark(input, 500m, 1), below.State);
        var peak = engine.EvaluatePosition(Mark(input, 800m, 2), armed.State);
        var pullback = engine.EvaluatePosition(Mark(input, 700m, 3), peak.State);
        var hit = engine.EvaluatePosition(Mark(input, 600m, 4), pullback.State);
        Assert.Equal(PositionAction.TIGHTEN_STOP, armed.Action); Assert.Equal(PositionAction.HOLD, pullback.Action);
        Assert.Equal(PositionAction.EXIT, hit.Action); Assert.Contains("NET_PROFIT_TRAIL", hit.ReasonCodes);
        Assert.Equal(300m, Assert.Single(armed.Gates, gate => gate.Code == "NET_PROFIT_TRAIL_THRESHOLD").Threshold);
        Assert.Equal(600m, Assert.Single(peak.Gates, gate => gate.Code == "NET_PROFIT_TRAIL_THRESHOLD").Threshold);
        Assert.Equal(600m, Assert.Single(pullback.Gates, gate => gate.Code == "NET_PROFIT_TRAIL_THRESHOLD").Threshold);
    }

    [Fact]
    public void Fixed_target_uses_net_profit_and_latches_the_exit_request()
    {
        var engine = new TradingDecisionEngine(new());
        var input = WithPolicy(ProfitExitMode.FixedNetTarget);
        Assert.Equal(PositionAction.HOLD, engine.EvaluatePosition(Mark(input, 498.75m)).Action);
        var hit = engine.EvaluatePosition(Mark(input, 500m));
        Assert.Equal(PositionAction.EXIT, hit.Action);
        Assert.Contains("NET_PROFIT_TARGET", hit.ReasonCodes);
        Assert.Equal(500m, hit.NetLiquidationPnl);
        var later = engine.EvaluatePosition(Mark(input, 300m, 1), hit.State);
        Assert.Equal(PositionAction.EXIT, later.Action);
        Assert.Contains("NET_PROFIT_TARGET", later.ReasonCodes);
    }

    private static PositionEvaluationInput WithPolicy(ProfitExitMode mode)
    {
        var input = EngineFixture.Position();
        return input with { Position = input.Position with { FrozenStrategy = input.Position.FrozenStrategy with
            { Position = input.Position.FrozenStrategy.Position with { ProfitExit = new() { Mode = mode } } } } };
    }

    private static PositionEvaluationInput Mark(PositionEvaluationInput input, decimal net, int seconds = 0)
    {
        var time = input.AsOfUtc.AddSeconds(seconds);
        // This fixture has one 25-unit position and exactly ₹20 fees on each side.
        var gross = net + 40m;
        var quote = EngineFixture.Quote(input.Position.Contract.Id, time, input.Position.EntryPrice + gross / input.Position.Units);
        var underlying = EngineFixture.Underlying(time);
        return input with { AsOfUtc = time, OptionQuote = quote, FuturesPrice = underlying,
            Progress = input.Progress! with { UnderlyingQuoteId = underlying.Id, AvailableAtUtc = time },
            Portfolio = input.Portfolio with { AvailableAtUtc = time, NetLiquidationSessionPnl = net },
            NetLiquidation = input.NetLiquidation! with { QuoteId = quote.Id, AvailableAtUtc = time, ValidUntilUtc = time.AddMinutes(1), GrossPnl = gross, NetPnl = net } };
    }

    [Fact]
    public void Profit_exit_profiles_load_and_export_explicit_net_rupee_settings()
    {
        const string json = """
            {"Strategies":[{"IndexId":"BANKNIFTY","Position":{"ProfitExit":{
              "Mode":"TrailingNetProfit","NetTarget":500,"TrailActivation":500,
              "TrailGiveback":200,"MinimumLockedNetProfit":300,"Version":"profit-exit-v1"}}}]}
            """;
        var configuration = EngineConfiguration.FromJson(json);
        Assert.Empty(configuration.Validate());
        using var exported = JsonDocument.Parse(configuration.ToJson());
        var policy = exported.RootElement.GetProperty("Strategies")[0].GetProperty("Position").GetProperty("ProfitExit");
        Assert.Equal("TrailingNetProfit", policy.GetProperty("Mode").GetString());
        Assert.Equal(300m, policy.GetProperty("MinimumLockedNetProfit").GetDecimal());
    }
}
