using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Releases.Exceptions;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Commands.Publishing;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

public interface IPublishService : IScopedDependency
{
    /// <summary>
    /// Publishes one stored version of a built-in package as an app: chooses the recipe, renders its plan, applies it to the package's project, creates the release that selects
    /// this version and starts the deployment. Publishing the same package again reuses the project; a version published twice reuses its release.
    /// </summary>
    Task<PublishPackageResponseData> PublishAsync(PublishPackageCommand command, CancellationToken ct);
}

public sealed class PublishService(IBuiltInFeedLocator feeds, IBuiltInPackageDataProvider packages, IDeployRecipeRegistry registry, IDeploymentPlanApplier applier, IReleaseDataProvider releaseData, IReleaseService releases, IDeploymentService deployments, IPublishedProjectDataProvider publishedProjects) : IPublishService
{
    private PublishContext _ctx;

    public async Task<PublishPackageResponseData> PublishAsync(PublishPackageCommand command, CancellationToken ct)
    {
        _ctx = new PublishContext { Command = command, TargetRoles = NormalizeRoles(command.TargetRoles) };

        await publishedProjects.RunInTransactionAsync(PrepareAsync, ct).ConfigureAwait(false);
        await StartDeploymentAsync(ct).ConfigureAwait(false);

        return BuildResponse();
    }

    // Everything that writes, in one transaction under a lock on (space, package): when any step refuses, nothing is kept, so retrying the same request meets the same state.
    // The deployment itself is created after the commit: creating it queues a background job, which must not be able to run before the rows it reads are committed.
    private async Task PrepareAsync(CancellationToken ct)
    {
        await publishedProjects.LockPackageAsync(_ctx.Command.SpaceId.GetValueOrDefault(), _ctx.Command.PackageId, ct).ConfigureAwait(false);
        await LoadPackageAsync(ct).ConfigureAwait(false);
        SelectRecipe();
        await RenderPlanAsync(ct).ConfigureAwait(false);
        _ctx.InputValues = PlanInputs.Validate(_ctx.Plan, _ctx.Command.Inputs);
        await ApplyPlanAsync(ct).ConfigureAwait(false);
        await EnsureReleaseAsync(ct).ConfigureAwait(false);
        await EnsureDeployableAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadPackageAsync(CancellationToken ct)
    {
        var feed = await feeds.GetAsync(_ctx.Command.SpaceId.GetValueOrDefault(), ct).ConfigureAwait(false);
        var package = await packages.GetAsync(feed.Id, _ctx.Command.PackageId ?? string.Empty, _ctx.Command.Version ?? string.Empty, ct).ConfigureAwait(false);

        _ctx.FeedId = feed.Id;
        _ctx.Package = package ?? throw new BuiltInPackageNotFoundException(feed.Id, _ctx.Command.PackageId, _ctx.Command.Version);
        _ctx.Facts = PackageFactsJson.Deserialize(package.Facts) ?? throw new PublishFactsMissingException(package.PackageId, package.Version);
    }

    private void SelectRecipe()
    {
        var selection = registry.Select(_ctx.Facts);
        var requested = _ctx.Command.RecipeKind;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            _ctx.Recipe = registry.Find(requested) ?? throw new PublishRecipeUnknownException(requested.Trim(), selection.ValidKinds);
            return;
        }

        if (selection.UnknownRecipe != null)
            throw new PublishRecipeUnknownException(selection.UnknownRecipe, selection.ValidKinds);

        var best = selection.Candidates.FirstOrDefault() ?? throw new PublishNoRecipeFitsException(_ctx.Package.PackageId, _ctx.Package.Version, selection.ValidKinds);

        _ctx.Recipe = registry.Find(best.Kind);
    }

    private async Task RenderPlanAsync(CancellationToken ct)
    {
        var context = new RecipeRenderContext { PackageId = _ctx.Package.PackageId, Facts = _ctx.Facts, TargetRoles = _ctx.TargetRoles };

        _ctx.Plan = await _ctx.Recipe.RenderAsync(context, ct).ConfigureAwait(false);
    }

    private async Task ApplyPlanAsync(CancellationToken ct)
    {
        var application = new PlanApplication
        {
            SpaceId = _ctx.Package.SpaceId,
            FeedId = _ctx.FeedId,
            EnvironmentId = _ctx.Command.EnvironmentId,
            PackageId = _ctx.Package.PackageId,
            RecipeKind = _ctx.Recipe.Kind,
            ProjectName = _ctx.Command.ProjectName,
            ProjectGroupId = _ctx.Command.ProjectGroupId,
            LifecycleId = _ctx.Command.LifecycleId,
            Plan = _ctx.Plan,
            InputValues = _ctx.InputValues
        };

        _ctx.Applied = await applier.ApplyAsync(application, ct).ConfigureAwait(false);
    }

