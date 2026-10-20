using Trading.Domain;
using Trading.Risk;

namespace Trading.Portfolio;

// Trusted engine-host output, never an HTTP request shape. The host rechecks the full
// decision/risk evidence against the filling quote; the broker enforces execution/cash.
public sealed record QuoteEntryApproval(string Id, string SetupId, string Origin, HistoricalInstrument Contract,
    DateTimeOffset AcceptedAtUtc, long AcceptedSequence, decimal ReservedCash, decimal MaximumNetRisk,
    decimal GrossStop, decimal UnderlyingEntry, decimal? StructuralStop, decimal UnderlyingRisk,
    string FrozenPolicyJson, ResearchFeeSchedule Fees, bool Synthetic = false);
public sealed record QuoteFillAssessment(decimal NetRisk, ResearchFeeSchedule Fees);
public sealed record QuoteOpenPosition(QuoteEntryApproval Approval, DateTimeOffset EnteredAtUtc,
    decimal EntryPrice, int Units, ResearchFeeBreakdown EntryFees, DateTimeOffset? ExitRequestedAtUtc = null,
    long? ExitRequestedSequence = null, string? ExitReason = null);
public sealed record QuotePaperTrade(string Id, string ContractId, string Origin, DateTimeOffset EntryTimeUtc,
    DateTimeOffset ExitTimeUtc, decimal EntryPrice, decimal ExitPrice, int Units,
    ResearchFeeBreakdown EntryFees, ResearchFeeBreakdown ExitFees, decimal GrossPnl, decimal NetPnl,
    string ExitReason, decimal GrossStopOverrun);
public sealed record QuotePaperEvent(string Id, DateTimeOffset TimeUtc, string Kind, string IntentId, string Detail);
public sealed record QuotePaperState(ResearchCashLedger Ledger, QuoteEntryApproval? Pending,
    QuoteOpenPosition? Position, IReadOnlyList<QuoteEntryApproval> Attempts,
    IReadOnlyList<QuotePaperTrade> Trades, IReadOnlyList<QuotePaperEvent> Events);

public sealed class QuotePaperBroker
{
    public QuotePaperState State { get; private set; }
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan EntryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumQuoteAge { get; init; } = TimeSpan.FromSeconds(2);
    public QuotePaperBroker(decimal capital) : this(new QuotePaperState(new(capital, capital), null, null, [], [], [])) { }
    public QuotePaperBroker(QuotePaperState state) => State = state;

    public void Accept(QuoteEntryApproval approval)
    {
        var previous = State.Attempts.SingleOrDefault(item => item.Id == approval.Id);
        if (previous is not null)
        {
            if (previous.Contract.Id != approval.Contract.Id || previous.Origin != approval.Origin || previous.SetupId != approval.SetupId)
                throw new InvalidOperationException("CONFLICTING_INTENT_ID");
            return;
        }
        if (State.Pending is not null || State.Position is not null) throw new InvalidOperationException("PAPER_CAPACITY");
        if (approval.ReservedCash <= 0 || approval.ReservedCash > State.Ledger.Cash || !approval.Contract.Verified
            || approval.Contract.LotSize is not > 0 || approval.Contract.TickSize is not > 0)
            throw new InvalidOperationException("PAPER_APPROVAL_INVALID");
        State = State with { Pending = approval, Attempts = [.. State.Attempts, approval] };
        Emit(approval.AcceptedAtUtc, "INTENT_ACCEPTED", approval.Id, approval.Origin);
    }

    public void CancelPending(DateTimeOffset now, string reason)
    {
        if (State.Pending is not { } intent) return;
        State = State with { Pending = null }; Emit(now, "ENTRY_CANCELED", intent.Id, reason);
    }

    public void RequestExit(DateTimeOffset now, long sequence, string reason)
    {
        if (State.Position is not { ExitRequestedAtUtc: null } position) return;
        State = State with { Position = position with { ExitRequestedAtUtc = now, ExitRequestedSequence = sequence, ExitReason = reason } };
        Emit(now, "EXIT_REQUESTED", position.Approval.Id, reason);
    }

    public void Advance(DateTimeOffset now, long sequence, bool entriesHealthy, DateTimeOffset entryEnd, DateTimeOffset sessionExit)
    {
        if (State.Pending is { } pending && (!entriesHealthy || now >= pending.AcceptedAtUtc + EntryTimeout || now >= entryEnd))
            CancelPending(now, !entriesHealthy ? "ENTRIES_BLOCKED" : "ENTRY_TIMEOUT_OR_WINDOW_END");
        if (now >= sessionExit) RequestExit(now, sequence, "SESSION_EXIT");
    }

    public bool Fresh(MarketTick tick, DateTimeOffset now) => tick.ExchangeTimeUtc is { } observed
        && tick.ReceivedAtUtc <= now && observed <= now && now - observed <= MaximumQuoteAge
        && now - tick.ReceivedAtUtc <= MaximumQuoteAge;

