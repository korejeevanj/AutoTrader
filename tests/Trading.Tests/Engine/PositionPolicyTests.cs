using System.Text.Json;
using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class PositionPolicyTests
{
    private readonly TradingDecisionEngine engine = new(new());

    [Fact]
    public void Valid_position_holds_and_profit_milestone_is_not_a_take_profit()
    {
        var input = EngineFixture.Position();
        var decision = engine.EvaluatePosition(input);
        Assert.Equal(PositionAction.HOLD, decision.Action);
        Assert.Equal(0m, decision.GrossLiquidationPnl);
        Assert.Equal(-40m, decision.NetLiquidationPnl);
        var quote = input.OptionQuote! with { Bid = 225m, Ask = 226m };
        var profitable = input with { OptionQuote = quote,
            NetLiquidation = input.NetLiquidation! with { GrossPnl = 625m, NetPnl = 585m } };
        // The default trailing profit lock arms above its ₹500 activation: protection tightens, but the profit is not taken.
        Assert.Equal(PositionAction.TIGHTEN_STOP, engine.EvaluatePosition(profitable).Action);
    }

    [Fact]
    public void Fresh_bid_only_cash_stop_exits_without_bar_or_fee_assessment_and_stays_latched()
    {
        var input = EngineFixture.Position();
        input = input with { OptionQuote = input.OptionQuote! with { Bid = 192m, Ask = null },
            NetLiquidation = null, FuturesBars = [], FuturesFeatures = [] };
        var stopped = engine.EvaluatePosition(input);
        Assert.Equal(-200m, stopped.GrossLiquidationPnl);
        Assert.Null(stopped.NetLiquidationPnl);
        Assert.Equal(PositionAction.EXIT, stopped.Action);
        Assert.Contains("GROSS_CASH_STOP", stopped.ReasonCodes);
        var recovered = engine.EvaluatePosition(EngineFixture.Position(), stopped.State);
        Assert.Equal(PositionAction.EXIT, recovered.Action);
        Assert.Contains("GROSS_CASH_STOP", recovered.ReasonCodes);
    }

    [Fact]
    public void Stale_bid_is_unpriced_and_cannot_invent_a_stop_observation()
    {
        var input = EngineFixture.Position();
        var stale = input with { OptionQuote = input.OptionQuote! with { Bid = 180m,
            BidAskObservedAtUtc = input.AsOfUtc.AddSeconds(-3) } };
        var decision = engine.EvaluatePosition(stale);
        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action); // agreed evidence-loss warning policy
        Assert.Null(decision.GrossLiquidationPnl);
        Assert.Null(decision.NetLiquidationPnl);
        Assert.DoesNotContain("GROSS_CASH_STOP", decision.ReasonCodes);
    }

    [Fact]
    public void Structural_invalidation_uses_fresh_underlying_trade_not_packet_heartbeat()
    {
        var input = EngineFixture.Position();
        var breached = input with { FuturesPrice = input.FuturesPrice! with { Price = 95m } };
        Assert.Contains("STRUCTURAL_INVALIDATION", engine.EvaluatePosition(breached).ReasonCodes);
        var stale = breached with { FuturesPrice = breached.FuturesPrice! with
            { LastTradeTimeUtc = breached.AsOfUtc.AddSeconds(-3) } };
        Assert.DoesNotContain("STRUCTURAL_INVALIDATION", engine.EvaluatePosition(stale).ReasonCodes);
    }

    [Fact]
    public void Two_adverse_closes_and_banks_exit_and_batching_cannot_hide_an_earlier_exit()
    {
        var initial = EngineFixture.Position();
        var position = initial.Position with { StructuralStop = 80m };
        var entry = EngineFixture.Entry();
        var one = EngineFixture.Extend(entry, 95m);
        var two = EngineFixture.Extend(entry, 95m, 94m);
        var three = EngineFixture.Extend(entry, 95m, 94m, 105m);
        PositionEvaluationInput Input(IndexEvaluationInput market, bool adverse) => initial with
        {
            AsOfUtc = market.AsOfUtc, Position = position, FuturesBars = market.FuturesBars,
            FuturesFeatures = market.FuturesFeatures, FuturesPrice = market.FuturesPrice,
            Constituents = market.FuturesBars.Skip(16).SelectMany(bar => EngineFixture.Banks(bar,
                adverse && bar.Id != "bar-18")).ToArray(), Portfolio = market.Portfolio
        };
        var first = engine.EvaluatePosition(Input(one, true));
        Assert.Equal(PositionAction.WATCH_EXIT, first.Action);
        var second = engine.EvaluatePosition(Input(two, true), first.State);
        Assert.Contains("THESIS_FAILURE", second.ReasonCodes);
        var stepped = engine.EvaluatePosition(Input(three, true), second.State);
        var batch = engine.EvaluatePosition(Input(three, true));
        Assert.Equal(PositionAction.EXIT, stepped.Action);
        Assert.Equal(PositionAction.EXIT, batch.Action);
        Assert.Contains("THESIS_FAILURE", batch.ReasonCodes);
        Assert.Equal(stepped.State.WrongSideCloses, batch.State.WrongSideCloses);
    }

    [Fact]
    public void Favorable_intervening_close_or_missing_bank_evidence_prevents_thesis_failure()
    {
        var initial = EngineFixture.Position();
        var market = EngineFixture.Extend(EngineFixture.Entry(), 95m, 105m, 94m);
        var input = initial with { Position = initial.Position with { StructuralStop = 80m },
            AsOfUtc = market.AsOfUtc, FuturesBars = market.FuturesBars, FuturesFeatures = market.FuturesFeatures,
            FuturesPrice = market.FuturesPrice, Constituents = market.FuturesBars.Skip(16).SelectMany(bar => EngineFixture.Banks(bar, true)).ToArray(),
            Portfolio = market.Portfolio };
        Assert.DoesNotContain("THESIS_FAILURE", engine.EvaluatePosition(input).ReasonCodes);
        var two = EngineFixture.Extend(EngineFixture.Entry(), 95m, 94m);
        var missing = input with { AsOfUtc = two.AsOfUtc, FuturesBars = two.FuturesBars, FuturesFeatures = two.FuturesFeatures,
            FuturesPrice = two.FuturesPrice, Constituents = [], Portfolio = two.Portfolio };
        Assert.DoesNotContain("THESIS_FAILURE", engine.EvaluatePosition(missing).ReasonCodes);
    }

    [Fact]
    public void Timer_exit_uses_preserved_maximum_progress_without_new_quotes()
    {
        var input = EngineFixture.Position();
        // Entry 105, initial R 8.6: a fresh price of 109.3 supplies actual 0.5R progress.
        var progressed = engine.EvaluatePosition(input with { FuturesPrice = input.FuturesPrice! with { Price = 109.3m },
            Progress = input.Progress! with { ProgressR = 0.5m } });
        var later = input with { AsOfUtc = input.Position.EnteredAtUtc.AddMinutes(20),
            Portfolio = input.Portfolio with { AvailableAtUtc = input.Position.EnteredAtUtc.AddMinutes(20) } };
        Assert.Contains("TIME_NO_PROGRESS", engine.EvaluatePosition(later).ReasonCodes);
        Assert.DoesNotContain("TIME_NO_PROGRESS", engine.EvaluatePosition(later, progressed.State).ReasonCodes);
        var missingReference = later with { Position = later.Position with { UnderlyingEntryReference = null } };
        Assert.DoesNotContain("TIME_NO_PROGRESS", engine.EvaluatePosition(missingReference).ReasonCodes);
    }

    [Fact]
    public void Session_exit_and_loss_pause_work_without_quotes_or_new_bars()
    {
        var input = EngineFixture.Position();
        var atClose = input with { AsOfUtc = input.Session.At(new(15, 15)), OptionQuote = null, FuturesPrice = null,
            FuturesBars = [], FuturesFeatures = [], Portfolio = input.Portfolio with { AvailableAtUtc = input.Session.At(new(15, 15)) } };
        var closing = engine.EvaluatePosition(atClose);
        Assert.Equal(PositionAction.EXIT, closing.Action);
        Assert.Contains("SESSION_EXIT", closing.ReasonCodes);
        Assert.Null(closing.GrossLiquidationPnl);
        var loss = input with { Portfolio = input.Portfolio with { NetLiquidationSessionPnl = -1200m } };
        Assert.Contains("PORTFOLIO_SESSION_PAUSE", engine.EvaluatePosition(loss).ReasonCodes);
        var futureLoss = loss with { Portfolio = loss.Portfolio with { AvailableAtUtc = input.AsOfUtc.AddSeconds(1) } };
        Assert.DoesNotContain("PORTFOLIO_SESSION_PAUSE", engine.EvaluatePosition(futureLoss).ReasonCodes);
    }

    [Fact]
    public void Existing_position_uses_frozen_strategy_and_progress_must_match_frozen_references()
    {
        var input = EngineFixture.Position();
        var changedEngine = new TradingDecisionEngine(new() { Version = "new-config", Strategies =
            [new() { IndexId = "BANKNIFTY", Risk = new() { GrossCashStop = 100m } }] });
        var quote = input.OptionQuote! with { Bid = 194m, Ask = 195m };
        var decision = changedEngine.EvaluatePosition(input with { OptionQuote = quote, NetLiquidation = null });
        Assert.DoesNotContain("GROSS_CASH_STOP", decision.ReasonCodes); // old position still uses 200, not 100
        Assert.Equal("paper-policy-v1", decision.ConfigurationVersion);
        var badProgress = input with { Progress = input.Progress! with { InitialRisk = 1m, ProgressR = 10m } };
        Assert.Null(engine.EvaluatePosition(badProgress).State.MaximumFavorableProgressR);
        Assert.Equal(JsonSerializer.Serialize(engine.EvaluatePosition(input)), JsonSerializer.Serialize(engine.EvaluatePosition(input)));
    }
}
