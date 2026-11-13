using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Whole-space scope (D4). Every resource Squid can import is imported, used or not. A resource no imported project
/// uses (<see cref="OctopusImportReferenceScope"/>) that Squid cannot write — an unsupported feed or account type, a
/// library whose variables Squid cannot hold — is skipped with a reason instead of blocking every project, and so is
/// an unused resource whose name matches several Squid resources. The same resource used by a project blocks in the
/// preview, with the rule confirmation would apply. Octopus's built-in package repository is never imported.
/// Decided after "already imported" (which always wins) and before the create/reuse decision.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private const int GroupedNameLimit = 10;

    private sealed record ScopeReason(string BlockerCode, string Message);

    private static OctopusImportPriorDecision DecideScope(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        if (SkippedOwnerOf(resource, context) is { } owner) return SkippedWithOwner(resource, owner);

        var referenced = context.IsReferenced(resource.SourceId);

        if (IsBuiltInFeed(resource)) return referenced ? Blocked(resource, new ScopeReason(OctopusImportPreviewDiagnosticCodes.BuiltInFeedReferenced, OctopusBuiltInFeeds.ReferencedMessage)) : BuiltInFeedExcluded(resource);
        if (IsReusedByName(resource, context)) return null;
        if (UnsupportedReason(resource) is { } reason) return referenced ? Blocked(resource, reason) : SkippedUnreferenced(resource, reason.Message);
        if (referenced) return null;
        if (UnusedLibraryReason(resource, context) is { } libraryReason) return SkippedUnreferenced(resource, libraryReason);
        if (HasAmbiguousConflict(resource, context)) return SkippedNameConflict(resource);

        return context.TargetsOf(resource.SourceId).FirstOrDefault(context.IsScopeSkipped) is { } target ? SkippedUnreferenced(resource, UsesSkippedMessage(context.FindNode(target))) : null;
    }

    private static void ApplyScopeDecision(OctopusImportResourceResultDto result, OctopusImportPriorDecision decision, PreviewPlanningContext context)
    {
        ApplyDecision(result, decision);

        if (decision.Action == OctopusImportPreviewAction.Blocked) result.OutcomeState = OctopusImportResourceOutcomeState.Blocked;
        if (decision.Action == OctopusImportPreviewAction.Skip) context.MarkScopeSkipped(result.SourceId);
    }

    private static OctopusResourceNode SkippedOwnerOf(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        var ownerId = OctopusImportPriorMappingPolicy.OwnerSourceId(resource);

        return context.IsScopeSkipped(ownerId) ? context.FindNode(ownerId) : null;
    }

    private static bool IsBuiltInFeed(OctopusResourceNode resource)
        => resource.Kind == OctopusResourceKind.Feed && OctopusBuiltInFeeds.IsBuiltInType(resource.GetSource<OctopusFeedDto>()?.FeedType);

    // One Squid resource with the same name is reused as-is: nothing is written, so nothing can block.
    private static bool IsReusedByName(OctopusResourceNode resource, PreviewPlanningContext context)
        => resource.Kind != OctopusResourceKind.Project && context.TryGetConflict(resource.SourceId, out var conflict) && conflict.Matches.Count == 1;

    private static bool HasAmbiguousConflict(OctopusResourceNode resource, PreviewPlanningContext context)
        => context.TryGetConflict(resource.SourceId, out var conflict) && conflict.Matches.Count != 1;

    // The same rules the feed and account mappers apply at confirmation.
    private static ScopeReason UnsupportedReason(OctopusResourceNode resource)
        => resource.Kind switch
        {
            OctopusResourceKind.Feed => FeedReason(resource.GetSource<OctopusFeedDto>()),
            OctopusResourceKind.Account => AccountReason(resource.GetSource<OctopusAccountDto>()),
            _ => null
        };

    private static ScopeReason FeedReason(OctopusFeedDto feed)
        => feed == null || OctopusImportFeedMapper.SquidFeedType(feed) != null ? null : new ScopeReason(OctopusImportFeedMappingDiagnosticCodes.UnsupportedFeedType, OctopusImportFeedMapper.UnsupportedFeedTypeMessage(feed));

    private static ScopeReason AccountReason(OctopusAccountDto account)
        => account == null || OctopusImportExternalResourceShellMapper.SquidAccountType(account.AccountType) != null ? null : new ScopeReason(OctopusImportExternalResourceShellMapper.UnsupportedAccountTypeCode, OctopusImportExternalResourceShellMapper.UnsupportedAccountTypeMessage(account.AccountType));

    // A used library is not judged here: its variables carry their own blockers (type below, scopes in validation).
    private static string UnusedLibraryReason(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        if (resource.Kind != OctopusResourceKind.LibraryVariableSet) return null;

        var library = resource.GetSource<OctopusLibraryVariableSetDto>();
        var variables = context.FindNode(library?.VariableSetId, OctopusResourceKind.VariableSet)?.GetSource<OctopusVariableSetDto>()?.Variables ?? [];

        return variables.Select(v => UnwritableVariableReason(v, context)).FirstOrDefault(r => r != null);
    }

    private static string UnwritableVariableReason(OctopusVariableDto variable, PreviewPlanningContext context)
    {
        if (OctopusImportVariableMapper.SquidVariableType(variable) == null) return OctopusImportVariableMapper.UnsupportedVariableTypeMessage(variable);

        var scope = OctopusImportVariableScopeRule.ResolveAll(variable.Scope, (sourceId, kind) => context.Resolves(kind, sourceId) && OctopusImportVariableScopeRule.IsImportedBefore(kind, OctopusResourceKind.LibraryVariableSet)).FirstOrDefault(r => r.Blocks);

        return scope == null ? null : $"Octopus variable '{variable.Name}' is scoped to {scope.ScopeKey} '{scope.ScopeValue}', which Squid cannot represent for this import.";
    }

    private static OctopusImportPriorDecision Blocked(OctopusResourceNode resource, ScopeReason reason)
        => new(OctopusImportPreviewAction.Blocked, null, Diagnostic(OctopusImportCompatibilitySeverity.Blocker, reason.BlockerCode, $"{reason.Message} An imported project uses it, so the import is blocked until it is changed in Octopus.", resource));

    private static OctopusImportPriorDecision SkippedUnreferenced(OctopusResourceNode resource, string reason)
        => new(OctopusImportPreviewAction.Skip, null, Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.UnreferencedResourceSkipped, $"{reason} No imported project uses this {resource.Kind}, so it is skipped instead of blocking the import.", resource));

    private static OctopusImportPriorDecision SkippedNameConflict(OctopusResourceNode resource)
        => new(OctopusImportPreviewAction.Skip, null, Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.UnreferencedNameConflictSkipped, $"Several Squid {resource.Kind} resources match the name of this Octopus {resource.Kind}. No imported project uses it, so it is skipped instead of asking for a rename.", resource));

    private static OctopusImportPriorDecision SkippedWithOwner(OctopusResourceNode resource, OctopusResourceNode owner)
        => new(OctopusImportPreviewAction.Skip, null, Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.SkippedWithOwner, $"Skipped together with Octopus {owner.Kind} '{owner.Name ?? owner.SourceId}', which this {resource.Kind} belongs to.", resource));

    private static OctopusImportPriorDecision BuiltInFeedExcluded(OctopusResourceNode resource)
        => new(OctopusImportPreviewAction.Skip, null, Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.BuiltInFeedExcluded, OctopusBuiltInFeeds.ExcludedMessage, resource));

    private static string UsesSkippedMessage(OctopusResourceNode target)
        => $"It uses Octopus {target?.Kind} '{target?.Name ?? target?.SourceId}', which is skipped.";

    // ── Grouped plan diagnostics ───────────────────────────────────────────

    // One Warning per kind, so a whole space with hundreds of machines reads as one line per kind, not one per resource.
    private static IEnumerable<OctopusImportDiagnosticDto> GroupedScopeDiagnostics(IReadOnlyList<OctopusImportResourceResultDto> resources)
    {
        var unreferenced = resources.Where(r => r.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourceSkipped));
        var unsupported = resources.Where(r => r.PreviewAction == OctopusImportPreviewAction.Unsupported);

        return GroupByKind(unreferenced, OctopusImportPreviewDiagnosticCodes.UnreferencedResourcesSkipped, "are skipped because Squid cannot import them and no imported project uses them")
            .Concat(GroupByKind(unsupported, OctopusImportPreviewDiagnosticCodes.UnsupportedResourcesDetected, "are not imported by Squid; configure them in Squid if they are needed"));
    }

    private static IEnumerable<OctopusImportDiagnosticDto> GroupByKind(IEnumerable<OctopusImportResourceResultDto> resources, string code, string outcome)
        => resources
            .GroupBy(r => r.SourceType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Warning,
                Code = code,
                ResourceType = g.Key,
                Message = $"{g.Count()} Octopus {g.Key} resource(s) {outcome}: {NameList(g)}."
            }));

    private static string NameList(IEnumerable<OctopusImportResourceResultDto> resources)
    {
        var names = resources.Select(r => r.SourceName ?? r.SourceId).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", names.Take(GroupedNameLimit));

        return names.Count > GroupedNameLimit ? $"{shown} and {names.Count - GroupedNameLimit} more" : shown;
    }
}
