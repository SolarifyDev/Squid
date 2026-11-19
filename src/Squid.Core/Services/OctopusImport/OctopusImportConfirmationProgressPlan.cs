using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// What a confirmation counts its progress by: the phase each resource kind belongs to, and how many items of each phase the validated
/// plan holds. Pure: the totals are known before the import starts.
/// </summary>
public static class OctopusImportConfirmationProgressPlan
{
    // A kind missing here is not counted: a ServerTask is written together with its Deployment (counting it would double the history),
    // snapshots ride on their release, and the rest are never imported as items of their own.
    private static readonly IReadOnlyDictionary<OctopusResourceKind, OctopusImportConfirmationPhase> Phases = new Dictionary<OctopusResourceKind, OctopusImportConfirmationPhase>
    {
        [OctopusResourceKind.ProjectGroup] = OctopusImportConfirmationPhase.ProjectGroups,
        [OctopusResourceKind.Environment] = OctopusImportConfirmationPhase.Environments,
        [OctopusResourceKind.Lifecycle] = OctopusImportConfirmationPhase.Lifecycles,
        [OctopusResourceKind.LifecyclePhase] = OctopusImportConfirmationPhase.Lifecycles,
        [OctopusResourceKind.Feed] = OctopusImportConfirmationPhase.Feeds,
        [OctopusResourceKind.Account] = OctopusImportConfirmationPhase.Accounts,
        [OctopusResourceKind.Team] = OctopusImportConfirmationPhase.Teams,
        [OctopusResourceKind.LibraryVariableSet] = OctopusImportConfirmationPhase.LibraryVariableSets,
        [OctopusResourceKind.Project] = OctopusImportConfirmationPhase.Projects,
        [OctopusResourceKind.DeploymentSettings] = OctopusImportConfirmationPhase.Projects,
        [OctopusResourceKind.Channel] = OctopusImportConfirmationPhase.Channels,
        [OctopusResourceKind.VariableSet] = OctopusImportConfirmationPhase.Variables,
        [OctopusResourceKind.Variable] = OctopusImportConfirmationPhase.Variables,
        [OctopusResourceKind.DeploymentProcess] = OctopusImportConfirmationPhase.Processes,
        [OctopusResourceKind.DeploymentStep] = OctopusImportConfirmationPhase.Processes,
        [OctopusResourceKind.DeploymentAction] = OctopusImportConfirmationPhase.Processes,
        [OctopusResourceKind.Release] = OctopusImportConfirmationPhase.Releases,
        [OctopusResourceKind.Deployment] = OctopusImportConfirmationPhase.Deployments
    };

    public static OctopusImportConfirmationPhase? PhaseOf(OctopusResourceKind kind) => Phases.TryGetValue(kind, out var phase) ? phase : null;

    /// <summary>Items per counted phase, each phase in the order its first item appears in the plan.</summary>
    public static IReadOnlyList<(OctopusImportConfirmationPhase Phase, int Total)> Totals(IReadOnlyList<OctopusResourceNode> ordered)
        => ordered.Select(r => PhaseOf(r.Kind)).Where(p => p != null).Select(p => p.Value).GroupBy(p => p).Select(g => (g.Key, g.Count())).ToList();

    /// <summary>Octopus project id → name, so an item can be shown with the project it belongs to.</summary>
    public static IReadOnlyDictionary<string, string> ProjectNames(OctopusResourceGraph graph)
        => graph.Resources.Where(r => r.Kind == OctopusResourceKind.Project && !string.IsNullOrWhiteSpace(r.SourceId)).GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
}
