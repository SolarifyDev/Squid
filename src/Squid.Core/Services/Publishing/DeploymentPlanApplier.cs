using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Models.Publishing;
using ProjectEntity = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.Core.Services.Publishing;

public interface IDeploymentPlanApplier : IScopedDependency
{
    /// <summary>
    /// Makes the package's project and deployment process match the plan, through the ordinary project and step services. The project is found by the marker publishing
    /// leaves in it, or created; the steps publishing owns are rebuilt only when the plan's fingerprint differs from the one recorded, so publishing the same package again changes nothing
    /// structural. Steps a person added are kept, the roles other environments deploy to are never changed, and a version that already has a release leaves the process as that release was made with it.
    /// </summary>
    Task<AppliedPlan> ApplyAsync(PlanApplication application, CancellationToken ct);
}

public sealed class DeploymentPlanApplier(IProjectService projects, IProjectDataProvider projectData, IPublishedProjectDataProvider publishedProjects, IDeploymentStepService steps, IDeploymentStepDataProvider stepData, IDeploymentStepPropertyDataProvider stepProperties, IReleaseDataProvider releaseData, IChannelDataProvider channels) : IDeploymentPlanApplier
{
    private const string StepType = "Action";
    private const string StepCondition = "Success";
    private const string StepStartTrigger = "StartAfterPrevious";
    private const string StepPackageRequirement = "LetSquidDecide";

    private ApplierContext _ctx;

    public async Task<AppliedPlan> ApplyAsync(PlanApplication application, CancellationToken ct)
    {
        _ctx = new ApplierContext { Application = application, Plan = application.Plan };

        await LoadExistingProjectAsync(ct).ConfigureAwait(false);
        await EnsureProjectAsync(ct).ConfigureAwait(false);
        await ResolveChannelAsync(ct).ConfigureAwait(false);
        await LoadOwnedStepsAsync(ct).ConfigureAwait(false);
        KeepTheRolesOtherEnvironmentsUse();
        await SyncProcessAsync(ct).ConfigureAwait(false);
        await ApplyInputsAsync(ct).ConfigureAwait(false);

        return BuildResult();
    }

    private async Task LoadExistingProjectAsync(CancellationToken ct)
    {
        _ctx.Project = await publishedProjects.FindByPackageAsync(_ctx.Application.SpaceId, _ctx.Application.PackageId, ct).ConfigureAwait(false);
    }

    private async Task EnsureProjectAsync(CancellationToken ct)
    {
        if (_ctx.Project != null) return;

        var name = string.IsNullOrWhiteSpace(_ctx.Application.ProjectName) ? _ctx.Application.PackageId : _ctx.Application.ProjectName.Trim();

        if (await publishedProjects.ProjectNameExistsAsync(_ctx.Application.SpaceId, name, ct).ConfigureAwait(false))
            throw new PublishProjectNameConflictException(name);

        var model = await BuildProjectModelAsync(name, ct).ConfigureAwait(false);
        var created = await projects.CreateProjectAsync(new CreateProjectCommand { Project = model }, ct).ConfigureAwait(false);

        _ctx.Project = await projectData.GetProjectByIdAsync(created.Data.Id, ct).ConfigureAwait(false);
        _ctx.ProjectCreated = true;
    }

    private async Task<CreateOrUpdateProjectModel> BuildProjectModelAsync(string name, CancellationToken ct)
    {
        var spaceId = _ctx.Application.SpaceId;

        var groupId = _ctx.Application.ProjectGroupId ?? await publishedProjects.FirstProjectGroupIdAsync(spaceId, ct).ConfigureAwait(false) ?? throw new PublishDefaultsMissingException("project group", spaceId);
        var lifecycleId = _ctx.Application.LifecycleId ?? await publishedProjects.FirstLifecycleIdAsync(spaceId, _ctx.Application.EnvironmentId, ct).ConfigureAwait(false) ?? throw new PublishDefaultsMissingException($"lifecycle that lists environment {_ctx.Application.EnvironmentId}", spaceId);

        // The hash is empty until the process is built, so a run that stops half way is rebuilt by the next one.
        return new CreateOrUpdateProjectModel { Name = name, SpaceId = spaceId, ProjectGroupId = groupId, LifecycleId = lifecycleId, Json = Marker(string.Empty).ToJson() };
    }

