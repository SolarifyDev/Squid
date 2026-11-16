using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The resources the user decides about: a child an earlier import created that was deleted in Squid since, or a child added in Octopus
/// after its owner was imported. The policy says which resources are choosable and their default; the user's selection is an input to
/// the plan, read here only for a resource the policy itself made choosable, and only ever turns it into Create or Skip. What belongs
/// to a choosable resource (the actions of a step) follows it and carries its choice, with no selection of its own.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private static void ApplyPriorDecision(OctopusImportResourceResultDto result, OctopusResourceNode resource, OctopusImportPriorDecision decided, PreviewPlanningContext context)
    {
        var kept = KeepChildStillInSquid(resource, decided, context);

        if (IsCreatedUnderImportedProject(resource, kept) && context.TryGetConflict(resource.SourceId, out var channelConflict))
        {
            ApplyConflictAction(result, resource, channelConflict);
            return;
        }

        var chosen = ApplyChoice(result, resource, kept, context);

        ApplyDecision(result, chosen);

        // Whatever belongs to a skipped child is skipped with it.
        if (chosen.Action == OctopusImportPreviewAction.Skip) context.MarkScopeSkipped(result.SourceId);
    }

    // A channel Octopus added after its project's import is matched by name against that project's Squid channels, the same rule as every
    // shared kind: one match is linked (never a second channel of that name), several need a rename. Never a choice.
    private static bool IsCreatedUnderImportedProject(OctopusResourceNode resource, OctopusImportPriorDecision decision)
        => resource.Kind == OctopusResourceKind.Channel && decision.Action == OctopusImportPreviewAction.Create;

    // Saving an owner in Squid re-inserts its children with new ids, so their rows go stale: one the owner still holds (by name; a
    // variable by name and scope, one row each) is in Squid, never offered (ticking it would add it a second time). An owner itself
    // held only by name (a step made again in Squid) has no row to tell its children apart in, or to insert into: they are taken as in
    // Squid too.
    private static OctopusImportPriorDecision KeepChildStillInSquid(OctopusResourceNode resource, OctopusImportPriorDecision decision, PreviewPlanningContext context)
    {
        if (decision.Choice == null || !OctopusImportChildOwners.IsNamedChild(resource.Kind)) return decision;

        var ownerId = OctopusImportChildOwners.ImportedOwnerDestinationId(resource, context.Prior);

        return ownerId == 0 || context.IsStillInSquid(resource, ownerId) ? new OctopusImportPriorDecision(OctopusImportPreviewAction.AlreadyImported, null) : decision;
    }

    // The only place a selection is read. The decision's action is what a selected resource does; an unselected one writes nothing.
    private static OctopusImportPriorDecision ApplyChoice(OctopusImportResourceResultDto result, OctopusResourceNode resource, OctopusImportPriorDecision decision, PreviewPlanningContext context)
    {
        if (decision.Choice is not { } choice) return decision;

        var selected = context.Selections.SelectedOr(resource.SourceId, choice.DefaultSelected);
        var ownerName = OwnerName(resource, context);

        result.Choice = new OctopusImportChoiceDto { Reason = choice.Reason, Selected = selected, DefaultSelected = choice.DefaultSelected, OwnerName = ownerName };
        result.Diagnostics.Add(ChoiceDiagnostic(resource, choice.Reason, selected, ownerName));

        return selected ? decision with { Choice = null } : new OctopusImportPriorDecision(OctopusImportPreviewAction.Skip, null);
    }

    // Runs once every resource is decided, in rank order: an owner is marked before what it owns, so followers of followers follow the root.
    private static void MarkChoiceFollowers(IReadOnlyList<OctopusResourceNode> nodes, IReadOnlyList<OctopusImportResourceResultDto> previews)
    {
        var previewsById = previews.ToDictionary(p => p.SourceId, StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            var preview = previewsById[node.SourceId];
            var owner = OwnerPreview(node, previewsById);

            if (preview.Choice != null || owner?.Choice == null) continue;

            preview.Choice = Follower(owner);
        }
    }

    private static OctopusImportResourceResultDto OwnerPreview(OctopusResourceNode node, IReadOnlyDictionary<string, OctopusImportResourceResultDto> previewsById)
    {
        var ownerId = OctopusImportPriorMappingPolicy.OwnerSourceId(node);

        return string.IsNullOrWhiteSpace(ownerId) ? null : previewsById.GetValueOrDefault(ownerId);
    }

    private static OctopusImportChoiceDto Follower(OctopusImportResourceResultDto owner)
        => new() { Reason = owner.Choice.Reason, Selected = owner.Choice.Selected, DefaultSelected = owner.Choice.DefaultSelected, FollowsSourceId = owner.Choice.FollowsSourceId ?? owner.SourceId, OwnerName = owner.Choice.OwnerName };

    // ── Wording ──────────────────────────────────────────────────────────────

    private static OctopusImportDiagnosticDto ChoiceDiagnostic(OctopusResourceNode resource, OctopusImportChoiceReason reason, bool selected, string ownerName)
        => reason == OctopusImportChoiceReason.DeletedInSquid
            ? Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ChildDeletedInSquid, DeletedInSquidMessage(resource, selected), resource)
            : Diagnostic(selected ? OctopusImportCompatibilitySeverity.Info : OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.ChildAddedInOctopus, AddedInOctopusMessage(resource, selected, ownerName), resource);

    private static string DeletedInSquidMessage(OctopusResourceNode resource, bool selected)
    {
        var what = $"This {resource.Kind} was imported before and deleted (or renamed) in Squid.";

        return selected ? $"{what} It is created again.{SelectedConsequence(resource)}" : $"{what} It stays deleted. Tick it to create it again. It is offered again on every import.";
    }

    private static string AddedInOctopusMessage(OctopusResourceNode resource, bool selected, string ownerName)
    {
        var what = $"This {resource.Kind} was added in Octopus after {ownerName} was imported.";

        return selected ? $"{what} It is added; nothing already in Squid is changed or reordered.{SelectedConsequence(resource)}" : $"{what} Imported configuration is never changed unless you tick it. It is offered again on every import.";
    }

    private static string SelectedConsequence(OctopusResourceNode resource)
        => resource.Kind switch
        {
            OctopusResourceKind.Channel => " The releases and deployments imported on it are moved onto it.",
            OctopusResourceKind.LifecyclePhase => " Releases on this lifecycle will also have to pass this phase.",
            _ => string.Empty
        };

    // "Lifecycle 'Default'", "Step 'Deploy web'", "Project variables of 'Web'": what the resource belongs to, as the user knows it.
    private static string OwnerName(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        var ownerId = OctopusImportPriorMappingPolicy.OwnerSourceId(resource);
        var owner = context.FindNode(ownerId);

        return owner?.Kind switch
        {
            OctopusResourceKind.Lifecycle => $"Lifecycle '{owner.Name}'",
            OctopusResourceKind.DeploymentStep => $"Step '{owner.Name}'",
            OctopusResourceKind.DeploymentProcess => $"Deployment process of '{ProjectName(owner, context)}'",
            OctopusResourceKind.VariableSet => VariableSetName(owner, context),
            OctopusResourceKind.Project => $"Project '{owner.Name}'",
            _ => owner?.Name ?? ownerId
        };
    }

    private static string VariableSetName(OctopusResourceNode variableSet, PreviewPlanningContext context)
    {
        var libraryId = OctopusImportVariableOwnership.FindOwningLibraryId(variableSet, context.FindNode);

        return string.IsNullOrWhiteSpace(libraryId) ? $"Project variables of '{ProjectName(variableSet, context)}'" : $"Library variable set '{context.FindNode(libraryId)?.Name ?? libraryId}'";
    }

    private static string ProjectName(OctopusResourceNode owned, PreviewPlanningContext context)
        => context.FindNode(owned.OwnerProjectId)?.Name ?? owned.OwnerProjectId;
}
