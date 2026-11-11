using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;

namespace Squid.Core.Services.OctopusImport.Mapping;

public enum OctopusImportScopeOutcome
{
    Mapped,
    PassThrough,
    EmptyValue,
    UnsupportedType,
    Unmapped
}

public sealed record OctopusImportScopeResolution(string ScopeKey, string ScopeValue, OctopusImportScopeOutcome Outcome, VariableScopeType? ScopeType, OctopusResourceKind? SourceKind)
{
    /// <summary>A scope that cannot be represented faithfully. Dropping it would WIDEN the variable, so it blocks.</summary>
    public bool Blocks => Outcome is OctopusImportScopeOutcome.UnsupportedType or OctopusImportScopeOutcome.Unmapped;
}

/// <summary>
/// Single source of truth for how an Octopus variable scope maps to a Squid scope. The confirmation
/// variable mapper resolves against the live id map; preview validation resolves against the plan
/// (what confirmation will have created or reused by the time the variable set is written). Both go
/// through <see cref="ResolveAll"/>, so preview blocks exactly the scopes confirmation would reject.
/// </summary>
public static class OctopusImportVariableScopeRule
{
    public static IEnumerable<OctopusImportScopeResolution> ResolveAll(Dictionary<string, List<string>> scope, Func<string, OctopusResourceKind, bool> hasDestination)
    {
        foreach (var entry in scope ?? [])
        {
            var scopeType = MapScopeType(entry.Key);

            if (scopeType == null)
            {
                yield return new OctopusImportScopeResolution(entry.Key, string.Join(",", entry.Value ?? []), OctopusImportScopeOutcome.UnsupportedType, null, null);
                continue;
            }

            foreach (var value in entry.Value ?? [])
                yield return Resolve(entry.Key, scopeType.Value, value, hasDestination);
        }
    }

    /// <summary>
    /// The resource kind whose confirmation step writes the variable set: a library's variables are
    /// written together with the library itself, a project's after its process has been created.
    /// </summary>
    public static OctopusResourceKind WriterKind(OctopusVariableSetDto variableSet)
        => OctopusResourceGraphBuilder.IsLibraryVariableSetOwner(variableSet?.OwnerType) ? OctopusResourceKind.LibraryVariableSet : OctopusResourceKind.VariableSet;

    /// <summary>Confirmation imports in dependency-rank order, so a scope target is in the id map only if it ranks before the writer.</summary>
    public static bool IsImportedBefore(OctopusResourceKind targetKind, OctopusResourceKind writerKind)
        => OctopusResourceMetadata.For(targetKind).Rank < OctopusResourceMetadata.For(writerKind).Rank;

    private static OctopusImportScopeResolution Resolve(string scopeKey, VariableScopeType scopeType, string value, Func<string, OctopusResourceKind, bool> hasDestination)
    {
        if (string.IsNullOrWhiteSpace(value)) return new OctopusImportScopeResolution(scopeKey, value, OctopusImportScopeOutcome.EmptyValue, scopeType, null);

        if (scopeType == VariableScopeType.Role) return new OctopusImportScopeResolution(scopeKey, value, OctopusImportScopeOutcome.PassThrough, scopeType, null);

        var sourceKind = SourceKind(scopeType);
        var outcome = sourceKind != null && hasDestination(value, sourceKind.Value) ? OctopusImportScopeOutcome.Mapped : OctopusImportScopeOutcome.Unmapped;

        return new OctopusImportScopeResolution(scopeKey, value, outcome, scopeType, sourceKind);
    }

    private static VariableScopeType? MapScopeType(string octopusScopeType)
        => octopusScopeType?.Trim() switch
        {
            "Environment" => VariableScopeType.Environment,
            "Machine" => VariableScopeType.Machine,
            "Role" => VariableScopeType.Role,
            "Channel" => VariableScopeType.Channel,
            "Action" => VariableScopeType.Action,
            "Process" => VariableScopeType.Process,
            _ => null
        };

    private static OctopusResourceKind? SourceKind(VariableScopeType scopeType)
        => scopeType switch
        {
            VariableScopeType.Environment => OctopusResourceKind.Environment,
            VariableScopeType.Machine => OctopusResourceKind.Machine,
            VariableScopeType.Channel => OctopusResourceKind.Channel,
            VariableScopeType.Action => OctopusResourceKind.DeploymentAction,
            VariableScopeType.Process => OctopusResourceKind.DeploymentProcess,
            _ => null
        };
}
