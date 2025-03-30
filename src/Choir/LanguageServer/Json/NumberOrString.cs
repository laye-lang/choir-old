using System.Text.Json;
using System.Text.Json.Serialization;

namespace Choir.LanguageServer.Json;

public abstract class NumberOrString
{
    public sealed class Number : NumberOrString { public required long Value { get; init; } }
    public sealed class String : NumberOrString { public required string? Value { get; init; } }
}

public sealed class NumberOrStringSerializer
    : JsonConverter<NumberOrString>
{
    public override NumberOrString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.String or JsonTokenType.Null)
            return new NumberOrString.String { Value = reader.GetString() };
        else if (reader.TokenType is JsonTokenType.Number)
            return new NumberOrString.Number { Value = reader.GetInt64() };
        else return null;
    }

    public override void Write(Utf8JsonWriter writer, NumberOrString value, JsonSerializerOptions options)
    {
        if (value is NumberOrString.String stringValue)
            writer.WriteStringValue(stringValue.Value);
        else if (value is NumberOrString.Number numberValue)
            writer.WriteNumberValue(numberValue.Value);
    }
}
