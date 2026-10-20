using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Engine;

internal static class FrozenPositionFingerprint
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new CanonicalDecimal(), new CanonicalTimestamp() }
    };

    // Representation changes (200 vs 200.00, equivalent UTC offsets) are not fill/policy changes.
    // Version this encoding independently from the public wire/checkpoint serialization.
    public static string Create(PaperPositionEvidence position) => EvidenceChecks.Key(
        "frozen-position-v2", JsonSerializer.Serialize(position, Options));

    private sealed class CanonicalDecimal : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture));
    }

    private sealed class CanonicalTimestamp : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}
