using System.Globalization;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Trading.Domain;

namespace Trading.Data;

// Market and paper stores can use separate database paths to isolate the high-volume writer.
public sealed class SqliteQuoteStore(string path)
{
    public string FilePath => path;

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS quote_runs(id TEXT PRIMARY KEY, created_at TEXT NOT NULL,
              manifest TEXT NOT NULL, closed INTEGER NOT NULL DEFAULT 0, last_sequence INTEGER NOT NULL DEFAULT 0, checkpoint TEXT);
            CREATE TABLE IF NOT EXISTS market_events(run_id TEXT NOT NULL, sequence INTEGER NOT NULL,
              received_at TEXT NOT NULL, connection_id TEXT NOT NULL, kind TEXT NOT NULL, payload BLOB NOT NULL, admission TEXT,
              PRIMARY KEY(run_id,sequence), FOREIGN KEY(run_id) REFERENCES quote_runs(id));
            CREATE TABLE IF NOT EXISTS quote_decisions(run_id TEXT NOT NULL, sequence INTEGER NOT NULL,
              payload BLOB NOT NULL, PRIMARY KEY(run_id,sequence));
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(market_events)";
        var hasAdmission = false;
        using (var columns = command.ExecuteReader()) while (columns.Read()) hasAdmission |= columns.GetString(1) == "admission";
        if (!hasAdmission) { command.CommandText = "ALTER TABLE market_events ADD COLUMN admission TEXT"; command.ExecuteNonQuery(); }
        return connection;
    }

    public void Create(string id, DateTimeOffset created, string manifest)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO quote_runs(id,created_at,manifest) VALUES($id,$time,$manifest)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$time", created.ToString("O"));
        command.Parameters.AddWithValue("$manifest", manifest); command.ExecuteNonQuery();
    }

    public void Append(IReadOnlyList<RecordedMarketEvent> events)
    {
        if (events.Count == 0) return;
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        foreach (var item in events)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO market_events(run_id,sequence,received_at,connection_id,kind,payload,admission)
                VALUES($run,$seq,$time,$connection,$kind,$payload,$admission)
                ON CONFLICT(run_id,sequence) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$run", item.RunId); command.Parameters.AddWithValue("$seq", item.Sequence);
            command.Parameters.AddWithValue("$time", item.ReceivedAtUtc.ToString("O")); command.Parameters.AddWithValue("$connection", item.ConnectionId);
            command.Parameters.AddWithValue("$kind", item.Kind); command.Parameters.AddWithValue("$payload", item.Payload);
            var admission = item.Admission is null ? null : JsonSerializer.Serialize(item.Admission);
            command.Parameters.AddWithValue("$admission", (object?)admission ?? DBNull.Value);
            if (command.ExecuteNonQuery() == 0)
            {
                using var existing = connection.CreateCommand(); existing.Transaction = transaction;
                existing.CommandText = "SELECT received_at,connection_id,kind,payload,admission FROM market_events WHERE run_id=$run AND sequence=$seq";
                existing.Parameters.AddWithValue("$run", item.RunId); existing.Parameters.AddWithValue("$seq", item.Sequence);
                using var reader = existing.ExecuteReader();
                if (!reader.Read() || reader.GetString(0) != item.ReceivedAtUtc.ToString("O") || reader.GetString(1) != item.ConnectionId
                    || reader.GetString(2) != item.Kind || !((byte[])reader[3]).AsSpan().SequenceEqual(item.Payload)
                    || (reader.IsDBNull(4) ? null : reader.GetString(4)) != admission)
                    throw new InvalidDataException("Conflicting market-event retry.");
            }
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE quote_runs SET last_sequence=MAX(last_sequence,$seq) WHERE id=$run";
            update.Parameters.AddWithValue("$run", item.RunId); update.Parameters.AddWithValue("$seq", item.Sequence);
            if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("Unknown quote run.");
        }
        transaction.Commit();
    }

    public void Checkpoint(string id, long sequence, string json, bool closed = false, string? decisions = null)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE quote_runs SET checkpoint=$json,last_sequence=$seq,closed=$closed WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$closed", closed ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Unknown quote run.");
        if (decisions is not null) InsertDecision(connection, transaction, id, sequence, decisions);
        transaction.Commit();
    }

    public void AppendDecision(string id, long sequence, string decisions)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        InsertDecision(connection, transaction, id, sequence, decisions); transaction.Commit();
    }

    private static void InsertDecision(SqliteConnection connection, SqliteTransaction transaction, string id, long sequence, string json)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true))
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(json));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO quote_decisions(run_id,sequence,payload) VALUES($run,$seq,$payload) ON CONFLICT(run_id,sequence) DO NOTHING";
        command.Parameters.AddWithValue("$run", id); command.Parameters.AddWithValue("$seq", sequence); command.Parameters.AddWithValue("$payload", buffer.ToArray());
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<JsonElement> Decisions(string id, long after = 0, int limit = 50)
    {
        if (after < 0 || limit is < 1 or > 200) throw new ArgumentException("Decision paging requires after>=0 and limit 1–200.");
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM quote_decisions WHERE run_id=$run AND sequence>$after ORDER BY sequence LIMIT $limit";
        command.Parameters.AddWithValue("$run", id); command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var result = new List<JsonElement>();
        while (reader.Read())
        {
            using var compressed = new MemoryStream((byte[])reader[0]); using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var document = JsonDocument.Parse(BoundedSourceRead.Read(gzip, 16 * 1024 * 1024, () => new InvalidDataException("Decision batch too large.")));
            result.Add(document.RootElement.Clone());
        }
        return result;
    }

    // Folds committed WAL pages into the main file so it can be copied or archived on its own.
    public void FoldWal()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; command.ExecuteNonQuery();
    }

    public IReadOnlyList<RecordedQuoteRun> Runs()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,created_at,manifest,closed,last_sequence,checkpoint FROM quote_runs ORDER BY created_at DESC";
        using var reader = command.ExecuteReader(); var result = new List<RecordedQuoteRun>();
        while (reader.Read()) result.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result.AsReadOnly();
    }

    public IEnumerable<RecordedMarketEvent> Read(string runId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence,received_at,connection_id,kind,payload,admission FROM market_events WHERE run_id=$run ORDER BY sequence";
        command.Parameters.AddWithValue("$run", runId); using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(runId, reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2), reader.GetString(3), (byte[])reader[4], reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<MarketAdmission>(reader.GetString(5)));
    }
}
