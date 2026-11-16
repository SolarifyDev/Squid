using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.ChildInsert.Inserters;

/// <summary>
/// An action added into an imported step: mapped the way the whole process is, but only the action's own rows are written (the step and
/// its other actions are never rewritten), and only its own diagnostics count.
/// </summary>
public sealed class ActionInserter(IOctopusImportDeploymentProcessMapper processMapper, IDeploymentStepService stepService, IRepository repository) : IOctopusImportChildInserter
{
    public OctopusResourceKind Kind => OctopusResourceKind.DeploymentAction;

    public async Task<IReadOnlyDictionary<int, int>> SiblingOrdersAsync(int ownerId, CancellationToken ct)
        => await repository.QueryNoTracking<DeploymentAction>(a => a.StepId == ownerId).ToDictionaryAsync(a => a.Id, a => a.ActionOrder, ct).ConfigureAwait(false);

    public async Task<OctopusImportInsertedChild> InsertAsync(OctopusImportChildInsert insert, CancellationToken ct)
    {
        var step = insert.Find(insert.Resource.ParentSourceId, OctopusResourceKind.DeploymentStep);
        var process = insert.Find(step.ParentSourceId, OctopusResourceKind.DeploymentProcess);
        var mapping = processMapper.MapToCreateStepCommands(process, insert.IdMap, insert.SpaceId, OctopusImportActionTemplates.From(insert.Graph));
        var action = FindAction(mapping, process, step.SourceId, insert.Resource.SourceId);
        var diagnostics = OctopusImportSiblingDiagnostics.Without(mapping.Diagnostics, ForeignIds(process, insert.Resource));

        if (action == null || OctopusImportSiblingDiagnostics.HasBlocker(diagnostics)) return OctopusImportInsertedChild.Blocked(diagnostics);

        var id = await stepService.InsertActionAsync(insert.OwnerDestinationId, action.Action, insert.Order, ct).ConfigureAwait(false);

        return new OctopusImportInsertedChild(id, diagnostics, new Dictionary<string, int>());
    }

    private static OctopusImportDeploymentActionModelMapping FindAction(OctopusImportDeploymentProcessMappingResult mapping, OctopusResourceNode process, string stepSourceId, string actionSourceId)
    {
        var step = mapping.Steps.Single(s => OctopusResourceGraphBuilder.BuildChildSourceId(process.SourceId, "step", s.SourceStepId, s.SourceIndex) == stepSourceId);

        return step.Actions.SingleOrDefault(a => OctopusResourceGraphBuilder.BuildChildSourceId(stepSourceId, "action", a.SourceActionId, a.ActionIndex) == actionSourceId);
    }

    // Every step and action id of the process except the action's own.
    private static IReadOnlySet<string> ForeignIds(OctopusResourceNode process, OctopusResourceNode action)
    {
        var steps = process.GetSource<OctopusDeploymentProcessDto>()?.Steps ?? [];
        var own = new[] { action.SourceId, action.GetSource<OctopusDeploymentActionDto>()?.Id };

        return steps.SelectMany((step, index) => OctopusImportSiblingDiagnostics.StepIds(step, OctopusResourceGraphBuilder.BuildChildSourceId(process.SourceId, "step", step.Id, index)))
            .Except(own.Where(id => id != null), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
