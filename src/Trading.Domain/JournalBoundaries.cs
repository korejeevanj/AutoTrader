namespace Trading.Domain;

public interface IDashboardSnapshots
{
    IReadOnlyList<ScenarioDescriptor> Scenarios { get; }
    DashboardSnapshot? Get(string scenarioId, string indexId = "BANKNIFTY");
}

public interface IIndexCatalog
{
    IReadOnlyList<IndexDefinition> Indices { get; }
    IndexDefinition? Find(string indexId);
}

public interface IJournalStore
{
    Task SaveAsync(JournalEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<JournalEntry>> ListAsync(CancellationToken cancellationToken);
    Task<JournalEntry?> GetAsync(string id, CancellationToken cancellationToken);
}