    // The steps publishing built carry OwnedStepProperty. A process with none marked was built before the marker existed, so all of its steps are publishing's.
    private async Task LoadOwnedStepsAsync(CancellationToken ct)
    {
        var existing = await stepData.GetDeploymentStepsByProcessIdAsync(_ctx.Project.DeploymentProcessId, ct).ConfigureAwait(false) ?? [];
        var properties = await stepProperties.GetDeploymentStepPropertiesByStepIdsAsync(existing.Select(s => s.Id).ToList(), ct).ConfigureAwait(false) ?? [];
        var marked = properties.Where(p => p.PropertyName == PublishedProjectMarker.OwnedStepProperty).Select(p => p.StepId).ToHashSet();

        _ctx.StepProperties = properties;
        _ctx.OwnedSteps = marked.Count == 0 ? existing : existing.Where(s => marked.Contains(s.Id)).ToList();
        _ctx.KeptStepCount = existing.Count - _ctx.OwnedSteps.Count;
    }

    // The process is shared by every environment, so its roles are too: publishing for one environment must not retarget the others. Roles the steps already target
    // are kept (asking for some of them is fine, machines match any role of a step); asking for a role no step targets is refused.
    private void KeepTheRolesOtherEnvironmentsUse()
    {
        var existing = ExistingRoles();

        if (existing.Count == 0) return;

        var requested = _ctx.Application.Plan.Steps.SelectMany(s => s.TargetRoles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (requested.Any(r => !existing.Contains(r, StringComparer.OrdinalIgnoreCase)))
            throw new PublishTargetRolesConflictException(existing, requested);

        _ctx.Plan = WithRoles(_ctx.Application.Plan, existing);
    }

    private List<string> ExistingRoles()
    {
        var ownedIds = _ctx.OwnedSteps.Select(s => s.Id).ToHashSet();
        var values = _ctx.StepProperties.Where(p => ownedIds.Contains(p.StepId) && p.PropertyName == SpecialVariables.Step.TargetRoles).Select(p => p.PropertyValue);

        return values.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DeploymentPlan WithRoles(DeploymentPlan plan, List<string> roles)
    {
        return new DeploymentPlan { Inputs = plan.Inputs, Steps = plan.Steps.Select(s => new PlanStep { Name = s.Name, TargetRoles = roles, Actions = s.Actions }).ToList() };
    }

    private async Task SyncProcessAsync(CancellationToken ct)
    {
        var hash = DeploymentPlanFingerprint.Of(_ctx.Plan);

        if (!NeedsRebuild(hash)) return;
        if (await ReleaseOfThisVersionExistsAsync(ct).ConfigureAwait(false)) return;

        await DeleteOwnedStepsAsync(ct).ConfigureAwait(false);
        await CreateStepsAsync(ct).ConfigureAwait(false);
        await RecordMarkerAsync(hash, ct).ConfigureAwait(false);

        _ctx.ProcessUpdated = !_ctx.ProjectCreated;
    }

    private bool NeedsRebuild(string hash)
    {
        var marker = PublishedProjectMarker.TryParse(_ctx.Project.Json);

        return marker?.PlanHash != hash || !string.Equals(marker.Recipe, _ctx.Application.RecipeKind, StringComparison.Ordinal);
    }

    // Publishing a version that has a release is promoting that release: it deploys with the process snapshot it was made with, so rebuilding the process now only
    // changes what the next new version will use, and would drag an older version's plan over a newer one's.
    private async Task<bool> ReleaseOfThisVersionExistsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_ctx.Application.PackageVersion)) return false;

