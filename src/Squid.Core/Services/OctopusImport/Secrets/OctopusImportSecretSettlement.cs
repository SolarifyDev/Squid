using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// "A value set in Squid wins for ever" applied the moment it is saved, not only when the next import happens to see it: a secret an
/// import left <c>Missing</c> that a Squid save gives a value is settled <c>Superseded</c> at once, so clearing it later (with no import
/// in between) is still the user's edit and no export file ever fills it again.
/// </summary>
public sealed class OctopusImportSecretSettlement(IOctopusImportSourceMapDataProvider sourceMap, IVariableDataProvider variables) : IVariableSetSavedHook
{
    public async Task OnVariableSetSavedAsync(int spaceId, int variableSetId, CancellationToken ct)
    {
        var rows = await sourceMap.GetByDestinationsAsync(spaceId, ImportedSecretPreflightRules.SourceTypes, [variableSetId], ct).ConfigureAwait(false);

        if (!rows.Any(IsMissingSecret)) return;

        var slots = await variables.GetSensitiveVariableSlotsAsync([variableSetId], ct).ConfigureAwait(false);
        var settled = OctopusImportSecretSettlements.SetInSquid(rows, slots, variableSetId);

        await sourceMap.SupersedeMissingSecretsAsync(spaceId, OctopusImportSourceMapQueries.VariableSecretSourceType, settled, ct).ConfigureAwait(false);
    }

    private static bool IsMissingSecret(OctopusImportSourceMapRow row)
        => row.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType && row.OutcomeState == OctopusImportSourceMapQueries.MissingOutcome;
}

/// <summary>Pure rule of <see cref="OctopusImportSecretSettlement"/>: which <c>Missing</c> rows of a set name a secret Squid holds a value for.</summary>
public static class OctopusImportSecretSettlements
{
    public static IReadOnlyList<string> SetInSquid(IReadOnlyList<OctopusImportSourceMapRow> rows, IReadOnlyList<SensitiveVariableSlot> slots, int variableSetId)
    {
        var ownerSetSourceIds = (rows ?? []).Where(r => r.SourceType == nameof(OctopusResourceKind.VariableSet) && r.DestinationId == variableSetId).Select(r => r.SourceId).ToList();
        var keys = (slots ?? []).Where(s => s.VariableSetId == variableSetId && s.HasValue).SelectMany(s => ownerSetSourceIds.Select(owner => OctopusImportPriorMappings.Normalize(OctopusImportSecretKeys.ForIdentity(owner, s.Identity)))).ToHashSet(StringComparer.Ordinal);

        return (rows ?? []).Where(r => r.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType && r.DestinationId == variableSetId && r.OutcomeState == OctopusImportSourceMapQueries.MissingOutcome && keys.Contains(OctopusImportPriorMappings.Normalize(r.SourceId))).Select(r => r.SourceId).ToList();
    }
}
