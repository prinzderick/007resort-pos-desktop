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
