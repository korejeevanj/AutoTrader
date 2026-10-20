using System.Globalization;
using System.Text;

namespace Trading.Data;

public sealed record KiteMasterRow(long InstrumentToken, string Exchange, string TradingSymbol, string Name,
    string InstrumentType, DateOnly? Expiry, decimal Strike, int LotSize, decimal TickSize);
public sealed record KiteMasterSnapshot(DateTimeOffset CapturedAtUtc, string Sha256, IReadOnlyList<KiteMasterRow> Instruments);

public static class KiteInstrumentMaster
{
    public static KiteMasterSnapshot Parse(byte[] csv, DateTimeOffset capturedAtUtc)
    {
        var rows = Rows(Encoding.UTF8.GetString(csv)).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("Empty instrument master.");
        var header = rows[0].Select((name, i) => (name: name.TrimStart('\uFEFF'), i)).ToDictionary(field => field.name, field => field.i, StringComparer.Ordinal);
        string[] required = ["instrument_token", "exchange", "tradingsymbol", "name", "instrument_type", "expiry", "strike", "lot_size", "tick_size"];
        if (required.Any(name => !header.ContainsKey(name))) throw new InvalidDataException("Invalid instrument master schema.");
        var parsed = new List<KiteMasterRow>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Length != rows[0].Length) throw new InvalidDataException("Invalid instrument master row.");
            string Value(string name) => row[header[name]];
            if (!long.TryParse(Value("instrument_token"), CultureInfo.InvariantCulture, out var token)
                || !int.TryParse(Value("lot_size"), CultureInfo.InvariantCulture, out var lot)
                || !decimal.TryParse(Value("tick_size"), CultureInfo.InvariantCulture, out var tick)
                || !decimal.TryParse(Value("strike"), CultureInfo.InvariantCulture, out var strike))
                throw new InvalidDataException("Invalid instrument master numbers.");
            DateOnly? expiry = Value("expiry").Length == 0 ? null : DateOnly.ParseExact(Value("expiry"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            parsed.Add(new(token, Value("exchange"), Value("tradingsymbol"), Value("name"), Value("instrument_type"), expiry, strike, lot, tick));
        }
        if (parsed.Select(row => row.InstrumentToken).Distinct().Count() != parsed.Count)
            throw new InvalidDataException("Conflicting instrument tokens.");
        return new(capturedAtUtc, KiteCandleImporter.Hash(csv), parsed.AsReadOnly());
    }

    internal static IEnumerable<string[]> Rows(string text)
    {
        var fields = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (!quoted && c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (!quoted && c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString()); field.Clear();
                if (fields.Any(value => value.Length != 0)) yield return fields.ToArray();
                fields.Clear();
            }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unterminated CSV quotation.");
        if (field.Length != 0 || fields.Count != 0) { fields.Add(field.ToString()); yield return fields.ToArray(); }
    }
}
