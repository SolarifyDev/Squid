using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableDto
{
    /// <summary>Default constructor — required for JSON deserialization.</summary>
    public VariableDto() { }

    /// <summary>
    /// Copy-constructor — preserves every field of <paramref name="other"/>.
    /// Used by paths that need to mutate exactly one field on a clone without
    /// aliasing the source instance (e.g. <c>ExecuteStepsPhase.EncryptIfSensitive</c>
    /// produces an encrypted-Value clone for the checkpoint JSON without
    /// rewriting the live <c>_ctx.Variables</c> entry).
    ///
    /// <para><b>Future-proof by design</b>: future fields added to this DTO
    /// flow through this ctor automatically <i>only if a contributor adds
    /// the assignment here</i>. The reflection-based drift detector
    /// <c>VariableDtoCopyConstructorTests.CopyConstructor_PreservesAllPublicProperties</c>
    /// fails on the first missing assignment so the omission is caught at
    /// PR review, not silently in production.</para>
    ///
    /// <para><b>Scopes is a shallow copy</b>: the cloned instance shares
    /// the same <see cref="VariableScopeDto"/> list reference as
    /// <paramref name="other"/>. Callers that need an independent list must
    /// reassign <c>Scopes = new List&lt;VariableScopeDto&gt;(other.Scopes)</c>
    /// post-clone. The encrypt-for-checkpoint use case doesn't mutate Scopes
    /// so the shared reference is safe.</para>
    /// </summary>
    public VariableDto(VariableDto other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Id = other.Id;
        VariableSetId = other.VariableSetId;
        Name = other.Name;
        Value = other.Value;
        Description = other.Description;
        Type = other.Type;
        IsSensitive = other.IsSensitive;
        SortOrder = other.SortOrder;
        LastModifiedDate = other.LastModifiedDate;
        LastModifiedBy = other.LastModifiedBy;
        PromptLabel = other.PromptLabel;
        PromptDescription = other.PromptDescription;
        PromptRequired = other.PromptRequired;
        Scopes = other.Scopes;
    }

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

    // Prompt
    public string PromptLabel { get; set; }
    public string PromptDescription { get; set; }
    public bool PromptRequired { get; set; }
    public bool IsPrompted => !string.IsNullOrEmpty(PromptLabel);

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
