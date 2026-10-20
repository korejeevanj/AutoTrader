using System.Text.Json;
using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class EntryPolicyTests
{
    private readonly TradingDecisionEngine engine = new(new());

    [Fact]
    public void Available_confirmed_crossing_is_eligible_but_only_allocation_can_enter()
    {
        var input = EngineFixture.Entry();
        var result = engine.EvaluateIndex(input);
        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.Eligible);
        Assert.Equal(EntryAction.WATCH, candidate.Action);
        Assert.Equal(25, candidate.Units);
        Assert.Equal(1, candidate.Rank);
        var allocation = engine.Allocate(input.Identity, input.AsOfUtc, input.Session, input.Portfolio, [result]);
        Assert.Equal(EntryAction.ENTER, Assert.Single(allocation.Decisions).Action);
        Assert.Equal(input.Portfolio.Version, allocation.ExpectedPortfolioVersion);
        Assert.Empty(input.Portfolio.AcceptedAttempts); // recommendations have no execution side effects
    }

    [Fact]
    public void Completed_but_unavailable_breakout_cannot_influence_decision()
    {
        var input = EngineFixture.Entry();
        var before = input with { AsOfUtc = input.FuturesBars[^1].EndUtc,
            Portfolio = input.Portfolio with { AvailableAtUtc = input.FuturesBars[^1].EndUtc } };
        Assert.False(Assert.Single(engine.EvaluateIndex(before).Candidates).Eligible);
        Assert.True(Assert.Single(engine.EvaluateIndex(input).Candidates).Eligible);
    }

    [Fact]
    public void Future_bars_features_and_constituent_observations_do_not_change_earlier_result()
    {
        var input = EngineFixture.Entry();
        var future = EngineFixture.Extend(input, 90m);
        var contaminated = input with { FuturesBars = future.FuturesBars,
            FuturesFeatures = future.FuturesFeatures, Constituents = input.Constituents.Concat(future.Constituents).ToArray() };
        Assert.Equal(JsonSerializer.Serialize(engine.EvaluateIndex(input)), JsonSerializer.Serialize(engine.EvaluateIndex(contaminated)));
    }

    [Fact]
    public void Missing_bank_observation_cannot_qualify_even_with_seventy_percent_confirming()
    {
        var input = EngineFixture.Entry();
        input = input with { ConstituentDefinitions = input.ConstituentDefinitions! with
            { Members = [new("bank-a", 70m), new("bank-b", 30m)] }, Constituents = [input.Constituents[0]] };
        var decision = Assert.Single(engine.EvaluateIndex(input).Candidates);
        Assert.Equal(EntryAction.INSUFFICIENT_DATA, decision.Action);
        Assert.Contains("MISSING_CONSTITUENT_EVIDENCE", decision.ReasonCodes);
        Assert.Contains(decision.Gates, gate => gate.Code == "MISSING_CONSTITUENT_EVIDENCE" && gate.Observed == 30m);
    }

    [Fact]
    public void Equality_and_just_below_threshold_do_not_confirm()
    {
        var input = EngineFixture.Entry();
        var below = input with { ConstituentDefinitions = input.ConstituentDefinitions! with
            { Members = [new("bank-a", 64.99m), new("bank-b", 35.01m)] } };
        Assert.Contains("CONSTITUENT_CONFIRMATION", Assert.Single(engine.EvaluateIndex(below).Candidates).ReasonCodes);
        var equal = input with { Constituents = [input.Constituents[0] with { Close = 100m }, input.Constituents[1]] };
        Assert.False(Assert.Single(engine.EvaluateIndex(equal).Candidates).Eligible);
    }

    [Fact]
    public void Gaps_conflicting_duplicates_and_old_constituent_periods_are_insufficient()
    {
        var input = EngineFixture.Entry();
        var gap = input with { FuturesBars = input.FuturesBars.Where(bar => bar.Id != "bar-3").ToArray() };
        Assert.False(Assert.Single(engine.EvaluateIndex(gap).Candidates).Eligible);
        var duplicate = input with { FuturesBars = input.FuturesBars.Append(input.FuturesBars[3] with { Close = 101m }).ToArray() };
        Assert.Contains("INVALID_OR_CONFLICTING_BARS", Assert.Single(engine.EvaluateIndex(duplicate).Candidates).ReasonCodes);
        var oldBanks = input with { Constituents = EngineFixture.Banks(input.FuturesBars[^2]) };
        Assert.Equal(EntryAction.INSUFFICIENT_DATA, Assert.Single(engine.EvaluateIndex(oldBanks).Candidates).Action);
    }

    [Fact]
    public void Features_cannot_claim_future_inputs_or_previous_session_history()
    {
        var input = EngineFixture.Entry();
        var feature = input.FuturesFeatures[^1] with { InputBarIds = input.FuturesFeatures[^1].InputBarIds.Append("future-bar").ToArray() };
        var invalid = input with { FuturesFeatures = input.FuturesFeatures.SkipLast(1).Append(feature).ToArray() };
        Assert.Contains("FUTURES_FEATURE_LINEAGE", Assert.Single(engine.EvaluateIndex(invalid).Candidates).ReasonCodes);
        var old = input.FuturesBars[0] with { Id = "old", SessionId = "previous", StartUtc = input.FuturesBars[0].StartUtc.AddDays(-1),
            EndUtc = input.FuturesBars[0].EndUtc.AddDays(-1), AvailableAtUtc = input.FuturesBars[0].AvailableAtUtc.AddDays(-1), High = 99999m };
        Assert.Equal(JsonSerializer.Serialize(engine.EvaluateIndex(input)),
            JsonSerializer.Serialize(engine.EvaluateIndex(input with { FuturesBars = input.FuturesBars.Append(old).ToArray() })));
    }

    [Fact]
    public void Quote_age_depth_validity_and_known_failure_precede_ranking()
    {
        var input = EngineFixture.Entry();
        var first = input.Candidates[0];
        var cheapButStale = first with { Quote = first.Quote! with
            { BidAskObservedAtUtc = input.AsOfUtc.AddSeconds(-3) }, Risk = first.Risk! with { SpreadPercent = 0.01m } };
        var decision = Assert.Single(engine.EvaluateIndex(input with { Candidates = [cheapButStale] }).Candidates);
        Assert.Equal(EntryAction.SKIP, decision.Action);
        Assert.Null(decision.Rank);
        var shallow = first with { Quote = first.Quote! with { AskQuantity = 24 }, Risk = null };
        decision = Assert.Single(engine.EvaluateIndex(input with { Candidates = [shallow] }).Candidates);
        Assert.Equal(EntryAction.SKIP, decision.Action);
        Assert.Contains("WHOLE_LOT_DEPTH", decision.ReasonCodes);
        Assert.Contains("RISK_ASSESSMENT_UNAVAILABLE", decision.ReasonCodes);
        var expiryDay = first with { Contract = first.Contract with { Expiry = input.Session.TradingDate } };
        Assert.Equal(EntryAction.SKIP, Assert.Single(engine.EvaluateIndex(input with { Candidates = [expiryDay] }).Candidates).Action);
    }

    [Theory]
    [InlineData("risk")]
    [InlineData("fees")]
    [InlineData("opportunity")]
    [InlineData("cash")]
    [InlineData("structural")]
    public void Unknown_required_financial_evidence_cannot_become_zero_or_approval(string missing)
    {
        var input = EngineFixture.Entry();
        var candidate = input.Candidates[0];
        var risk = candidate.Risk!;
        risk = missing switch
        {
            "risk" => risk with { CredibleOptionRisk = false },
            "fees" => risk with { RoundTripFees = null },
            "opportunity" => risk with { CredibleOpportunity = false },
            "cash" => risk with { RequiredCash = null },
            _ => risk
        };
        input = input with { Candidates = [candidate with { Risk = risk }] };
        if (missing == "structural") input = input with { FuturesFeatures = input.FuturesFeatures.SkipLast(1)
            .Append(input.FuturesFeatures[^1] with { LongInitialRisk = null }).ToArray() };
        Assert.Equal(EntryAction.INSUFFICIENT_DATA, Assert.Single(engine.EvaluateIndex(input).Candidates).Action);
    }

    [Fact]
    public void Cash_and_net_risk_boundaries_reject_infeasible_whole_lots()
    {
        var input = EngineFixture.Entry();
        var candidate = input.Candidates[0];
        input = input with { Candidates = [candidate with { Risk = candidate.Risk! with { NetRisk = 400m } }],
            Portfolio = input.Portfolio with { AvailableCash = 5040m } };
        Assert.True(Assert.Single(engine.EvaluateIndex(input).Candidates).Eligible);
        var poor = input with { Portfolio = input.Portfolio with { AvailableCash = 5039.99m } };
        Assert.Contains("WHOLE_LOT_CASH", Assert.Single(engine.EvaluateIndex(poor).Candidates).ReasonCodes);
        var risky = input with { Candidates = [input.Candidates[0] with { Risk = input.Candidates[0].Risk! with { NetRisk = 400.01m } }] };
        Assert.Contains("NET_RISK_BUDGET", Assert.Single(engine.EvaluateIndex(risky).Candidates).ReasonCodes);
    }

    [Fact]
    public void Ranking_is_deterministic_and_invalid_high_rank_candidate_cannot_win()
    {
        var input = EngineFixture.Entry();
        var first = input.Candidates[0];
        OptionCandidateEvidence Copy(string id) => first with { Contract = first.Contract with { Id = id },
            Quote = first.Quote! with { Id = $"quote-{id}", InstrumentId = id },
            Risk = first.Risk! with { Id = $"risk-{id}", ContractId = id, QuoteId = $"quote-{id}" } };
        var a = Copy("a");
        var b = Copy("b");
        var invalid = Copy("cheap") with { Quote = Copy("cheap").Quote! with { AskQuantity = 0 },
            Risk = Copy("cheap").Risk! with { SpreadPercent = 0m } };
        var left = engine.EvaluateIndex(input with { Candidates = [b, invalid, a] });
        var right = engine.EvaluateIndex(input with { Candidates = [a, b, invalid] });
        Assert.Equal(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right));
        Assert.Equal(1, left.Candidates.Single(candidate => candidate.ContractId == "a").Rank);
        var allocated = engine.Allocate(input.Identity, input.AsOfUtc, input.Session, input.Portfolio, [left]);
        Assert.Equal("a", Assert.Single(allocated.Decisions, candidate => candidate.Action == EntryAction.ENTER).ContractId);
    }

    [Fact]
    public void Repeated_evaluation_is_idempotent_and_an_accepted_setup_cannot_retry()
    {
        var input = EngineFixture.Entry();
        var first = engine.EvaluateIndex(input);
        var second = engine.EvaluateIndex(input, first.State);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        input = input with { Portfolio = input.Portfolio with { AcceptedAttempts =
            [new("intent-1", input.Index.Id, input.Session.Id, first.State.Setup!.Id, EntryOrigin.STRATEGY, input.AsOfUtc)] } };
        Assert.Contains("SETUP_NOT_CONSUMED", Assert.Single(engine.EvaluateIndex(input, first.State).Candidates).ReasonCodes);
    }

    [Fact]
    public void Outside_closes_do_not_retrigger_but_return_inside_rearms_a_new_crossing()
    {
        var input = EngineFixture.Entry();
        var first = engine.EvaluateIndex(input);
        var outside = engine.EvaluateIndex(EngineFixture.Extend(input, 106m), first.State);
        Assert.Null(outside.State.Setup);
        var rearmedInput = EngineFixture.Extend(input, 100m, 105m);
        var rearmed = engine.EvaluateIndex(rearmedInput, first.State);
        Assert.True(Assert.Single(rearmed.Candidates).Eligible);
        Assert.NotEqual(first.State.Setup!.Id, rearmed.State.Setup!.Id);
    }

    [Fact]
    public void Timer_expires_setup_even_when_next_bar_is_missing()
    {
        var input = EngineFixture.Entry();
        var first = engine.EvaluateIndex(input);
        var later = first.State.Setup!.ExpiresAtUtc;
        var quote = EngineFixture.Quote(input.Candidates[0].Contract.Id, later);
        var expired = input with { AsOfUtc = later, FuturesPrice = EngineFixture.Underlying(later),
            Portfolio = input.Portfolio with { AvailableAtUtc = later },
            Candidates = [input.Candidates[0] with { Quote = quote }] };
        Assert.Null(engine.EvaluateIndex(expired, first.State).State.Setup);
        Assert.False(Assert.Single(engine.EvaluateIndex(expired, first.State).Candidates).Eligible);
    }

    [Fact]
    public void Manual_entry_bypasses_setup_only_and_uses_stable_request_identity()
    {
        var input = EngineFixture.Entry();
        input = input with { Origin = EntryOrigin.MANUAL_TEST, ManualRequestId = "manual-1", ManualContractId = "option-a",
            FuturesBars = [], FuturesFeatures = [], ConstituentDefinitions = null, Constituents = [],
            Portfolio = input.Portfolio with { AutoPaused = true },
            Candidates = [input.Candidates[0] with { Risk = input.Candidates[0].Risk! with { SignalBarId = null, StructuralFeatureId = null } }] };
        var result = engine.EvaluateIndex(input);
        Assert.True(Assert.Single(result.Candidates).Eligible);
        var stale = input with { Candidates = [input.Candidates[0] with { Quote = input.Candidates[0].Quote! with
            { BidAskObservedAtUtc = input.AsOfUtc.AddSeconds(-3) } }] };
        Assert.Equal(EntryAction.SKIP, Assert.Single(engine.EvaluateIndex(stale).Candidates).Action);
        var repeated = input with { Portfolio = input.Portfolio with { AcceptedAttempts =
            [new("manual-intent", input.Index.Id, input.Session.Id, result.Candidates[0].SetupId, EntryOrigin.MANUAL_TEST, input.AsOfUtc)] } };
        Assert.False(Assert.Single(engine.EvaluateIndex(repeated).Candidates).Eligible);
    }

    [Fact]
    public void Other_indices_need_explicit_configuration_and_cannot_reuse_index_state()
    {
        var bank = EngineFixture.Entry();
        var nifty = EngineFixture.Entry("NIFTY");
        Assert.Contains("STRATEGY_NOT_CONFIGURED", Assert.Single(engine.EvaluateIndex(nifty).Candidates).ReasonCodes);
        var configured = new TradingDecisionEngine(new() { Strategies = [new() { IndexId = "NIFTY" }] });
        Assert.True(Assert.Single(configured.EvaluateIndex(nifty).Candidates).Eligible);
        Assert.Throws<ArgumentException>(() => configured.EvaluateIndex(nifty, engine.EvaluateIndex(bank).State));
    }

    [Fact]
    public void Bid_ask_observation_skew_and_replaced_underlying_assessments_block_entry()
    {
        var input = EngineFixture.Entry();
        var candidate = input.Candidates[0];
        var skewed = input with { Candidates = [candidate with { Quote = candidate.Quote! with
            { BidAskObservedAtUtc = input.AsOfUtc.AddMilliseconds(-1900) } }] };
        Assert.Contains("QUOTE_SYNCHRONIZATION", Assert.Single(engine.EvaluateIndex(skewed).Candidates).ReasonCodes);
        var replaced = input with { FuturesPrice = input.FuturesPrice! with { Id = "new-underlying-observation" } };
        Assert.Contains("RISK_ASSESSMENT_UNAVAILABLE", Assert.Single(engine.EvaluateIndex(replaced).Candidates).ReasonCodes);
        var prematurelyCalculated = input with { Candidates = [candidate with { Risk = candidate.Risk! with
            { AvailableAtUtc = input.AsOfUtc.AddSeconds(-1) } }] };
        Assert.Contains("RISK_ASSESSMENT_UNAVAILABLE", Assert.Single(engine.EvaluateIndex(prematurelyCalculated).Candidates).ReasonCodes);
    }

    [Fact]
    public void Manual_late_start_does_not_require_unused_opening_range_history()
    {
        var input = EngineFixture.Entry();
        var manual = input with { Origin = EntryOrigin.MANUAL_TEST, ManualRequestId = "late-start", ManualContractId = "option-a",
            FuturesBars = [input.FuturesBars[^1]], FuturesFeatures = [], Constituents = [], ConstituentDefinitions = null,
            Candidates = [input.Candidates[0] with { Risk = input.Candidates[0].Risk! with { SignalBarId = null, StructuralFeatureId = null } }] };
        Assert.True(Assert.Single(engine.EvaluateIndex(manual).Candidates).Eligible);
    }

    [Fact]
    public void Exclusive_entry_end_and_degraded_recording_block_even_manual_entries()
    {
        var input = EngineFixture.Entry();
        var end = input.Session.At(new(11, 30));
        var outsideWindow = input with { AsOfUtc = end, Portfolio = input.Portfolio with { AvailableAtUtc = end } };
        Assert.Contains("ENTRY_WINDOW", Assert.Single(engine.EvaluateIndex(outsideWindow).Candidates).ReasonCodes);
        var degraded = input with { Portfolio = input.Portfolio with { RecordingComplete = false } };
        Assert.Contains("RECORDER_COMPLETE", Assert.Single(engine.EvaluateIndex(degraded).Candidates).ReasonCodes);
        var missingCurrentFees = input with { Candidates = [input.Candidates[0] with { Risk = input.Candidates[0].Risk! with
            { FeeEffectiveFromUtc = input.AsOfUtc.AddDays(1) } }] };
        Assert.Equal(EntryAction.INSUFFICIENT_DATA, Assert.Single(engine.EvaluateIndex(missingCurrentFees).Candidates).Action);
    }

    [Fact]
    public void Bearish_crossing_requires_puts_and_below_vwap_constituent_weight()
    {
        var input = EngineFixture.Entry();
        var bars = input.FuturesBars.SkipLast(1).Append(EngineFixture.Bar(15, 95m)).ToArray();
        var put = input.Candidates[0] with { Contract = input.Candidates[0].Contract with { Right = OptionRight.Put } };
        var bearish = input with { FuturesBars = bars, FuturesFeatures = EngineFixture.Features(bars),
            Constituents = EngineFixture.Banks(bars[^1], adverse: true), FuturesPrice = EngineFixture.Underlying(input.AsOfUtc, 95m),
            Candidates = [put] };
        var result = Assert.Single(engine.EvaluateIndex(bearish).Candidates);
        Assert.True(result.Eligible);
        Assert.Equal(TradeDirection.Bearish, result.Direction);
        var wrongExpression = bearish with { Candidates = [put with { Contract = put.Contract with { Right = OptionRight.Call } }] };
        Assert.Contains("OPTION_DIRECTION", Assert.Single(engine.EvaluateIndex(wrongExpression).Candidates).ReasonCodes);
    }
}
