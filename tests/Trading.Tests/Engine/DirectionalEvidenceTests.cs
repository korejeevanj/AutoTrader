using System.Text.Json;
using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class DirectionalEvidenceTests
{
    [Fact]
    public void Directional_snapshot_explains_the_crossing_and_absolute_constituent_support()
    {
        var input = EngineFixture.Entry();
        var result = new TradingDecisionEngine(new()).EvaluateIndex(input);
        var evidence = Assert.IsType<DirectionalEvidence>(result.DirectionalEvidence);
        Assert.Equal(DirectionalStatus.UP, evidence.Status);
        Assert.Equal(input.AsOfUtc, evidence.AsOfUtc);
        Assert.Equal("bar-15", evidence.BarId);
        Assert.Equal(105m, evidence.Close);
        Assert.Equal(103m, evidence.OpeningHigh);
        Assert.Equal(2m, evidence.CloseMinusOpeningHigh);
        Assert.Equal(5m, evidence.CloseMinusVwap);
        Assert.Equal(5m, evidence.Return1MinutePercent);
        Assert.Equal(5m, evidence.Return5MinutePercent);
        Assert.Equal(65m, evidence.AboveWeightPercent);
        Assert.Equal(35m, evidence.BelowWeightPercent);
        Assert.Equal(0m, evidence.MissingWeightPercent);
        Assert.Equal(1, evidence.ConfirmingCount);
        Assert.Equal(1, evidence.OpposingCount);
        Assert.Equal(65m, evidence.TopThreeConfirmingWeightPercent);
        Assert.Equal("weights-v1", evidence.ConstituentDefinitionVersion);
        Assert.Equal(2, evidence.Constituents.Count);
        Assert.Equal(16, evidence.InputBarIds.Count);
        Assert.True(Assert.Single(result.Candidates).Eligible);
    }

    [Fact]
    public void Optional_breadth_rule_rejects_one_dominant_constituent_and_accepts_broad_support()
    {
        var input = EngineFixture.Entry();
        var settings = new EngineConfiguration();
        var engine = new TradingDecisionEngine(settings with { Strategies = [settings.Strategies[0] with
            { MinimumConfirmingConstituents = 2 }] });
        var narrow = engine.EvaluateIndex(input);
        Assert.False(Assert.Single(narrow.Candidates).Eligible);
        Assert.Contains(narrow.ContextGates, gate => gate.Code == "CONSTITUENT_BREADTH" && gate.Outcome == GateOutcome.FAIL
            && gate.Observed == 1m && gate.Threshold == 2m);
        Assert.False(narrow.DirectionalEvidence!.MeetsDirectionalRules);
        var broad = engine.EvaluateIndex(input with { Constituents = input.Constituents.Select(row => row with { Close = 110m }).ToArray() });
        Assert.True(Assert.Single(broad.Candidates).Eligible);
        Assert.Equal(2, broad.DirectionalEvidence!.ConfirmingCount);
        Assert.Contains(broad.DirectionalEvidence.Gates, gate => gate.Code == "CONSTITUENT_BREADTH" && gate.Outcome == GateOutcome.PASS);
        Assert.Throws<ArgumentException>(() => new TradingDecisionEngine(settings with { Strategies = [settings.Strategies[0] with
            { MinimumConfirmingConstituents = 0 }] }));
    }

    [Fact]
    public void Missing_weight_remains_absolute_and_unavailable_information_cannot_change_a_snapshot()
    {
        var input = EngineFixture.Entry();
        var engine = new TradingDecisionEngine(new());
        var missing = engine.EvaluateIndex(input with { Constituents = [input.Constituents[0]] }).DirectionalEvidence!;
        Assert.Equal(DirectionalStatus.INSUFFICIENT_DATA, missing.Status);
        Assert.Equal(65m, missing.AboveWeightPercent);
        Assert.Equal(35m, missing.MissingWeightPercent);
        Assert.Null(missing.ConfirmingCount);
        var before = engine.EvaluateIndex(input).DirectionalEvidence;
        var futureBar = EngineFixture.Bar(16, 500m);
        var future = input with
        {
            FuturesBars = input.FuturesBars.Append(futureBar).ToArray(),
            FuturesFeatures = input.FuturesFeatures.Concat(EngineFixture.Features([futureBar])).ToArray(),
            Constituents = input.Constituents.Concat(EngineFixture.Banks(futureBar, adverse: true)).ToArray()
        };
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(engine.EvaluateIndex(future).DirectionalEvidence));
    }

    [Fact]
    public void Future_weight_publication_is_not_reported_as_available_snapshot_evidence()
    {
        var input = EngineFixture.Entry();
        var result = new TradingDecisionEngine(new()).EvaluateIndex(input with
            { ConstituentDefinitions = input.ConstituentDefinitions! with { AvailableAtUtc = input.AsOfUtc.AddMinutes(1) } });
        var evidence = result.DirectionalEvidence!;
        Assert.Equal(DirectionalStatus.INSUFFICIENT_DATA, evidence.Status);
        Assert.True(evidence.EvidenceAvailableAtUtc <= input.AsOfUtc);
        Assert.Null(evidence.ConstituentDefinitionVersion);
        Assert.Empty(evidence.Constituents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Breadth_is_directional_and_neutral_members_do_not_count_as_support(bool bullish)
    {
        var input = EngineFixture.Entry();
        if (!bullish)
        {
            var bars = input.FuturesBars.Take(15).Append(EngineFixture.Bar(15, 95m)).ToArray();
            input = input with { FuturesBars = bars, FuturesFeatures = EngineFixture.Features(bars),
                FuturesPrice = EngineFixture.Underlying(input.AsOfUtc, 95m), Constituents = EngineFixture.Banks(bars[^1], adverse: true),
                Candidates = [input.Candidates[0] with { Contract = input.Candidates[0].Contract with { Right = OptionRight.Put } }] };
        }
        var settings = new EngineConfiguration();
        var engine = new TradingDecisionEngine(settings with { Strategies = [settings.Strategies[0] with { MinimumConfirmingConstituents = 2 }] });
        Assert.False(Assert.Single(engine.EvaluateIndex(input).Candidates).Eligible);
        var neutral = engine.EvaluateIndex(input with { Constituents = input.Constituents.Select(row => row with { Close = 100m }).ToArray() });
        Assert.Equal(0, neutral.DirectionalEvidence!.ConfirmingCount);
        Assert.Equal(2, neutral.DirectionalEvidence.NeutralCount);
        var broad = engine.EvaluateIndex(input with { Constituents = input.Constituents.Select(row => row with { Close = bullish ? 110m : 90m }).ToArray() });
        Assert.True(Assert.Single(broad.Candidates).Eligible);
        Assert.Equal(bullish ? DirectionalStatus.UP : DirectionalStatus.DOWN, broad.DirectionalEvidence!.Status);
    }

    [Fact]
    public void Entry_breadth_filter_cannot_delay_an_independent_known_cash_stop()
    {
        var settings = new EngineConfiguration();
        var engine = new TradingDecisionEngine(settings with { Strategies = [settings.Strategies[0] with { MinimumConfirmingConstituents = 2 }] });
        Assert.False(Assert.Single(engine.EvaluateIndex(EngineFixture.Entry()).Candidates).Eligible);
        var position = EngineFixture.Position();
        var decision = engine.EvaluatePosition(position with { OptionQuote = EngineFixture.Quote(position.Position.Contract.Id, position.AsOfUtc, 190m),
            FuturesBars = [], FuturesFeatures = [], Constituents = [], NetLiquidation = null });
        Assert.Equal(PositionAction.EXIT, decision.Action);
        Assert.Contains("GROSS_CASH_STOP", decision.ReasonCodes);
    }

    [Fact]
    public void Unknown_cash_does_not_masquerade_as_absence_of_a_directional_crossing()
    {
        var input = EngineFixture.Entry();
        var result = new TradingDecisionEngine(new()).EvaluateIndex(input with { Portfolio = input.Portfolio with { AvailableCash = null } });
        Assert.Equal(DirectionalStatus.UP, result.DirectionalEvidence!.Status);
        Assert.True(result.DirectionalEvidence.MeetsDirectionalRules);
        Assert.False(Assert.Single(result.Candidates).Eligible);
        Assert.Contains("AVAILABLE_CASH", Assert.Single(result.Candidates).ReasonCodes);
    }
}
