using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The children Squid holds as rows of an owner that keeps its own id when it is saved in Squid: a process's steps, a step's actions,
/// a lifecycle's phases, a variable set's variables. Saving the owner re-inserts them, so a child is told apart by its name under the
/// owner's live row, and a child the user selects is inserted into that owner.
/// </summary>
public static class OctopusImportChildOwners
{
    private static readonly IReadOnlyDictionary<OctopusResourceKind, OctopusResourceKind> OwnerKinds = new Dictionary<OctopusResourceKind, OctopusResourceKind>
    {
        [OctopusResourceKind.DeploymentStep] = OctopusResourceKind.DeploymentProcess,
        [OctopusResourceKind.DeploymentAction] = OctopusResourceKind.DeploymentStep,
        [OctopusResourceKind.LifecyclePhase] = OctopusResourceKind.Lifecycle,
        [OctopusResourceKind.Variable] = OctopusResourceKind.VariableSet
    };

    public static bool IsNamedChild(OctopusResourceKind kind) => OwnerKinds.ContainsKey(kind);

    public static OctopusResourceKind OwnerKind(OctopusResourceKind kind)
        => OwnerKinds.TryGetValue(kind, out var owner) ? owner : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only steps, actions, lifecycle phases and variables live in an owner's rows.");

    /// <summary>The Squid row of the child's owner an earlier import recorded (live), or 0 when there is none.</summary>
    public static int ImportedOwnerDestinationId(OctopusResourceNode node, OctopusImportPriorMappings prior)
        => prior.TryGet(OwnerKind(node.Kind).ToString(), OctopusImportPriorMappingPolicy.OwnerSourceId(node), out var owner) ? owner.DestinationId : 0;
}
