namespace Squid.Core.Services.OctopusImport.Octopus;

/// <summary>
/// Single source of truth for the source id of a child resource (a step, action, variable or lifecycle phase). Octopus copies child
/// ids verbatim when it clones a project (and keeps them when a lifecycle or library set is copied through the API), so a raw child id
/// cannot name one child. Every child id is therefore qualified by its Octopus owner — the project, library variable set or lifecycle:
/// <c>{owner}/{token}/{id}</c>. A child Octopus gives no id keeps its positional id under its parent: <c>{parent}/{token}-{n}</c>.
/// The tokens are pinned by a test against the migration that rewrote ids recorded before (20261117_qualify_octopus_import_child_source_ids.sql).
/// </summary>
public static class OctopusImportChildSourceIds
{
    public const string StepToken = "step";

    public const string ActionToken = "action";

    public const string VariableToken = "variable";

    public const string PhaseToken = "phase";

    /// <summary>The child's graph source id: qualified by its owner (its parent when the owner is unknown), or positional when Octopus gives no id.</summary>
    public static string Qualify(string ownerId, string parentSourceId, string childToken, string octopusId, int index)
    {
        if (string.IsNullOrWhiteSpace(octopusId)) return $"{parentSourceId}/{childToken}-{index + 1}";

        var owner = string.IsNullOrWhiteSpace(ownerId) ? parentSourceId : ownerId;

        return $"{owner}/{childToken}/{octopusId}";
    }

    /// <summary>A step of a deployment process node, as the graph names it.</summary>
    public static string Step(OctopusResourceNode process, string octopusStepId, int index) => Qualify(process?.OwnerProjectId, process?.SourceId, StepToken, octopusStepId, index);

    /// <summary>An action of a step of a deployment process node, as the graph names it.</summary>
    public static string Action(OctopusResourceNode process, string stepSourceId, string octopusActionId, int index) => Qualify(process?.OwnerProjectId, stepSourceId, ActionToken, octopusActionId, index);

    /// <summary>A variable of a variable set, as the graph names it.</summary>
    public static string Variable(OctopusVariableSetDto variableSet, string octopusVariableId, int index) => Qualify(variableSet?.OwnerId, variableSet?.Id, VariableToken, octopusVariableId, index);

    /// <summary>
    /// The source id an <c>Action</c> scope value names: the action of the variable's own project. A library variable has no project,
    /// so its action scope stays as it is and never maps (Octopus does not allow action scopes on library variable sets).
    /// </summary>
    public static string ScopedAction(string ownerProjectId, string actionId)
        => string.IsNullOrWhiteSpace(ownerProjectId) || string.IsNullOrWhiteSpace(actionId) ? actionId : Qualify(ownerProjectId, null, ActionToken, actionId, 0);

    /// <summary>The Octopus id inside a source id: the part after the last '/' (the whole value when it has none).</summary>
    public static string OctopusId(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return sourceId;

        var slash = sourceId.LastIndexOf('/');

        return slash < 0 ? sourceId : sourceId[(slash + 1)..];
    }
}
