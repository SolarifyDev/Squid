using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableDto
{
    public int Id { get; set; }

    public int VariableSetId { get; set; }

    public string Name { get; set; }

    public string Value { get; set; }

    public string Description { get; set; }

    public VariableType Type { get; set; }

    public bool IsSensitive { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset LastModifiedDate { get; set; }

    [JsonConverter(typeof(LegacyNullableIntConverter))]
    public int? LastModifiedBy { get; set; }

    public List<VariableScopeDto> Scopes { get; set; } = new List<VariableScopeDto>();
}

/// <summary>
/// Handles backward-compatible deserialization of LastModifiedBy which was previously
/// a nullable string (e.g. null, "System") and is now int?.
/// </summary>
internal sealed class LegacyNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.Number => reader.GetInt32(),
        JsonTokenType.String when int.TryParse(reader.GetString(), out var v) => v,
        JsonTokenType.String => null,
        _ => null
    };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}
