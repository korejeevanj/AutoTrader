using System.Text.Json;
using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class ConfigurationAndAllocationTests
{
    [Fact]
    public void Settings_round_trip_with_named_defaults_and_reject_typos_or_invalid_limits()
    {
        var defaults = new EngineConfiguration();
        var parsed = EngineConfiguration.FromJson(defaults.ToJson());
        Assert.Equal(defaults.ToJson(), parsed.ToJson());
        Assert.Equal(200m, parsed.Strategies[0].Risk.GrossCashStop);
        Assert.Equal(TimeSpan.FromMilliseconds(250), parsed.PaperSimulation.ExecutionDelay);
        Assert.Throws<JsonException>(() => EngineConfiguration.FromJson("{\"GrossStpo\": 100}"));
        Assert.Throws<ArgumentException>(() => new TradingDecisionEngine(defaults with
            { Strategies = [new() { IndexId = "BANKNIFTY", Lots = 0 }] }));
        Assert.Throws<ArgumentException>(() => EngineConfiguration.FromJson("{\"Portfolio\": null}"));
    }

    [Fact]
    public void Editable_example_matches_typed_defaults_and_changed_settings_cannot_reuse_old_state()
    {
        var profilePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/Trading.Engine/engine.paper.example.json"));
        var example = EngineConfiguration.FromJson(File.ReadAllText(profilePath));
        Assert.Equal(new EngineConfiguration().ToJson(), example.ToJson());
        var input = EngineFixture.Entry();
        var original = new TradingDecisionEngine(example);
        var state = original.EvaluateIndex(input).State;
        var changed = new TradingDecisionEngine(example with { Portfolio = example.Portfolio with { MaximumManualAttempts = 2 } });
        Assert.NotEqual(original.ConfigurationFingerprint, changed.ConfigurationFingerprint);
        Assert.Throws<ArgumentException>(() => changed.EvaluateIndex(input, state));
    }

    [Fact]
    public void Engine_freezes_configuration_collections_and_uses_configured_quantities()
    {
        var profiles = new[] { new IndexStrategyConfiguration { IndexId = "BANKNIFTY" } };
        var engine = new TradingDecisionEngine(new() { Strategies = profiles });
        profiles[0] = profiles[0] with { Lots = 2 };
        Assert.Equal(25, Assert.Single(engine.EvaluateIndex(EngineFixture.Entry()).Candidates).Units);
        var huge = new TradingDecisionEngine(new() { Strategies = [profiles[0] with { Lots = int.MaxValue }] });
        Assert.Contains("WHOLE_LOT_QUANTITY", Assert.Single(huge.EvaluateIndex(EngineFixture.Entry()).Candidates).ReasonCodes);
    }

    [Fact]
    public void Cross_index_allocation_uses_explicit_priority_not_enumeration_or_scores()
    {
        var engine = new TradingDecisionEngine(new()
        {
            Strategies = [new() { IndexId = "BANKNIFTY" }, new() { IndexId = "NIFTY" }],
            Portfolio = new() { IndexPriority = ["NIFTY", "BANKNIFTY"] }
        });
        var bank = EngineFixture.Entry();
        var nifty = EngineFixture.Entry("NIFTY");
        var bankResult = engine.EvaluateIndex(bank);
        var niftyResult = engine.EvaluateIndex(nifty);
        var first = engine.Allocate(bank.Identity, bank.AsOfUtc, bank.Session, bank.Portfolio, [bankResult, niftyResult]);
        var second = engine.Allocate(bank.Identity, bank.AsOfUtc, bank.Session, bank.Portfolio, [niftyResult, bankResult]);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal("NIFTY", Assert.Single(first.Decisions, candidate => candidate.Action == EntryAction.ENTER).IndexId);
        Assert.Equal(EntryAction.WATCH, first.Decisions.Single(candidate => candidate.IndexId == "BANKNIFTY").Action);
    }

    [Fact]
    public void Multiple_reservations_require_shared_risk_aggregate_cash_and_correct_bindings()
    {
        var engine = new TradingDecisionEngine(new()
        {
            Strategies = [new() { IndexId = "BANKNIFTY" }, new() { IndexId = "NIFTY" }],
            Portfolio = new() { MaximumActiveOrPendingEntries = 2 }
        });
        var bank = EngineFixture.Entry() with { Portfolio = EngineFixture.Entry().Portfolio with { AvailableCash = 11000m } };
        var nifty = EngineFixture.Entry("NIFTY") with { Portfolio = bank.Portfolio };
        var results = new[] { engine.EvaluateIndex(bank), engine.EvaluateIndex(nifty) };
        var noCalculator = engine.Allocate(bank.Identity, bank.AsOfUtc, bank.Session, bank.Portfolio, results);
        Assert.Equal(EntryAction.INSUFFICIENT_DATA, noCalculator.Decisions.Single(candidate => candidate.IndexId == "NIFTY").Action);
        var costly = engine.Allocate(bank.Identity, bank.AsOfUtc, bank.Session, bank.Portfolio, results,
            reservationAssessor: new DeclaredReservationAssessor(11000.01m));
        Assert.Single(costly.Decisions, candidate => candidate.Action == EntryAction.ENTER);
        var affordable = engine.Allocate(bank.Identity, bank.AsOfUtc, bank.Session, bank.Portfolio, results,
            reservationAssessor: new DeclaredReservationAssessor(10080m));
        Assert.Equal(2, affordable.Decisions.Count(candidate => candidate.Action == EntryAction.ENTER));
        var mismatched = engine.Allocate(bank.Identity, bank.AsOfUtc, bank.Session, bank.Portfolio, results,
            reservationAssessor: new DeclaredReservationAssessor(10080m, wrongVersion: true));
        Assert.Single(mismatched.Decisions, candidate => candidate.Action == EntryAction.ENTER);
    }

    [Fact]
    public void Pending_entries_and_accepted_unfilled_attempts_consume_shared_limits()
    {
        var engine = new TradingDecisionEngine(new());
        var input = EngineFixture.Entry();
        var pending = input with { Portfolio = input.Portfolio with { ActiveOrPendingEntries = [new("pending-1", "NIFTY", EntryOrigin.MANUAL_TEST)] } };
        Assert.Contains("PORTFOLIO_CAPACITY", Assert.Single(engine.EvaluateIndex(pending).Candidates).ReasonCodes);
        var used = input with { Portfolio = input.Portfolio with { AcceptedAttempts = Enumerable.Range(1, 3).Select(i =>
            new AcceptedEntryAttempt($"intent-{i}", "BANKNIFTY", input.Session.Id, $"setup-{i}", EntryOrigin.STRATEGY, input.AsOfUtc)).ToArray() } };
        Assert.Contains("PORTFOLIO_ATTEMPTS", Assert.Single(engine.EvaluateIndex(used).Candidates).ReasonCodes);
    }

    [Fact]
    public void Pause_is_latched_after_threshold_but_never_from_future_portfolio_evidence()
    {
        var engine = new TradingDecisionEngine(new());
        var input = EngineFixture.Entry();
        var loss = input.Portfolio with { NetLiquidationSessionPnl = -1200m,
            ActiveOrPendingEntries = [new("open", "BANKNIFTY", EntryOrigin.STRATEGY)] };
        var paused = engine.Allocate(input.Identity, input.AsOfUtc, input.Session, loss, []);
        Assert.True(paused.State.PauseLatched);
        Assert.True(paused.RequestPositionClosure);
        var recovered = engine.Allocate(input.Identity, input.AsOfUtc, input.Session,
            loss with { NetLiquidationSessionPnl = 100m }, [], paused.State);
        Assert.True(recovered.State.PauseLatched);
        var future = engine.Allocate(input.Identity, input.AsOfUtc, input.Session,
            loss with { AvailableAtUtc = input.AsOfUtc.AddSeconds(1) }, []);
        Assert.False(future.State.PauseLatched);
        Assert.False(future.RequestPositionClosure);
    }

    [Fact]
    public void Identical_intent_duplicates_count_once_but_conflicting_duplicates_are_quarantined()
    {
        var engine = new TradingDecisionEngine(new());
        var input = EngineFixture.Entry();
        var attempt = new AcceptedEntryAttempt("old-intent", "BANKNIFTY", input.Session.Id, "old-setup", EntryOrigin.STRATEGY, input.AsOfUtc);
        var identical = input with { Portfolio = input.Portfolio with { AcceptedAttempts = [attempt, attempt, attempt] } };
        Assert.True(Assert.Single(engine.EvaluateIndex(identical).Candidates).Eligible);
        var conflict = attempt with { Origin = EntryOrigin.MANUAL_TEST };
        foreach (var attempts in new[] { new[] { attempt, conflict }, new[] { conflict, attempt } })
        {
            var bad = input with { Portfolio = input.Portfolio with { AcceptedAttempts = attempts } };
            Assert.Equal(EntryAction.INSUFFICIENT_DATA, Assert.Single(engine.EvaluateIndex(bad).Candidates).Action);
        }
    }

    [Fact]
    public void Configuration_or_run_changes_cannot_regenerate_consumed_crossings_or_manual_requests()
    {
        var defaults = new EngineConfiguration();
        var original = new TradingDecisionEngine(defaults);
        var changed = new TradingDecisionEngine(defaults with { Portfolio = defaults.Portfolio with { MaximumManualAttempts = 2 } });
        var input = EngineFixture.Entry();
        var crossing = original.EvaluateIndex(input).State.Setup!;
        var usedCrossing = input with { Identity = input.Identity with { RunId = "restarted-run" },
            Portfolio = input.Portfolio with { AcceptedAttempts =
                [new("old-intent", input.Index.Id, input.Session.Id, crossing.Id, EntryOrigin.STRATEGY, input.AsOfUtc)] } };
        Assert.Contains("SETUP_NOT_CONSUMED", Assert.Single(changed.EvaluateIndex(usedCrossing).Candidates).ReasonCodes);
        var manual = input with { Origin = EntryOrigin.MANUAL_TEST, ManualRequestId = "same-request", ManualContractId = "option-a",
            Candidates = [input.Candidates[0] with { Risk = input.Candidates[0].Risk! with { SignalBarId = null, StructuralFeatureId = null } }] };
        var request = Assert.Single(original.EvaluateIndex(manual).Candidates);
        var usedManual = manual with { Identity = manual.Identity with { RunId = "restarted-run" },
            Portfolio = manual.Portfolio with { AcceptedAttempts =
                [new("old-manual", manual.Index.Id, manual.Session.Id, request.SetupId, EntryOrigin.MANUAL_TEST, manual.AsOfUtc)] } };
        Assert.Contains("SETUP_NOT_CONSUMED", Assert.Single(changed.EvaluateIndex(usedManual).Candidates).ReasonCodes);
    }

    private sealed class DeclaredReservationAssessor(decimal cash, bool wrongVersion = false) : IPortfolioReservationAssessor
    {
        public ReservationAssessment Assess(PortfolioEvidence portfolio, string configurationVersion,
            IReadOnlyList<EntryRiskEvidence> proposedReservations, DateTimeOffset asOfUtc) =>
            new(wrongVersion ? "obsolete" : portfolio.Version, configurationVersion,
                proposedReservations.Select(risk => risk.Id).ToArray(), cash, "shared-risk-contract-fixture");
    }
}
