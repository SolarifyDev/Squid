using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportRedactionService : IScopedDependency
{
    bool ShouldRedactPropertyValue(string propertyName, string value = null);

    string RedactMetadataValue(string propertyName, string value);

    Dictionary<string, string> RedactProperties(IReadOnlyDictionary<string, string> properties);

    string RedactJson(string json);

    T RedactDto<T>(T value) where T : class;

    OctopusImportDiagnosticDto RedactDiagnostic(OctopusImportDiagnosticDto diagnostic);

    OctopusInputExtractionDiagnostic RedactDiagnostic(OctopusInputExtractionDiagnostic diagnostic);
}

public class OctopusImportRedactionService : IOctopusImportRedactionService
{
    public bool ShouldRedactPropertyValue(string propertyName, string value = null)
        => OctopusImportRedaction.ShouldRedactPropertyValue(propertyName, value);

    public string RedactMetadataValue(string propertyName, string value)
        => OctopusImportRedaction.RedactMetadataValue(propertyName, value);

    public Dictionary<string, string> RedactProperties(IReadOnlyDictionary<string, string> properties)
        => OctopusImportRedaction.RedactProperties(properties);

    public string RedactJson(string json)
        => OctopusImportRedaction.RedactJson(json);

    public T RedactDto<T>(T value) where T : class
        => OctopusImportRedaction.RedactDto(value);

    public OctopusImportDiagnosticDto RedactDiagnostic(OctopusImportDiagnosticDto diagnostic)
        => OctopusImportRedaction.RedactDiagnostic(diagnostic);

    public OctopusInputExtractionDiagnostic RedactDiagnostic(OctopusInputExtractionDiagnostic diagnostic)
        => OctopusImportRedaction.RedactDiagnostic(diagnostic);
}

public static class OctopusImportRedaction
{
    public const string RedactedValue = "[redacted]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] SensitiveNameFragments =
    [
        "password",
        "passphrase",
        "secret",
        "token",
        "credential",
        "apikey",
        "api-key",
        "api key",
        "username",
        "user name",
        "accesskey",
        "access-key",
        "clientsecret",
        "client-secret",
        "privatekey",
        "private-key",
        "private key",
        "pfx",
        "certificateData",
        "certificate-data",
        "certificate data"
    ];

    private static readonly string[] SensitiveValueFragments =
    [
        "password",
        "passphrase",
        "secret",
        "token",
        "credential",
        "apikey",
        "api-key",
        "api key",
        "accesskey",
        "access-key",
        "clientsecret",
        "client-secret",
        "privatekey",
        "private-key",
        "private key",
        "pfx"
    ];

    private static readonly Regex QuotedSensitiveValuePattern = new("'([^']*)'", RegexOptions.Compiled);

    private static readonly Regex AssignmentSecretPattern =
        new(@"(?i)\b(password|passphrase|secret|token|credential|api[-_ ]?key|access[-_ ]?key|client[-_ ]?secret|private[-_ ]?key|pfx)\b\s*[:=]\s*[^,\s}""]+", RegexOptions.Compiled);

    private static readonly Regex AuthorizationSecretPattern =
        new("(?i)\\b(bearer|basic)\\s+[a-z0-9._~+/=-]+", RegexOptions.Compiled);

    public static bool ShouldRedactPropertyValue(string propertyName, string value = null)
        => IsSensitiveName(propertyName)
           || LooksSensitiveAssignment(value)
           || LooksSensitiveAuthorizationValue(value);

    public static string RedactMetadataValue(string propertyName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return ShouldRedactPropertyValue(propertyName, value)
               || LooksSensitiveValue(value)
            ? RedactedValue
            : value.Trim();
    }

    public static Dictionary<string, string> RedactProperties(IReadOnlyDictionary<string, string> properties)
    {
        if (properties == null)
            return [];

        var redacted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in properties)
        {
            redacted[key] = ShouldRedactPropertyValue(key, value)
                ? RedactedValue
                : value;
        }

