using System.Text.Json;
using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class EvidenceIntegrityTests
{
    private readonly TradingDecisionEngine engine = new(new());

    [Theory]
    [InlineData(ProfitExitMode.FixedNetTarget, 500)]
    [InlineData(ProfitExitMode.TrailingNetProfit, 800)]
    public void Unreconciled_net_marks_cannot_trigger_profit_exits_or_ratchet_peak(ProfitExitMode mode, decimal net)
    {
        // Fresh bid equals entry: gross is zero; ₹20 per side means net must be -₹40.
        var input = EngineFixture.Position();
        input = input with { Position = input.Position with { FrozenStrategy = input.Position.FrozenStrategy with
            { Position = input.Position.FrozenStrategy.Position with { ProfitExit = new() { Mode = mode } } } },
            NetLiquidation = input.NetLiquidation! with { NetPnl = net } };

        var decision = engine.EvaluatePosition(input);

        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action);
        Assert.Equal(0m, decision.GrossLiquidationPnl);
        Assert.Null(decision.NetLiquidationPnl);
        Assert.Null(decision.State.PeakNetLiquidationPnl);
        Assert.Null(decision.State.ProfitTrailThreshold);
        Assert.Contains("NET_LIQUIDATION_UNKNOWN", decision.ReasonCodes);
    }

    [Fact]
    public void Inconsistent_progress_cannot_suppress_a_no_progress_time_exit()
    {
        var input = EngineFixture.Position();
        var forged = engine.EvaluatePosition(input with { Progress = input.Progress! with { ProgressR = 10m } });
        Assert.Null(forged.State.MaximumFavorableProgressR);
        Assert.Contains("UNDERLYING_PROGRESS_UNKNOWN", forged.ReasonCodes);

        var time = input.Position.EnteredAtUtc.AddMinutes(20);
        var timer = input with { AsOfUtc = time, OptionQuote = null, FuturesPrice = null,
            Portfolio = input.Portfolio with { AvailableAtUtc = time } };
        Assert.Contains("TIME_NO_PROGRESS", engine.EvaluatePosition(timer, forged.State).ReasonCodes);
    }

    [Fact]
    public void Later_entry_state_cannot_be_reused_to_change_an_earlier_decision()
    {
        var input = EngineFixture.Entry();
        var later = engine.EvaluateIndex(input with { AsOfUtc = input.AsOfUtc.AddMilliseconds(100) });
        Assert.Throws<ArgumentException>(() => engine.EvaluateIndex(input, later.State));
    }

    [Theory]
    [InlineData("bidAsk")]
    [InlineData("event")]
    [InlineData("underlying")]
    public void Observation_after_its_receipt_is_not_fresh_evidence(string field)
    {
        var input = EngineFixture.Entry();
        var candidate = input.Candidates[0];
        var quote = candidate.Quote!;
        var underlying = input.FuturesPrice!;
        input = field switch
        {
            "bidAsk" => input with { Candidates = [candidate with { Quote = quote with
                { BidAskObservedAtUtc = quote.ReceivedAtUtc!.Value.AddMilliseconds(50) } }] },
            "event" => input with { Candidates = [candidate with { Quote = quote with
                { EventTimeUtc = quote.ReceivedAtUtc!.Value.AddMilliseconds(50) } }] },
            _ => input with { FuturesPrice = underlying with
                { LastTradeTimeUtc = underlying.ReceivedAtUtc!.Value.AddMilliseconds(50) } }
        };
        var decision = Assert.Single(engine.EvaluateIndex(input).Candidates);
        Assert.False(decision.Eligible);
        Assert.Null(decision.Rank);
        Assert.Contains(field == "underlying" ? "FUTURES_PRICE_FRESHNESS" : "OPTION_QUOTE_FRESH", decision.ReasonCodes);
    }

    [Fact]
    public void Position_state_cannot_be_reused_with_changed_fill_or_frozen_policy_under_the_same_id()
    {
        var input = EngineFixture.Position();
        var previous = engine.EvaluatePosition(input).State;
        Assert.Throws<ArgumentException>(() => engine.EvaluatePosition(input with
            { Position = input.Position with { EntryFillVersion = "different-fill" } }, previous));
        var changedPolicy = input.Position.FrozenStrategy with { Risk = input.Position.FrozenStrategy.Risk with { GrossCashStop = 300m } };
        Assert.Throws<ArgumentException>(() => engine.EvaluatePosition(input with
            { Position = input.Position with { FrozenStrategy = changedPolicy } }, previous));
    }

    [Fact]
    public void Allocation_cannot_mix_distinct_market_evidence_snapshots_at_the_same_timestamp()
    {
        var input = EngineFixture.Entry();
        var earlierSnapshot = engine.EvaluateIndex(input);
        var correctedIdentity = input.Identity with { EvidenceVersion = "corrected-same-time-market-evidence" };
        Assert.Throws<ArgumentException>(() => engine.Allocate(correctedIdentity, input.AsOfUtc,
            input.Session, input.Portfolio, [earlierSnapshot]));
    }

    [Fact]
    public void Future_session_pause_state_cannot_be_borrowed_by_an_earlier_allocation()
    {
        var input = EngineFixture.Entry();
        var futureTime = input.AsOfUtc.AddSeconds(1);
        var future = engine.Allocate(input.Identity, futureTime, input.Session,
            input.Portfolio with { AvailableAtUtc = futureTime, NetLiquidationSessionPnl = -1200m }, []);
        Assert.True(future.State.PauseLatched);
        Assert.Throws<ArgumentException>(() => engine.Allocate(input.Identity, input.AsOfUtc,
            input.Session, input.Portfolio, [], future.State));
    }

    [Fact]
    public void Conflicting_candidate_identities_remain_inspectable_rejections_in_candle_research()
    {
        var input = EngineFixture.Entry();
        var candidate = input.Candidates[0];
        input = input with { Candidates = [candidate, candidate with { Contract = candidate.Contract with { Strike = 150m } }] };
        var strict = engine.EvaluateAllCandidates(input);
        Assert.All(strict.Candidates, decision => Assert.Contains("UNIQUE_CANDIDATES", decision.ReasonCodes));

        var research = engine.AssessCandleResearch(input, strict, []);
        Assert.Equal(2, research.Count);
        Assert.All(research, decision =>
        {
            Assert.False(decision.HypotheticallyEligible);
            Assert.Null(decision.Rank);
            Assert.Contains("UNIQUE_CANDIDATES", decision.ReasonCodes);
        });
    }

    [Fact]
    public void Manual_setup_bypass_does_not_bypass_the_configured_entry_window()
    {
        var input = EngineFixture.Entry();
        input = input with { AsOfUtc = input.Session.At(new(11, 30)), Origin = EntryOrigin.MANUAL_TEST,
            ManualRequestId = "outside-entry-window", ManualContractId = input.Candidates[0].Contract.Id };
        var decision = Assert.Single(engine.EvaluateIndex(input).Candidates);
        Assert.Contains("ENTRY_WINDOW", decision.ReasonCodes);
        Assert.False(decision.Eligible);
    }

    [Fact]
    public void Equivalent_decimal_scales_and_timestamp_offsets_preserve_position_state_and_hard_stops()
    {
        var input = EngineFixture.Position();
        var previous = engine.EvaluatePosition(input).State;
        var equivalent = input.Position with { EntryPrice = 200.00m,
            EnteredAtUtc = input.Position.EnteredAtUtc.ToOffset(TimeSpan.FromHours(5.5)),
            FrozenStrategy = input.Position.FrozenStrategy with { Risk = input.Position.FrozenStrategy.Risk with
                { GrossCashStop = 200.00m } } };
        var stopped = engine.EvaluatePosition(input with { Position = equivalent,
            OptionQuote = input.OptionQuote! with { Bid = 192m }, NetLiquidation = null }, previous);
        Assert.Equal(previous.PositionFingerprint, stopped.State.PositionFingerprint);
        Assert.Equal(PositionAction.EXIT, stopped.Action);
        Assert.Contains("GROSS_CASH_STOP", stopped.ReasonCodes);
    }

    [Fact]
    public void Literal_legacy_position_state_requires_reconstruction_instead_of_silent_rebinding()
    {
        const string legacyJson = """
            {"PositionId":"position-1","MaximumFavorableProgressR":10,
             "PeakNetLiquidationPnl":800,"ProfitTrailArmed":true,"ProfitTrailThreshold":600}
            """;
        var legacy = JsonSerializer.Deserialize<PositionPolicyState>(legacyJson)!;
        var input = EngineFixture.Position();
        var error = Assert.Throws<ArgumentException>(() => engine.EvaluatePosition(input, legacy));
        Assert.Contains("reconstruct", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => engine.EvaluatePosition(input with
            { Position = input.Position with { EntryFillVersion = "changed-fill" } }, legacy));
    }

    [Fact]
    public void Fractional_fees_and_repeating_progress_reconcile_without_rounding_tolerance()
    {
        var input = EngineFixture.Position();
        input = input with
        {
            Position = input.Position with { ActualEntryFees = 7.13m, InitialUnderlyingRisk = 3m },
            NetLiquidation = input.NetLiquidation! with { ActualEntryFees = 7.13m, ProjectedExitFees = 10.89m, NetPnl = -18.02m },
            FuturesPrice = input.FuturesPrice! with { Price = 106m },
            Progress = input.Progress! with { InitialRisk = 3m, ProgressR = 0.3333333333333333333333333333m }
        };
        var decision = engine.EvaluatePosition(input);
        Assert.Equal(PositionAction.HOLD, decision.Action);
        Assert.Equal(-18.02m, decision.NetLiquidationPnl);
        Assert.Equal(0.3333333333333333333333333333m, decision.State.MaximumFavorableProgressR);
        var offByOnePaisa = engine.EvaluatePosition(input with
            { NetLiquidation = input.NetLiquidation! with { NetPnl = -18.01m } });
        Assert.Null(offByOnePaisa.NetLiquidationPnl);
    }
}
