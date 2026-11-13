using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Secrets the Octopus server withheld on resources this confirmation CREATED: the fillable ones (sensitive variable values,
/// feed passwords) are recorded as <c>*Secret</c> source-map rows in state <c>Missing</c>, so a later export-file import can fill
/// exactly those and nothing else; every one of them is listed in the result's <c>MissingSensitiveValues</c> (names only). A resource
/// only reused or already imported is never listed — its secret is whatever Squid holds.
/// </summary>
public sealed partial class OctopusImportConfirmationOrchestrator
{
    private OctopusExportSecrets Secrets => _secretsAccessor?.Secrets ?? OctopusExportSecrets.None;

    private IEnumerable<OctopusImportSourceMapRow> SecretRows(ConfirmationExecutionContext execution)
    {
        foreach (var variable in CreatedWithheldVariables(execution))
        {
            var key = VariableSecretKey(execution, variable);

            if (key != null && execution.IdMap.TryGetDestinationId(variable.ParentSourceId, nameof(OctopusResourceKind.VariableSet), out var variableSetId))
                yield return SecretRow(execution, OctopusImportSourceMapQueries.VariableSecretSourceType, key, variableSetId);
        }

        foreach (var feed in CreatedWithheldFeeds(execution))
        {
            if (execution.IdMap.TryGetDestinationId(feed, out var feedId))
                yield return SecretRow(execution, OctopusImportSourceMapQueries.FeedSecretSourceType, feed.SourceId, feedId);
        }
    }

    private static OctopusImportSourceMapRow SecretRow(ConfirmationExecutionContext execution, string sourceType, string sourceId, int destinationId)
        => new(execution.DestinationSpaceId, sourceType, sourceId, destinationId, OctopusImportSourceMapQueries.MissingOutcome);

    private IEnumerable<OctopusResourceNode> CreatedWithheldVariables(ConfirmationExecutionContext execution)
        => execution.CurrentResources.Where(r => r.Kind == OctopusResourceKind.Variable && IsCreated(execution, r) && IsWithheldVariable(r));

    private bool IsWithheldVariable(OctopusResourceNode resource)
    {
        var variable = resource.GetSource<OctopusVariableDto>();

        return OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable) && OctopusImportSensitiveValues.IsWithheldByServer(variable, Secrets);
    }

    private IEnumerable<OctopusResourceNode> CreatedWithheldFeeds(ConfirmationExecutionContext execution)
        => Secrets.IsWithheldByServer ? execution.CurrentResources.Where(r => r.Kind == OctopusResourceKind.Feed && IsCreated(execution, r) && OctopusImportFeedCredentials.IsPasswordWithheld(r.GetSource<OctopusFeedDto>())) : [];

    private static bool IsCreated(ConfirmationExecutionContext execution, OctopusResourceNode resource) => execution.OutcomeOf(resource.SourceId) == OctopusImportResourceOutcomeState.Created;

    private static string VariableSecretKey(ConfirmationExecutionContext execution, OctopusResourceNode variable)
        => OctopusImportSecretKeys.ForVariable(variable.ParentSourceId, variable.GetSource<OctopusVariableDto>(), (sourceId, kind) => execution.IdMap.TryGetDestinationId(sourceId, kind.ToString(), out var id) ? id : null);

    // ── Missing secrets listed in the result ────────────────────────────────

    private void RecordMissingSensitiveValues(ConfirmationExecutionContext execution)
    {
        var missing = CreatedWithheldVariables(execution).Select(v => MissingVariableValue(execution, v))
            .Concat(execution.CurrentResources.Where(r => IsCreated(execution, r)).SelectMany(r => MissingResourceValues(execution, r)));

        execution.Result.MissingSensitiveValues = missing.ToList();
    }

    private static OctopusImportMissingSensitiveValueDto MissingVariableValue(ConfirmationExecutionContext execution, OctopusResourceNode resource)
    {
        var variable = resource.GetSource<OctopusVariableDto>();
        var owner = VariableOwner(execution, resource);

        return new OctopusImportMissingSensitiveValueDto
        {
            Kind = OctopusImportRequiredInputKind.SensitiveVariableValue,
            Name = variable.Name,
            OwnerName = owner?.Name,
            OwnerKind = owner?.Kind.ToString(),
            Field = OctopusImportSensitiveValues.ValueMember,
            ScopeSummary = ScopeSummary(execution, variable),
            DestinationId = DestinationOf(execution, resource),
            DestinationOwnerId = owner == null ? null : DestinationOf(execution, owner),
            Fillable = VariableSecretKey(execution, resource) != null
        };
    }

    private IEnumerable<OctopusImportMissingSensitiveValueDto> MissingResourceValues(ConfirmationExecutionContext execution, OctopusResourceNode resource)
    {
        var owner = string.IsNullOrWhiteSpace(resource.OwnerProjectId) ? null : GraphNode(execution, resource.OwnerProjectId, OctopusResourceKind.Project);

        return OctopusImportWithheldSecretInputs.For(resource, Secrets).Select(input => new OctopusImportMissingSensitiveValueDto
        {
            Kind = input.Kind,
            Name = resource.Name,
            OwnerName = owner?.Name,
            OwnerKind = owner?.Kind.ToString(),
            Field = input.FieldName,
            ScopeSummary = string.Empty,
            DestinationId = DestinationOf(execution, resource),
            DestinationOwnerId = owner == null ? null : DestinationOf(execution, owner),
            Fillable = input.Fillable
        });
    }

    // A project's variable belongs to the project; a library's to the library variable set that owns its variable set.
    private static OctopusResourceNode VariableOwner(ConfirmationExecutionContext execution, OctopusResourceNode variable)
    {
        if (!string.IsNullOrWhiteSpace(variable.OwnerProjectId)) return GraphNode(execution, variable.OwnerProjectId, OctopusResourceKind.Project);

        var libraryId = GraphNode(execution, variable.ParentSourceId, OctopusResourceKind.VariableSet)?.GetSource<OctopusVariableSetDto>()?.OwnerId;

        return GraphNode(execution, libraryId, OctopusResourceKind.LibraryVariableSet);
    }

    private static int? DestinationOf(ConfirmationExecutionContext execution, OctopusResourceNode resource)
        => execution.IdMap.TryGetDestinationId(resource, out var id) ? id : null;

    private static OctopusResourceNode GraphNode(ConfirmationExecutionContext execution, string sourceId, OctopusResourceKind kind)
        => string.IsNullOrWhiteSpace(sourceId) ? null : execution.Request.Graph.Resources.FirstOrDefault(r => r.Kind == kind && string.Equals(r.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

    // "Environment: Production, Staging; Role: web" — scope targets by name where the export/API carries them.
    private static string ScopeSummary(ConfirmationExecutionContext execution, OctopusVariableDto variable)
        => string.Join("; ", (variable.Scope ?? []).Where(s => s.Value is { Count: > 0 }).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => $"{s.Key}: {string.Join(", ", s.Value.Select(v => ScopeValueName(execution, v)))}"));

    private static string ScopeValueName(ConfirmationExecutionContext execution, string value)
        => execution.Request.Graph.Resources.FirstOrDefault(r => string.Equals(r.SourceId, value, StringComparison.OrdinalIgnoreCase))?.Name ?? value;
}