        return redacted;
    }

    public static string RedactJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        try
        {
            var node = JsonNode.Parse(json);
            var redacted = RedactNode(node, null, false);
            return redacted?.ToJsonString(JsonOptions) ?? json;
        }
        catch (JsonException)
        {
            return LooksSensitiveValue(json) ? RedactedValue : json;
        }
    }

    public static JsonElement RedactJsonElement(JsonElement element)
    {
        var redactedJson = RedactJson(element.GetRawText());
        using var document = JsonDocument.Parse(redactedJson);
        return document.RootElement.Clone();
    }

    public static T RedactDto<T>(T value) where T : class
    {
        if (value == null)
            return null;

        var json = JsonSerializer.Serialize(value, JsonOptions);
        return JsonSerializer.Deserialize<T>(RedactJson(json), JsonOptions);
    }

    public static OctopusImportDiagnosticDto RedactDiagnostic(OctopusImportDiagnosticDto diagnostic)
    {
        if (diagnostic == null)
            return null;

        return new OctopusImportDiagnosticDto
        {
            Severity = diagnostic.Severity,
            Code = diagnostic.Code,
            Message = RedactDiagnosticText(diagnostic.Message),
            ResourceType = diagnostic.ResourceType,
            SourceId = RedactMetadataValue("SourceId", diagnostic.SourceId),
            ResourceName = RedactMetadataValue("ResourceName", diagnostic.ResourceName)
        };
    }

    public static OctopusInputExtractionDiagnostic RedactDiagnostic(OctopusInputExtractionDiagnostic diagnostic)
    {
        if (diagnostic == null)
            return null;

        return new OctopusInputExtractionDiagnostic(
            diagnostic.Severity,
            diagnostic.Code,
            RedactDiagnosticText(diagnostic.Message),
            RedactMetadataValue("SourcePath", diagnostic.SourcePath),
            RedactMetadataValue("SourceId", diagnostic.SourceId),
            diagnostic.DocumentKind);
    }

    private static JsonNode RedactNode(JsonNode node, string propertyName, bool forceRedact)
    {
        if (node == null)
            return null;

        if (node is JsonValue value)
            return RedactValue(value, propertyName, forceRedact);

        if (node is JsonArray array)
        {
            var redactedArray = new JsonArray();
            foreach (var child in array)
                redactedArray.Add(RedactNode(child, propertyName, forceRedact));

            return redactedArray;
        }

        var obj = node.AsObject();
        var redactedObject = new JsonObject();
        var isSensitiveVariable = IsSensitiveVariableObject(obj);

        foreach (var (childName, childNode) in obj)
        {
            var childForceRedact = forceRedact || IsSensitiveContainerName(childName);

            if (isSensitiveVariable && string.Equals(childName, "Value", StringComparison.OrdinalIgnoreCase))
            {
                redactedObject[childName] = RedactedValue;
                continue;
            }

            redactedObject[childName] = RedactNode(childNode, childName, childForceRedact);
        }

        return redactedObject;
    }

    private static JsonNode RedactValue(JsonValue value, string propertyName, bool forceRedact)
    {
        if (!value.TryGetValue<string>(out var stringValue))
            return value.DeepClone();

        return forceRedact || ShouldRedactPropertyValue(propertyName, stringValue)
               || (IsRedactableMetadataName(propertyName) && LooksSensitiveValue(stringValue))
            ? RedactedValue
            : stringValue;
    }

    private static bool IsRedactableMetadataName(string propertyName)
        => string.Equals(propertyName, "ResourceName", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "SourceName", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveVariableObject(JsonObject obj)
    {
        var isSensitive = TryGetBoolean(obj, "IsSensitive") == true;
        var isSensitiveType = string.Equals(TryGetString(obj, "Type"), "Sensitive", StringComparison.OrdinalIgnoreCase);

        return isSensitive || isSensitiveType;
    }

    private static bool IsSensitiveContainerName(string propertyName)
        => IsSensitiveName(propertyName)
           || string.Equals(propertyName, "Credentials", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "CertificateData", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveName(string value)
        => ContainsAny(value, SensitiveNameFragments);

    private static bool LooksSensitiveValue(string value)
        => ContainsAny(value, SensitiveValueFragments);

    private static bool LooksSensitiveAssignment(string value)
        => !string.IsNullOrWhiteSpace(value) && AssignmentSecretPattern.IsMatch(value);

    private static bool LooksSensitiveAuthorizationValue(string value)
        => !string.IsNullOrWhiteSpace(value) && AuthorizationSecretPattern.IsMatch(value);

    private static bool ContainsAny(string value, IEnumerable<string> fragments)
        => !string.IsNullOrWhiteSpace(value)
           && fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool? TryGetBoolean(JsonObject obj, string propertyName)
    {
        return TryGetProperty(obj, propertyName) is { } node && node is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static string TryGetString(JsonObject obj, string propertyName)
    {
        return TryGetProperty(obj, propertyName) is { } node && node is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    private static JsonNode TryGetProperty(JsonObject obj, string propertyName)
    {
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    private static string RedactDiagnosticText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var redacted = QuotedSensitiveValuePattern.Replace(value, match =>
        {
            var quoted = match.Groups[1].Value;
            return LooksSensitiveValue(quoted) ? $"'{RedactedValue}'" : match.Value;
        });

        redacted = AssignmentSecretPattern.Replace(redacted, match =>
        {
            var separatorIndex = match.Value.IndexOfAny([':', '=']);
            return separatorIndex < 0
                ? RedactedValue
                : match.Value[..(separatorIndex + 1)] + RedactedValue;
        });

        return AuthorizationSecretPattern.Replace(redacted, match =>
        {
            var scheme = match.Groups[1].Value;
            return $"{scheme} {RedactedValue}";
        });
    }
}
