using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
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
    /// leaves in it, or created; its process is rebuilt only when the plan's fingerprint differs from the one recorded, so publishing the same package again changes nothing structural.
    /// </summary>
    Task<AppliedPlan> ApplyAsync(PlanApplication application, CancellationToken ct);
}

public sealed class DeploymentPlanApplier(IProjectService projects, IProjectDataProvider projectData, IPublishedProjectDataProvider publishedProjects, IDeploymentStepService steps, IDeploymentStepDataProvider stepData, IChannelDataProvider channels) : IDeploymentPlanApplier
{
    private const string StepType = "Action";
    private const string StepCondition = "Success";
    private const string StepStartTrigger = "StartAfterPrevious";
    private const string StepPackageRequirement = "LetSquidDecide";

    private ApplierContext _ctx;

    public async Task<AppliedPlan> ApplyAsync(PlanApplication application, CancellationToken ct)
    {
        _ctx = new ApplierContext { Application = application };

        await LoadExistingProjectAsync(ct).ConfigureAwait(false);
        await EnsureProjectAsync(ct).ConfigureAwait(false);
        await SyncProcessAsync(ct).ConfigureAwait(false);
        await ResolveChannelAsync(ct).ConfigureAwait(false);

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

    private async Task SyncProcessAsync(CancellationToken ct)
    {
        var hash = DeploymentPlanFingerprint.Of(_ctx.Application.Plan);

        if (!NeedsRebuild(hash)) return;

        await DeleteStepsAsync(ct).ConfigureAwait(false);
        await CreateStepsAsync(ct).ConfigureAwait(false);
        await RecordMarkerAsync(hash, ct).ConfigureAwait(false);

        _ctx.ProcessUpdated = !_ctx.ProjectCreated;
    }

    private bool NeedsRebuild(string hash)
    {
        var marker = PublishedProjectMarker.TryParse(_ctx.Project.Json);

        return marker?.PlanHash != hash || !string.Equals(marker.Recipe, _ctx.Application.RecipeKind, StringComparison.Ordinal);
    }

    private async Task DeleteStepsAsync(CancellationToken ct)
    {
        await stepData.DeleteDeploymentStepsByProcessIdAsync(_ctx.Project.DeploymentProcessId, ct).ConfigureAwait(false);
    }

    private async Task CreateStepsAsync(CancellationToken ct)
    {
        foreach (var step in _ctx.Application.Plan.Steps)
            await steps.CreateDeploymentStepAsync(new CreateDeploymentStepCommand { SpaceId = _ctx.Application.SpaceId, ProcessId = _ctx.Project.DeploymentProcessId, Step = ToStepModel(step) }, ct).ConfigureAwait(false);
    }

    private async Task RecordMarkerAsync(string hash, CancellationToken ct)
    {
        _ctx.Project.Json = Marker(hash).ToJson();

        await projectData.UpdateProjectAsync(_ctx.Project, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task ResolveChannelAsync(CancellationToken ct)
    {
        var channel = await channels.GetDefaultChannelByProjectIdAsync(_ctx.Project.Id, ct).ConfigureAwait(false);

        _ctx.ChannelId = channel?.Id ?? throw new DeploymentEntityNotFoundException("Default channel of project", _ctx.Project.Id);
    }

    private AppliedPlan BuildResult()
    {
        var packageActions = _ctx.Application.Plan.Steps.SelectMany(s => s.Actions).Where(a => a.Package != null).Select(a => a.Name).ToList();

        return new AppliedPlan { ProjectId = _ctx.Project.Id, ChannelId = _ctx.ChannelId, ProjectCreated = _ctx.ProjectCreated, ProcessUpdated = _ctx.ProcessUpdated, PackageActionNames = packageActions };
    }

    private PublishedProjectMarker Marker(string hash) => new(_ctx.Application.PackageId, _ctx.Application.RecipeKind, hash);

    private CreateOrUpdateDeploymentStepModel ToStepModel(PlanStep step)
    {
        var roles = new StepPropertyModel { PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = string.Join(',', step.TargetRoles) };

        return new CreateOrUpdateDeploymentStepModel
        {
            Name = step.Name,
            StepType = StepType,
            Condition = StepCondition,
            StartTrigger = StepStartTrigger,
            PackageRequirement = StepPackageRequirement,
            IsRequired = true,
            Properties = [roles],
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

        // Project
        public ProjectEntity Project { get; set; }
        public bool ProjectCreated { get; set; }
        public bool ProcessUpdated { get; set; }
        public int ChannelId { get; set; }
    }
}