        return await releaseData.GetReleaseByVersionAsync(_ctx.Project.Id, _ctx.ChannelId, _ctx.Application.PackageVersion, ct).ConfigureAwait(false) != null;
    }

    private async Task DeleteOwnedStepsAsync(CancellationToken ct)
    {
        if (_ctx.OwnedSteps.Count == 0) return;

        _ctx.FirstOwnedOrder = _ctx.OwnedSteps.Min(s => s.StepOrder);

        await stepData.DeleteDeploymentStepsAsync(_ctx.OwnedSteps, cancellationToken: ct).ConfigureAwait(false);
    }

    // The rebuilt steps take the place of the first replaced one, so the steps a person added keep their places around them.
    private async Task CreateStepsAsync(CancellationToken ct)
    {
        for (var i = 0; i < _ctx.Plan.Steps.Count; i++)
            await CreateStepAsync(_ctx.Plan.Steps[i], i, ct).ConfigureAwait(false);
    }

    private async Task CreateStepAsync(PlanStep step, int index, CancellationToken ct)
    {
        var command = new CreateDeploymentStepCommand { SpaceId = _ctx.Application.SpaceId, ProcessId = _ctx.Project.DeploymentProcessId, Step = ToStepModel(step) };

        if (_ctx.KeptStepCount == 0 || _ctx.FirstOwnedOrder == null)
            await steps.CreateDeploymentStepAsync(command, ct).ConfigureAwait(false);
        else
            await steps.InsertDeploymentStepAsync(command, _ctx.FirstOwnedOrder.Value + index, ct).ConfigureAwait(false);
    }

    private async Task RecordMarkerAsync(string hash, CancellationToken ct)
    {
        _ctx.Project.Json = Marker(hash).ToJson(_ctx.Project.Json);

        await projectData.UpdateProjectAsync(_ctx.Project, cancellationToken: ct).ConfigureAwait(false);
    }

    // The values the recipe asked for are project variables scoped to this environment, so one shared process serves every environment with its own.
    private async Task ApplyInputsAsync(CancellationToken ct)
    {
        foreach (var (name, value) in _ctx.Application.InputValues)
            _ctx.InputsChanged |= await publishedProjects.UpsertEnvironmentVariableAsync(_ctx.Project.VariableSetId, PlanInputVariables.Name(name), _ctx.Application.EnvironmentId, value, ct).ConfigureAwait(false);
    }

    private async Task ResolveChannelAsync(CancellationToken ct)
    {
        var channel = await channels.GetDefaultChannelByProjectIdAsync(_ctx.Project.Id, ct).ConfigureAwait(false);

        _ctx.ChannelId = channel?.Id ?? throw new DeploymentEntityNotFoundException("Default channel of project", _ctx.Project.Id);
    }

    private AppliedPlan BuildResult()
    {
        var packageActions = _ctx.Plan.Steps.SelectMany(s => s.Actions).Where(a => a.Package != null).Select(a => a.Name).ToList();

        return new AppliedPlan { ProjectId = _ctx.Project.Id, ChannelId = _ctx.ChannelId, ProjectCreated = _ctx.ProjectCreated, ProcessUpdated = _ctx.ProcessUpdated, InputsChanged = _ctx.InputsChanged, PackageActionNames = packageActions };
    }

    private PublishedProjectMarker Marker(string hash) => new(_ctx.Application.PackageId, _ctx.Application.RecipeKind, hash);

    private CreateOrUpdateDeploymentStepModel ToStepModel(PlanStep step)
    {
        var roles = new StepPropertyModel { PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = string.Join(',', step.TargetRoles) };
        var owned = new StepPropertyModel { PropertyName = PublishedProjectMarker.OwnedStepProperty, PropertyValue = "true" };

        return new CreateOrUpdateDeploymentStepModel
        {
            Name = step.Name,
            StepType = StepType,
            Condition = StepCondition,
            StartTrigger = StepStartTrigger,
            PackageRequirement = StepPackageRequirement,
            IsRequired = true,
            Properties = [roles, owned],
            Actions = step.Actions.Select(ToActionModel).ToList()
        };
    }

    private CreateOrUpdateDeploymentActionModel ToActionModel(PlanAction action)
    {
        var properties = action.Properties.Select(p => new ActionPropertyModel { PropertyName = p.Key, PropertyValue = p.Value }).ToList();

        if (action.Package != null) properties.AddRange(PackageProperties(action.Package));

        return new CreateOrUpdateDeploymentActionModel { Name = action.Name, ActionType = action.ActionType, IsRequired = true, Properties = properties };
    }

    private IEnumerable<ActionPropertyModel> PackageProperties(PlanPackageReference package)
    {
        yield return new ActionPropertyModel { PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = _ctx.Application.FeedId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        yield return new ActionPropertyModel { PropertyName = SpecialVariables.Action.PackageId, PropertyValue = package.PackageId };
    }

    private sealed class ApplierContext
    {
        // Input
        public PlanApplication Application { get; set; }
        public DeploymentPlan Plan { get; set; }

        // Process
        public List<DeploymentStep> OwnedSteps { get; set; } = new();
        public List<DeploymentStepProperty> StepProperties { get; set; } = new();
        public int KeptStepCount { get; set; }
        public int? FirstOwnedOrder { get; set; }

        // Project
        public ProjectEntity Project { get; set; }
        public bool ProjectCreated { get; set; }
        public bool ProcessUpdated { get; set; }
        public bool InputsChanged { get; set; }
        public int ChannelId { get; set; }
    }
}
