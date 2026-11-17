using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.ChildInsert.Inserters;

/// <summary>
/// A step (with its actions) added into an imported deployment process: mapped the way a whole process is, but only this step is written,
/// and only diagnostics of its siblings are left out (a sibling the user did not select never blocks it).
/// </summary>
public sealed class StepInserter(IOctopusImportDeploymentProcessMapper processMapper, IDeploymentStepService stepService, IRepository repository) : IOctopusImportChildInserter
{
    public OctopusResourceKind Kind => OctopusResourceKind.DeploymentStep;

    public async Task<IReadOnlyDictionary<int, int>> SiblingOrdersAsync(int ownerId, CancellationToken ct)
        => await repository.QueryNoTracking<DeploymentStep>(s => s.ProcessId == ownerId).ToDictionaryAsync(s => s.Id, s => s.StepOrder, ct).ConfigureAwait(false);

    public async Task<OctopusImportInsertedChild> InsertAsync(OctopusImportChildInsert insert, CancellationToken ct)
    {
        var process = insert.Find(insert.Resource.ParentSourceId, OctopusResourceKind.DeploymentProcess);
        var mapping = processMapper.MapToCreateStepCommands(process, insert.IdMap, insert.SpaceId, OctopusImportActionTemplates.From(insert.Graph));
        var step = mapping.Steps.Single(s => StepSourceId(process, s) == insert.Resource.SourceId);
        var diagnostics = OctopusImportSiblingDiagnostics.Without(mapping.Diagnostics, ForeignIds(process, insert.Resource.SourceId));

        if (OctopusImportSiblingDiagnostics.HasBlocker(diagnostics)) return OctopusImportInsertedChild.Blocked(diagnostics);

        var created = await stepService.InsertDeploymentStepAsync(step.CreateCommand, insert.Order, ct).ConfigureAwait(false);

        return new OctopusImportInsertedChild(created.Data.Id, diagnostics, Followers(process, insert.Resource.SourceId, step, created.Data));
    }

    private static string StepSourceId(OctopusResourceNode process, OctopusImportDeploymentStepCommandMapping step)
        => OctopusImportChildSourceIds.Step(process, step.SourceStepId, step.SourceIndex);

    // The actions written with the step, by the Octopus source id the graph gives them.
    private static Dictionary<string, int> Followers(OctopusResourceNode process, string stepSourceId, OctopusImportDeploymentStepCommandMapping step, DeploymentStepDto created)
    {
        var actions = step.Actions.OrderBy(a => a.ActionIndex).ToList();
        var written = created.Actions.OrderBy(a => a.ActionOrder).ToList();

        return actions.Zip(written).ToDictionary(pair => OctopusImportChildSourceIds.Action(process, stepSourceId, pair.First.SourceActionId, pair.First.ActionIndex), pair => pair.Second.Id, StringComparer.OrdinalIgnoreCase);
    }

    // The source ids of every other step of the process and of their actions.
    private static IReadOnlySet<string> ForeignIds(OctopusResourceNode process, string stepSourceId)
    {
        var steps = process.GetSource<OctopusDeploymentProcessDto>()?.Steps ?? [];

        return steps.Select((step, index) => (Step: step, Id: OctopusImportChildSourceIds.Step(process, step.Id, index)))
            .Where(s => !string.Equals(s.Id, stepSourceId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => OctopusImportSiblingDiagnostics.StepIds(s.Step, s.Id, process.OwnerProjectId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
