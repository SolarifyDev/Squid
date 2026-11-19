using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Pure rules of the deployment preflight for secrets an Octopus API import recorded as missing (D16). A deployment variable is
/// flagged only when ALL hold: it is sensitive and empty in the deployment's variables; it may be used by this deployment (its
/// environment/channel scope matches, and no value that applies to every target outranks it); and an import recorded a
/// <c>VariableSecret</c> row for exactly its identity in its variable set. Empty sensitive values Squid users created, or that an
/// export file imported, have no row and never block — so the guard only ever fails deployments of data the API import created.
/// </summary>
public static class ImportedSecretPreflightRules
{
    /// <summary>Scope dimensions a deployment fixes before any target is chosen; machine, role, action and process differ per target or step.</summary>
    public static IReadOnlySet<VariableScopeType> DeploymentLevelScopes { get; } = new HashSet<VariableScopeType> { VariableScopeType.Environment, VariableScopeType.Channel };

    /// <summary>The source types the preflight reads: the owner variable set's source id (part of the key) and the secret rows.</summary>
    public static IReadOnlyList<string> SourceTypes { get; } = [nameof(OctopusResourceKind.VariableSet), OctopusImportSourceMapQueries.VariableSecretSourceType];

    public static IReadOnlyList<VariableDto> Candidates(IReadOnlyList<VariableDto> variables, VariableScopeContext deployment)
    {
        var all = variables ?? [];

        return all.Where(v => IsEmptySecret(v) && AppliesToDeployment(v, deployment) && !IsOutranked(v, all, deployment)).ToList();
    }

    public static IReadOnlyList<MissingImportedSecret> Match(IReadOnlyList<VariableDto> candidates, IReadOnlyList<OctopusImportSourceMapRow> rows, IReadOnlyList<SensitiveVariableSlot> live, VariableScopeContext deployment)
    {
        var missing = new List<MissingImportedSecret>();

        foreach (var candidate in candidates ?? [])
        {
            var reason = ReasonFor(candidate, rows ?? [], live ?? []);

            if (reason != null) missing.Add(new MissingImportedSecret(candidate.Name, candidate.VariableSetId, ScopeSummary(candidate, deployment), reason.Value));
        }

        return missing;
    }

    // ── Candidates ──────────────────────────────────────────────────────────

    // Built-in and output variables carry no variable set: only stored variables can have an import row.
    private static bool IsEmptySecret(VariableDto variable) => variable.IsSensitive && string.IsNullOrEmpty(variable.Value) && variable.VariableSetId > 0;

    // Target-level scopes are unknown here, so they never exclude: the guard errs towards naming a secret a target might use.
    private static bool AppliesToDeployment(VariableDto variable, VariableScopeContext deployment)
        => VariableScopeEvaluator.IsApplicable(new VariableDto(variable) { Scopes = (variable.Scopes ?? []).Where(s => DeploymentLevelScopes.Contains(s.ScopeType)).ToList() }, deployment);

    // A value scoped only at deployment level applies to every target, so one that outranks the variable is always chosen over it.
    private static bool IsOutranked(VariableDto variable, IReadOnlyList<VariableDto> all, VariableScopeContext deployment)
        => all.Any(other => !ReferenceEquals(other, variable) && string.Equals(other.Name, variable.Name, StringComparison.OrdinalIgnoreCase) && IsDeploymentLevelOnly(other) && AppliesToDeployment(other, deployment) && VariableScopeEvaluator.ComputeRank(other) > VariableScopeEvaluator.ComputeRank(variable));

    private static bool IsDeploymentLevelOnly(VariableDto variable) => (variable.Scopes ?? []).All(s => DeploymentLevelScopes.Contains(s.ScopeType));

    // ── Matching ────────────────────────────────────────────────────────────

    // Missing and still empty in the set → never imported. A value set in the set since (any row state) → this snapshot is stale.
    // Filled/Superseded and empty → settled (empty in Octopus too, or a user cleared it): not the import's concern.
    private static MissingImportedSecretReason? ReasonFor(VariableDto candidate, IReadOnlyList<OctopusImportSourceMapRow> rows, IReadOnlyList<SensitiveVariableSlot> live)
    {
        var identity = IdentityOf(candidate);
        var row = SecretRowOf(candidate.VariableSetId, identity, rows);

        if (row == null) return null;
        if (SensitiveVariableSlots.Find(live, candidate.VariableSetId, identity).State == SecretSlotState.AlreadySet) return MissingImportedSecretReason.SnapshotPredatesValue;

        return row.OutcomeState == OctopusImportSourceMapQueries.MissingOutcome ? MissingImportedSecretReason.NotImported : null;
    }

    private static VariableIdentity IdentityOf(VariableDto variable) => VariableIdentity.Of(variable.Name, variable.Type, (variable.Scopes ?? []).Select(s => (s.ScopeType, s.ScopeValue)));

    // The key embeds the Octopus source id of the owner set, which the set's own source-map row records.
    private static OctopusImportSourceMapRow SecretRowOf(int variableSetId, VariableIdentity identity, IReadOnlyList<OctopusImportSourceMapRow> rows)
    {
        var keys = rows.Where(r => r.SourceType == nameof(OctopusResourceKind.VariableSet) && r.DestinationId == variableSetId).Select(r => OctopusImportPriorMappings.Normalize(OctopusImportSecretKeys.ForIdentity(r.SourceId, identity))).ToHashSet(StringComparer.Ordinal);

        return rows.FirstOrDefault(r => r.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType && r.DestinationId == variableSetId && keys.Contains(OctopusImportPriorMappings.Normalize(r.SourceId)));
    }

    // "Environment: Production; Role: web" — the deployment's own environment and channel by name, other values as stored.
    private static string ScopeSummary(VariableDto variable, VariableScopeContext deployment)
        => string.Join("; ", (variable.Scopes ?? []).Where(s => !string.IsNullOrWhiteSpace(s.ScopeValue)).GroupBy(s => s.ScopeType).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => $"{g.Key}: {string.Join(", ", g.Select(s => ScopeValueName(s, deployment)))}"));

    private static string ScopeValueName(VariableScopeDto scope, VariableScopeContext deployment) => scope.ScopeType switch
    {
        VariableScopeType.Environment when IsId(scope.ScopeValue, deployment?.EnvironmentId) => deployment.EnvironmentName ?? scope.ScopeValue,
        VariableScopeType.Channel when IsId(scope.ScopeValue, deployment?.ChannelId) => deployment.ChannelName ?? scope.ScopeValue,
        _ => scope.ScopeValue
    };

    private static bool IsId(string value, int? id) => id.HasValue && string.Equals(value?.Trim(), id.Value.ToString(), StringComparison.Ordinal);
}
