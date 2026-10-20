using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class DecisionReinspectionTests
{
    private readonly TradingDecisionEngine engine = new(new());

    [Fact]
    public void Neutral_evidence_does_not_clear_a_soft_warning_or_allow_a_safe_hold()
    {
        var initial = EngineFixture.Position();
        var firstMarket = EngineFixture.Extend(EngineFixture.Entry(), 99m);
        var warning = engine.EvaluatePosition(Market(initial, firstMarket));
        var neutralMarket = EngineFixture.Extend(EngineFixture.Entry(), 99m, 100m);
        var neutral = Market(initial, neutralMarket) with { Constituents = neutralMarket.FuturesBars.Skip(16)
            .SelectMany(bar => EngineFixture.Banks(bar).Select(bank => bar.Id == "bar-17" ? bank with { Close = bank.Vwap } : bank)).ToArray() };

        var decision = engine.EvaluatePosition(neutral, warning.State);

        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action);
        Assert.Equal(warning.State.SoftWeakeningDeadlineUtc, decision.State.SoftWeakeningDeadlineUtc);
        Assert.Contains("SOFT_THESIS_CONFIRMATION_PENDING", decision.ReasonCodes);
        Assert.Contains(decision.WatchResolutions!, resolution => resolution.DeadlineUtc == warning.State.SoftWeakeningDeadlineUtc);
    }

    [Theory]
    [InlineData("banks", "VWAP_THESIS_WEAKENING")]
    [InlineData("feature", "ADVERSE_CONSTITUENT_CONFIRMATION")]
    public void Known_soft_weakening_starts_its_deadline_even_if_the_other_confirmation_is_missing(string missing, string reason)
    {
        var input = EngineFixture.Position();
        if (missing == "banks")
        {
            var bars = input.FuturesBars.SkipLast(1).Append(EngineFixture.Bar(15, 99m)).ToArray();
            input = input with { FuturesBars = bars, FuturesFeatures = EngineFixture.Features(bars), Constituents = [] };
        }
        else input = input with { FuturesFeatures = [], Constituents = EngineFixture.Banks(input.FuturesBars[^1], adverse: true) };

        var warning = engine.EvaluatePosition(input);

        Assert.Equal(PositionAction.WATCH_EXIT, warning.Action);
        Assert.Contains(reason, warning.ReasonCodes);
        Assert.Equal(input.Position.EnteredAtUtc.AddMinutes(2), warning.State.SoftWeakeningDeadlineUtc);
        var timer = input with { AsOfUtc = input.Position.EnteredAtUtc.AddMinutes(2), OptionQuote = null, FuturesPrice = null };
        Assert.Contains("SOFT_THESIS_TIMEOUT", engine.EvaluatePosition(timer, warning.State).ReasonCodes);
    }

    [Theory]
    [InlineData("candle", true)]
    [InlineData("candle", false)]
    [InlineData("opening", true)]
    [InlineData("opening", false)]
    public void Stale_research_marks_cannot_invent_a_profit_or_cash_stop_observation(string source, bool profitable)
    {
        var input = EngineFixture.Position();
        var option = EngineFixture.Bar(17, profitable ? 225m : 190m) with { InstrumentId = input.Position.Contract.Id };
        var time = option.AvailableAtUtc.AddMinutes(2);
        input = input with { AsOfUtc = time, OptionQuote = null, FuturesPrice = null, Progress = null,
            Position = input.Position with { FrozenStrategy = input.Position.FrozenStrategy with
                { Position = input.Position.FrozenStrategy.Position with { ProfitExit = new() { Mode = ProfitExitMode.FixedNetTarget } } } },
            Portfolio = input.Portfolio with { AvailableAtUtc = time } };
        var gross = profitable ? 625m : -250m;
        var evidence = new CandlePositionEvidence(input.Position.Id, time, source == "candle" ? option : null,
            null, gross, gross - 40m, null, "synthetic-risk-v1",
            source == "opening" ? new(option.Id, option.InstrumentId, option.StartUtc, option.Close) : null);

        var decision = engine.EvaluateCandlePosition(input, evidence);

        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action);
        Assert.Null(decision.GrossLiquidationPnl);
        Assert.Null(decision.NetLiquidationPnl);
        Assert.Contains("POSITION_UNPRICED", decision.ReasonCodes);
        Assert.DoesNotContain("NET_PROFIT_TARGET", decision.ReasonCodes);
        Assert.DoesNotContain("GROSS_CASH_STOP", decision.ReasonCodes);
        Assert.Null(decision.State.PeakNetLiquidationPnl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(24)]
    public void A_profitable_bid_without_whole_position_exit_depth_is_not_a_qualified_net_mark(int? depth)
    {
        var input = EngineFixture.Position();
        input = input with { Position = input.Position with { FrozenStrategy = input.Position.FrozenStrategy with
            { Position = input.Position.FrozenStrategy.Position with { ProfitExit = new() { Mode = ProfitExitMode.FixedNetTarget } } } },
            OptionQuote = input.OptionQuote! with { Bid = 225m, Ask = 226m, BidQuantity = depth },
            NetLiquidation = input.NetLiquidation! with { GrossPnl = 625m, NetPnl = 585m } };

        var decision = engine.EvaluatePosition(input);

        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action);
        Assert.Contains("POSITION_EXIT_DEPTH_UNAVAILABLE", decision.ReasonCodes);
        Assert.Null(decision.GrossLiquidationPnl);
        Assert.Null(decision.NetLiquidationPnl);
        Assert.Null(decision.State.PeakNetLiquidationPnl);
        Assert.DoesNotContain("NET_PROFIT_TARGET", decision.ReasonCodes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Missing_exit_depth_does_not_hide_a_known_cash_stop_or_fabricate_a_liquidation(int? depth)
    {
        var input = EngineFixture.Position();
        var stopped = engine.EvaluatePosition(input with { OptionQuote = input.OptionQuote! with
            { Bid = 192m, Ask = 193m, BidQuantity = depth }, NetLiquidation = null });
        Assert.Equal(PositionAction.EXIT, stopped.Action);
        Assert.Equal("GROSS_CASH_STOP", stopped.ReasonCodes[0]);
        Assert.Null(stopped.GrossLiquidationPnl);
        Assert.Null(stopped.NetLiquidationPnl);
        Assert.Contains(stopped.Gates, gate => gate.Code == "GROSS_CASH_STOP_OBSERVED" && gate.Observed == -200m);
        Assert.Equal(PositionAction.EXIT, engine.EvaluatePosition(input, stopped.State).Action);
    }

    [Theory]
    [InlineData("candle")]
    [InlineData("opening")]
    public void Current_research_marks_still_apply_the_frozen_profit_policy(string source)
    {
        var input = EngineFixture.Position();
        var option = EngineFixture.Bar(17, 225m) with { InstrumentId = input.Position.Contract.Id };
        var time = source == "candle" ? option.AvailableAtUtc : option.StartUtc.AddSeconds(30);
        input = input with { AsOfUtc = time, OptionQuote = null, FuturesPrice = null, Progress = null,
            Position = input.Position with { FrozenStrategy = input.Position.FrozenStrategy with
                { Position = input.Position.FrozenStrategy.Position with { ProfitExit = new() { Mode = ProfitExitMode.FixedNetTarget } } } },
            Portfolio = input.Portfolio with { AvailableAtUtc = time } };
        var evidence = new CandlePositionEvidence(input.Position.Id, time, source == "candle" ? option : null,
            null, 625m, 585m, null, "synthetic-risk-v1",
            source == "opening" ? new(option.Id, option.InstrumentId, option.StartUtc, 225m) : null);

        var decision = engine.EvaluateCandlePosition(input, evidence);

        Assert.Equal(PositionAction.EXIT, decision.Action);
        Assert.Equal(585m, decision.NetLiquidationPnl);
        Assert.Contains("NET_PROFIT_TARGET", decision.ReasonCodes);
        Assert.Equal("CANDLE_RESEARCH", decision.EvidenceClass);
    }

    [Fact]
    public void An_old_underlying_candle_cannot_manufacture_progress_to_suppress_the_time_exit()
    {
        var initial = EngineFixture.Position();
        var market = EngineFixture.Extend(EngineFixture.Entry(), 105m, 105m);
        var time = initial.Position.EnteredAtUtc.AddMinutes(20);
        var input = Market(initial, market) with { AsOfUtc = time, OptionQuote = null, FuturesPrice = null, Progress = null,
            Portfolio = initial.Portfolio with { AvailableAtUtc = time } };
        var evidence = new CandlePositionEvidence(input.Position.Id, time, null, market.FuturesBars[^1],
            null, null, 0.5m, "synthetic-risk-v1");

        var decision = engine.EvaluateCandlePosition(input, evidence);

        Assert.Equal(PositionAction.EXIT, decision.Action);
        Assert.Contains("TIME_NO_PROGRESS", decision.ReasonCodes);
        Assert.Null(decision.State.MaximumFavorableProgressR);
    }

    [Fact]
    public void Current_bank_weakness_cannot_combine_with_old_close_counts_to_invent_a_hard_thesis_failure()
    {
        var initial = EngineFixture.Position();
        initial = initial with { Position = initial.Position with { FrozenStrategy = initial.Position.FrozenStrategy with
            { Position = initial.Position.FrozenStrategy.Position with { MaximumSoftWeakeningDuration = TimeSpan.FromMinutes(10) } } } };
        var two = EngineFixture.Extend(EngineFixture.Entry(), 99m, 98m);
        var previous = engine.EvaluatePosition(Market(initial, two)); // two wrong closes, banks still support
        Assert.False(previous.State.ExitLatched);
        Assert.Equal(2, previous.State.WrongSideCloses);
        var latest = EngineFixture.Extend(EngineFixture.Entry(), 99m, 98m, 105m);
        var missingFeature = Market(initial, latest) with
        {
            FuturesFeatures = latest.FuturesFeatures.SkipLast(1).ToArray(),
            Constituents = latest.FuturesBars.Skip(16).SelectMany(bar => EngineFixture.Banks(bar, adverse: bar.Id == "bar-18")).ToArray()
        };

        var decision = engine.EvaluatePosition(missingFeature, previous.State);

        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action);
        Assert.DoesNotContain("THESIS_FAILURE", decision.ReasonCodes);
        Assert.Contains("THESIS_EVIDENCE_UNKNOWN", decision.ReasonCodes);
        var restored = engine.EvaluatePosition(missingFeature with { FuturesFeatures = latest.FuturesFeatures }, decision.State);
        Assert.Equal(0, restored.State.WrongSideCloses);
        Assert.DoesNotContain("THESIS_FAILURE", restored.ReasonCodes);
    }

    private static PositionEvaluationInput Market(PositionEvaluationInput input, IndexEvaluationInput market) => input with
    {
        AsOfUtc = market.AsOfUtc, FuturesBars = market.FuturesBars, FuturesFeatures = market.FuturesFeatures,
        Constituents = market.FuturesBars.Skip(16).SelectMany(bar => EngineFixture.Banks(bar)).ToArray(),
        OptionQuote = EngineFixture.Quote(input.Position.Contract.Id, market.AsOfUtc, 200m),
        NetLiquidation = input.NetLiquidation! with { QuoteId = EngineFixture.Quote(input.Position.Contract.Id, market.AsOfUtc).Id,
            AvailableAtUtc = market.AsOfUtc, ValidUntilUtc = market.AsOfUtc.AddSeconds(2) },
        FuturesPrice = EngineFixture.Underlying(market.AsOfUtc),
        Progress = input.Progress! with { UnderlyingQuoteId = EngineFixture.Underlying(market.AsOfUtc).Id, AvailableAtUtc = market.AsOfUtc },
        Portfolio = market.Portfolio
    };
}
