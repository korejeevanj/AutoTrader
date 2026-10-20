using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Trading.Domain;

namespace Trading.Data;

// Append-only journal: one complete immutable entry JSON and indexed identity/save time.
public sealed class SqliteJournalStore(string absolutePath) : IJournalStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim initialization = new(1, 1);
    private volatile bool initialized;

    public async Task SaveAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO journal_plans (id, saved_at_utc, entry_json) VALUES ($id, $saved, $json)";
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$saved", entry.SavedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<JournalEntry>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_json FROM journal_plans ORDER BY saved_at_utc DESC, rowid DESC";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<JournalEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntry(reader.GetString(0)));
        }
        return entries;
    }

    public async Task<JournalEntry?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_json FROM journal_plans WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : ReadEntry(json);
    }

    private static JournalEntry ReadEntry(string json) =>
        JsonSerializer.Deserialize<JournalEntry>(json, JsonOptions)
        ?? throw new InvalidDataException("Journal entry JSON is unavailable.");

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await initialization.WaitAsync(cancellationToken);
        try
        {
            if (!initialized)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)
                    ?? throw new InvalidOperationException("Journal path must have a parent directory."));
                await using var schemaConnection = CreateConnection();
                await schemaConnection.OpenAsync(cancellationToken);
                using var transaction = schemaConnection.BeginTransaction();
                using var command = schemaConnection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS journal_plans (
                        id TEXT PRIMARY KEY NOT NULL,
                        saved_at_utc TEXT NOT NULL,
                        entry_json TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_journal_plans_saved_at ON journal_plans (saved_at_utc DESC);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                initialized = true;
            }
        }
        finally
        {
            initialization.Release();
        }
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = absolutePath, Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false, DefaultTimeout = 30
    }.ToString());

    public void Dispose() => initialization.Dispose();
}
