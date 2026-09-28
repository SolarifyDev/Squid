using System.Security.Cryptography;
using System.Text;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportRequiredInputBuilder
{
    public const string RequiredSecretInputPrefix = "required-secret-input";

    public static bool IsSensitiveVariable(OctopusVariableDto variable)
        => variable != null
           && (variable.IsSensitive || string.Equals(variable.Type?.Trim(), "Sensitive", StringComparison.OrdinalIgnoreCase));

    public static OctopusImportRequiredInputDto ForSensitiveVariable(
        string sourceId,
        OctopusVariableDto variable)
    {
        ArgumentNullException.ThrowIfNull(variable);

        return new OctopusImportRequiredInputDto
        {
            InputKey = BuildInputKey(OctopusImportRequiredInputKind.SensitiveVariableValue, sourceId, "Value"),
            Kind = OctopusImportRequiredInputKind.SensitiveVariableValue,
            SourceId = sourceId,
            SourceType = OctopusResourceKind.Variable.ToString(),
            Name = variable.Name,
            FieldName = "Value",
            ValueType = variable.Type,
            HasSourceValue = !string.IsNullOrEmpty(variable.Value),
            IsRequired = true,
            SourceScopes = CloneScopes(variable.Scope)
        };
    }

    public static string BuildVariableSourceId(string variableSetSourceId, string variableSourceId, int index)
        => string.IsNullOrWhiteSpace(variableSourceId)
            ? $"{variableSetSourceId}/variable-{index + 1}"
            : variableSourceId;

    private static string BuildInputKey(
        OctopusImportRequiredInputKind kind,
        string sourceId,
        string fieldName)
        => $"{RequiredSecretInputPrefix}:{kind}:{StableKeyPart(sourceId)}:{fieldName}";

    private static string StableKeyPart(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static Dictionary<string, List<string>> CloneScopes(Dictionary<string, List<string>> scopes)
    {
        if (scopes == null)
            return [];

        return scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope.Key))
            .OrderBy(scope => scope.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                scope => scope.Key,
                scope => (scope.Value ?? [])
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }
}
