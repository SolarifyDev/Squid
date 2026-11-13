using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SourceMap;

/// <summary>
/// The natural key of a sensitive variable value, used as the <c>VariableSecret</c> row's source id. An Octopus export may
/// rewrite variable ids and Squid re-inserts variables on every save, so neither id can identify "this secret" across
/// sources; <c>(owner variable set, name, type, scope)</c> can. Scope values are compared by the Squid row they map to (the
/// same environment imported from either source is the same Squid environment); roles are compared as text. The same
/// function is used when the row is written (server import) and when a file import looks for it (fill).
/// </summary>
public static class OctopusImportSecretKeys
{
    public const string VariablePrefix = "vs:";

    /// <summary>The key, or null when a scope value cannot be mapped (such a secret cannot be identified, so it is never filled).</summary>
    public static string ForVariable(string ownerSetSourceId, OctopusVariableDto variable, Func<string, OctopusResourceKind, int?> destinationOf)
    {
        ArgumentNullException.ThrowIfNull(variable);

        var scopes = CanonicalScopes(variable.Scope, destinationOf);

        if (scopes == null) return null;

        var type = OctopusImportVariableMapper.SquidVariableType(variable)?.ToString() ?? variable.Type;

        return VariablePrefix + Hash($"{OctopusImportPriorMappings.Normalize(ownerSetSourceId)}|{variable.Name}|{type}|{string.Join(",", scopes)}");
    }

    /// <summary>Each scope as <c>Type:Value</c> (mapped scopes by Squid destination id), distinct and ordinal-sorted; null if any value cannot be mapped.</summary>
    public static IReadOnlyList<string> CanonicalScopes(Dictionary<string, List<string>> scope, Func<string, OctopusResourceKind, int?> destinationOf)
    {
        ArgumentNullException.ThrowIfNull(destinationOf);

        var values = new List<string>();

        foreach (var resolution in OctopusImportVariableScopeRule.ResolveAll(scope, (sourceId, kind) => destinationOf(sourceId, kind) is > 0))
        {
            if (resolution.Blocks) return null;
            if (resolution.Outcome == OctopusImportScopeOutcome.EmptyValue) continue;

            values.Add($"{resolution.ScopeType}:{CanonicalValue(resolution, destinationOf)}");
        }

        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static string CanonicalValue(OctopusImportScopeResolution resolution, Func<string, OctopusResourceKind, int?> destinationOf)
        => resolution.Outcome == OctopusImportScopeOutcome.Mapped ? destinationOf(resolution.ScopeValue, resolution.SourceKind!.Value)!.Value.ToString(CultureInfo.InvariantCulture) : resolution.ScopeValue;

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