    public void OnQuote(MarketTick tick, DateTimeOffset now, bool entriesHealthy,
        Func<QuoteEntryApproval, MarketTick, QuoteFillAssessment?> reassess)
    {
        if (!Fresh(tick, now)) return;
        if (State.Position is { } position && position.Approval.Contract.InstrumentToken == tick.InstrumentToken)
        {
            var bid = tick.Bids.FirstOrDefault();
            var mark = IndicativePlanning.ExecutablePrice(bid?.Price, position.Approval.Contract.TickSize!.Value,
                position.Approval.Contract.TickSize.Value, false);
            if (mark is { } marked && IndicativePlanning.LongGrossPnl(position.EntryPrice, marked, position.Units) <= -position.Approval.GrossStop)
                RequestExit(now, tick.Sequence, "GROSS_CASH_STOP");
            position = State.Position!;
            if (position.ExitRequestedAtUtc is { } requested && tick.ReceivedAtUtc >= requested + Delay
                && tick.ReceivedAtUtc > requested && tick.Sequence > position.ExitRequestedSequence
                && bid?.Quantity >= position.Units && mark is { } fill && position.Approval.Fees.Applies(now, position.Approval.Fees.ProfileId, position.Approval.Synthetic))
            {
                var fees = HistoricalResearchCalculations.Fees(fill, position.Units, false, position.Approval.Fees);
                var gross = IndicativePlanning.LongGrossPnl(position.EntryPrice, fill, position.Units)!.Value;
                var net = HistoricalResearchCalculations.Net(position.EntryPrice, fill, position.Units, position.EntryFees.Total, fees.Total);
                var trade = new QuotePaperTrade(position.Approval.Id, position.Approval.Contract.Id, position.Approval.Origin,
                    position.EnteredAtUtc, now, position.EntryPrice, fill, position.Units, position.EntryFees, fees, gross, net,
                    position.ExitReason!, HistoricalResearchCalculations.GrossStopOverrun(gross, position.Approval.GrossStop));
                State = State with { Position = null, Trades = [.. State.Trades, trade], Ledger = HistoricalResearchCalculations.Exit(State.Ledger,
                    position.EntryPrice, fill, position.Units, position.EntryFees.Total, fees.Total) };
                Emit(now, "EXIT_FILLED", position.Approval.Id, position.ExitReason!);
            }
        }
        if (!entriesHealthy || State.Pending is not { } intent || tick.InstrumentToken != intent.Contract.InstrumentToken
            || tick.ReceivedAtUtc < intent.AcceptedAtUtc + Delay || tick.ReceivedAtUtc <= intent.AcceptedAtUtc
            || tick.Sequence <= intent.AcceptedSequence) return;
        if (now >= intent.AcceptedAtUtc + EntryTimeout) { CancelPending(now, "ENTRY_TIMEOUT"); return; }
        var units = intent.Contract.LotSize!.Value;
        var ask = tick.Asks.FirstOrDefault(); var bestBid = tick.Bids.FirstOrDefault();
        if (ask?.Quantity < units || bestBid?.Quantity < units || ask is null || bestBid is null
            || bestBid.Price <= 0 || ask.Price < bestBid.Price) return;
        var assessment = reassess(intent, tick);
        var entry = IndicativePlanning.ExecutablePrice(ask.Price, intent.Contract.TickSize!.Value, intent.Contract.TickSize.Value, true);
        if (assessment is null || assessment.NetRisk > intent.MaximumNetRisk || assessment.NetRisk <= 0 || entry is null
            || now < intent.Contract.ValidFromUtc || now >= intent.Contract.ValidToUtc || intent.Contract.AvailableAtUtc > now
            || !assessment.Fees.Applies(now, intent.Fees.ProfileId, intent.Synthetic))
        { CancelPending(now, "PREFILL_RISK_OR_CONTRACT"); return; }
        var entryFees = HistoricalResearchCalculations.Fees(entry.Value, units, true, assessment.Fees);
        var premium = IndicativePlanning.LongPremiumCash([(entry.Value, units)]);
        if (premium is null || premium + entryFees.Total > State.Ledger.Cash)
        { CancelPending(now, "PREFILL_CASH"); return; }
        State = State with { Pending = null, Position = new(intent, now, entry.Value, units, entryFees),
            Ledger = HistoricalResearchCalculations.Enter(State.Ledger, entry.Value, units, entryFees.Total) };
        Emit(now, "ENTRY_FILLED", intent.Id, "ASK_PLUS_ONE_TICK");
    }

    private void Emit(DateTimeOffset time, string kind, string id, string detail) => State = State with
        { Events = [.. State.Events, new($"{id}:{kind}", time, kind, id, detail)] };
}
