using System.Text.Json;
using System.Text.Json.Serialization;

namespace R007.Pos.Core.Money;

/// <summary>Reads money as a decimal string (or, tolerantly, a JSON number) and always writes a decimal string.</summary>
public sealed class DecimalStringJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => MoneyFormat.TryParseWire(reader.GetString(), out var d)
                ? d
                : throw new JsonException("Invalid decimal string."),
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException("Expected a decimal string."),
        };

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(MoneyFormat.ToWire(value));
}

/// <summary>Nullable counterpart of <see cref="DecimalStringJsonConverter"/>.</summary>
public sealed class NullableDecimalStringJsonConverter : JsonConverter<decimal?>
{
    private static readonly DecimalStringJsonConverter Inner = new();

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(decimal), options);

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            Inner.Write(writer, value.Value, options);
        }
    }
}

/// <summary>
/// Reads a map of tender -> amount. PHP serialises an empty map as <c>[]</c> and a filled one as <c>{...}</c>, so an empty JSON array is
/// accepted as "no entries" (the node's cash-session <c>totals.nonCash</c> does exactly this).
/// </summary>
public sealed class DecimalMapJsonConverter : JsonConverter<IReadOnlyDictionary<string, decimal>?>
{
    public override IReadOnlyDictionary<string, decimal>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartArray:
                reader.Skip();
                return new Dictionary<string, decimal>();
            case JsonTokenType.StartObject:
                var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
                var inner = new DecimalStringJsonConverter();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString() ?? string.Empty;
                    reader.Read();
                    map[key] = inner.Read(ref reader, typeof(decimal), options);
                }

                return map;
            default:
                throw new JsonException("Expected an object of amounts.");
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, decimal>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var (key, amount) in value)
        {
            writer.WriteString(key, MoneyFormat.ToWire(amount));
        }

        writer.WriteEndObject();
    }
}