    private async Task EnsureReleaseAsync(CancellationToken ct)
    {
        var applied = _ctx.Applied;
        var existing = await releaseData.GetReleaseByVersionAsync(applied.ProjectId, applied.ChannelId, _ctx.Package.Version, ct).ConfigureAwait(false);

        if (existing != null && applied.ProcessUpdated)
            throw new PublishReleaseVersionExistsException(applied.ProjectId, _ctx.Package.Version);

        if (existing != null && applied.InputsChanged)
            await releases.UpdateReleaseVariableAsync(new UpdateReleaseVariableCommand { SpaceId = _ctx.Package.SpaceId, ReleaseId = existing.Id }, ct).ConfigureAwait(false);

        _ctx.ReleaseId = existing?.Id ?? await CreateReleaseAsync(ct).ConfigureAwait(false);
    }

    // The checks a deployment makes when it is created (the lifecycle lets this release go to the environment, the roles have targets), made before the commit so a refusal keeps nothing.
    private async Task EnsureDeployableAsync(CancellationToken ct)
    {
        var preview = await deployments.PreviewDeploymentAsync(new DeploymentRequestPayload { ReleaseId = _ctx.ReleaseId, EnvironmentId = _ctx.Command.EnvironmentId }, ct).ConfigureAwait(false);

        if (preview.CanDeploy) return;

        var reasons = preview.BlockingReasons.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        throw new DeploymentValidationException($"The package cannot be deployed to environment {_ctx.Command.EnvironmentId}: {(reasons.Count == 0 ? "the deployment preview refused it" : string.Join("; ", reasons))}");
    }

    private async Task<int> CreateReleaseAsync(CancellationToken ct)
    {
        var command = new CreateReleaseCommand
        {
            SpaceId = _ctx.Package.SpaceId,
            ProjectId = _ctx.Applied.ProjectId,
            ChannelId = _ctx.Applied.ChannelId,
            Version = _ctx.Package.Version,
            SelectedPackages = _ctx.Applied.PackageActionNames.Select(SelectedPackage).ToList()
        };

        var created = await releases.CreateReleaseAsync(command, ct).ConfigureAwait(false);

        return created.Release.Id;
    }

    private CreateReleaseSelectedPackageDto SelectedPackage(string actionName)
    {
        return new CreateReleaseSelectedPackageDto { ActionName = actionName, PackageReferenceName = _ctx.Package.PackageId, Version = _ctx.Package.Version, FeedId = _ctx.FeedId };
    }

    private async Task StartDeploymentAsync(CancellationToken ct)
    {
        var command = new CreateDeploymentCommand { SpaceId = _ctx.Package.SpaceId, ReleaseId = _ctx.ReleaseId, EnvironmentId = _ctx.Command.EnvironmentId };

        var created = await deployments.CreateDeploymentAsync(command, ct).ConfigureAwait(false);

        _ctx.DeploymentId = created.Deployment.Id;
        _ctx.TaskId = created.TaskId;
    }

    private PublishPackageResponseData BuildResponse()
    {
        return new PublishPackageResponseData
        {
            ProjectId = _ctx.Applied.ProjectId,
            ProjectCreated = _ctx.Applied.ProjectCreated,
            ProcessUpdated = _ctx.Applied.ProcessUpdated,
            RecipeKind = _ctx.Recipe.Kind,
            ReleaseId = _ctx.ReleaseId,
            DeploymentId = _ctx.DeploymentId,
            TaskId = _ctx.TaskId
        };
    }

    private static List<string> NormalizeRoles(IEnumerable<string> roles)
    {
        var cleaned = (roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (cleaned.Count == 0)
            throw new RecipeInputInvalidException("targetRoles", "name at least one target role, the machines that should run the app");

        if (cleaned.Any(r => r.Contains(',', StringComparison.Ordinal)))
            throw new RecipeInputInvalidException("targetRoles", "a role cannot contain a comma");

        return cleaned;
    }

    private sealed class PublishContext
    {
        // Request
        public PublishPackageCommand Command { get; set; }
        public List<string> TargetRoles { get; set; }

        // Package
        public int FeedId { get; set; }
        public BuiltInPackage Package { get; set; }
        public PackageFacts Facts { get; set; }

        // Recipe
        public IDeployRecipe Recipe { get; set; }
        public DeploymentPlan Plan { get; set; }
        public Dictionary<string, string> InputValues { get; set; }
        public AppliedPlan Applied { get; set; }

        // Release and deployment
        public int ReleaseId { get; set; }
        public int DeploymentId { get; set; }
        public int TaskId { get; set; }
    }
}
