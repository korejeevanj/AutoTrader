using Trading.Domain;
using Trading.Risk;

namespace Trading.Portfolio;

public sealed class PlanValidationException(string message) : Exception(message);

public sealed class SyntheticPlanJournal(IDashboardSnapshots snapshots, IIndexCatalog catalog, IJournalStore store)
{
    public PlanPreview Preview(PlanSelection selection)
    {
        var (snapshot, option, units) = Resolve(selection);
        return new(selection.Lots, units, option.Quote.State == "fresh" ? option.Quote.Ask : null,
            option.Quote.State, snapshot.Planning);
    }

    public async Task<JournalEntry> SaveAsync(
        SavePlanRequest request, DateTimeOffset savedAtUtc, CancellationToken cancellationToken)
    {
        var (snapshot, option, units) = Resolve(new(request.IndexId, request.ScenarioId, request.SnapshotId, request.ContractId, request.Lots));
        if (request.Status is not ("considered" or "planned" or "skipped"))
        {
            throw new PlanValidationException("status must be considered, planned or skipped. A journal plan is not an executed order.");
        }
        ValidateString(request.Thesis, "thesis", 2000);
        ValidateString(request.StopPlan, "stopPlan", 2000);
        ValidateString(request.Invalidation, "invalidation", 2000);
        ValidateString(request.Note, "note", 4000);
        var entry = new JournalEntry(Guid.NewGuid().ToString("N"), savedAtUtc.ToUniversalTime(), "SYNTHETIC TEST",
            snapshot.ScenarioId, snapshot.SnapshotId, option.Contract.Id, request.Lots, units,
            request.Status, request.Thesis, request.StopPlan, request.Invalidation, request.Note, snapshot, snapshot.IndexId);
        await store.SaveAsync(entry, cancellationToken);
        return entry;
    }

    private (DashboardSnapshot Snapshot, OptionRow Option, int Units) Resolve(PlanSelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.IndexId) || catalog.Find(selection.IndexId) is null)
            throw new PlanValidationException("indexId must identify a configured synthetic index.");
        ValidateString(selection.ScenarioId, "scenarioId", 64);
        ValidateString(selection.SnapshotId, "snapshotId", 128);
        ValidateString(selection.ContractId, "contractId", 128);
        var snapshot = snapshots.Get(selection.ScenarioId, selection.IndexId)
            ?? throw new PlanValidationException("scenarioId does not identify a supported synthetic scenario.");
        if (snapshot.IndexId != selection.IndexId || snapshot.SnapshotId != selection.SnapshotId)
        {
            throw new PlanValidationException("snapshotId does not match the selected scenario's snapshot.");
        }
        var option = snapshot.Options.FirstOrDefault(row => row.Contract.Id == selection.ContractId && row.Contract.Underlying == selection.IndexId)
            ?? throw new PlanValidationException("contractId does not identify a contract in this snapshot.");
        int units;
        try
        {
            units = IndicativePlanning.WholeLotUnits(selection.Lots, option.Contract.LotSize);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new PlanValidationException("lots must be positive whole lots with derived units within the 32-bit integer range.");
        }
        return (snapshot, option, units);
    }

    public async Task<IReadOnlyList<JournalEntry>> ListAsync(string? indexId, CancellationToken cancellationToken)
    {
        if (indexId is not null && catalog.Find(indexId) is null)
            throw new PlanValidationException("indexId must identify a configured synthetic index.");
        var entries = (await store.ListAsync(cancellationToken)).Select(ProjectIdentity).ToArray();
        return indexId is null ? entries : entries.Where(entry => entry.IndexId == indexId).ToArray();
    }
    public async Task<JournalEntry?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var entry = await store.GetAsync(id, cancellationToken);
        return entry is null ? null : ProjectIdentity(entry);
    }

    // Read-only bridge: preserve schema-1 evidence and stored JSON exactly; project entry identity only.
    private static JournalEntry ProjectIdentity(JournalEntry entry)
    {
        var snapshot = entry.Snapshot;
        var matchingEvidence = entry.SnapshotId == snapshot.SnapshotId && entry.ScenarioId == snapshot.ScenarioId
            && snapshot.Options.Any(x => x.Contract.Id == entry.ContractId);
        if (matchingEvidence && snapshot.SchemaVersion == "1" && snapshot.FixtureVersion == "synthetic-dashboard-v1"
            && snapshot.SnapshotId == $"synthetic-dashboard-v1:{snapshot.ScenarioId}"
            && snapshot.Source == "SYNTHETIC_FIXTURE" && snapshot.Mode == "SYNTHETIC TEST"
            && entry.Mode == "SYNTHETIC TEST" && snapshot.Market.IndexName == "BANKNIFTY (synthetic cash context)"
            && snapshot.IndexId is null && snapshot.Constituents is null
            && entry.IndexId is null && snapshot.Options.All(x => x.Contract.Underlying == "BANKNIFTY"
                && x.Contract.Exchange == "SYNTHETIC" && x.Contract.IsSynthetic))
            return entry with { IndexId = "BANKNIFTY" };
        if (matchingEvidence && snapshot.SchemaVersion == "2" && !string.IsNullOrWhiteSpace(entry.IndexId)
            && entry.IndexId == snapshot.IndexId && snapshot.Constituents is not null
            && snapshot.Options.All(x => x.Contract.Underlying == entry.IndexId))
            return entry;
        throw new InvalidDataException("Saved journal evidence has an unknown or inconsistent index identity; no identity was inferred.");
    }

    private static void ValidateString(string? value, string name, int maxLength)
    {
        if (value is null || value.Length > maxLength)
        {
            throw new PlanValidationException($"{name} must be a string of at most {maxLength} characters; empty plan text is allowed.");
        }
    }
}
