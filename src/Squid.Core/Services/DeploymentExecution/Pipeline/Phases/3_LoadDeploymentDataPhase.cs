using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class LoadDeploymentDataPhase(
    IDeploymentDataProvider deploymentDataProvider,
    IReleaseDataProvider releaseDataProvider,
    IProjectDataProvider projectDataProvider,
    IEnvironmentDataProvider environmentDataProvider,
    IReleaseSelectedPackageDataProvider releaseSelectedPackageDataProvider) : IDeploymentPipelinePhase
{
    public int Order => 200;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        await LoadDeploymentAsync(ctx, ct).ConfigureAwait(false);
        await LoadSelectedPackagesAsync(ctx, ct).ConfigureAwait(false);
    }

    private async Task LoadDeploymentAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var deployment = await deploymentDataProvider.GetDeploymentByTaskIdAsync(ctx.Task.Id, ct).ConfigureAwait(false);

        if (deployment == null) throw new DeploymentEntityNotFoundException("Deployment", $"task:{ctx.Task.Id}");

        ctx.Deployment = deployment;
        ctx.Deployment.DeploymentRequestPayload = DeploymentTargetFinder.ParseRequestPayload(deployment.Json);

        var release = await releaseDataProvider.GetReleaseByIdAsync(deployment.ReleaseId, ct).ConfigureAwait(false);
        var project = await projectDataProvider.GetProjectByIdAsync(deployment.ProjectId, ct).ConfigureAwait(false);
        var environment = await environmentDataProvider.GetEnvironmentByIdAsync(deployment.EnvironmentId, ct).ConfigureAwait(false);

        ctx.Release = release;
        ctx.Project = project;
        ctx.Environment = environment;
        ctx.UseGuidedFailure = (ctx.Deployment.DeploymentRequestPayload?.UseGuidedFailure ?? false) || (environment?.UseGuidedFailure ?? false);
    }

    private async Task LoadSelectedPackagesAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        ctx.SelectedPackages = await releaseSelectedPackageDataProvider.GetByReleaseIdAsync(ctx.Release.Id, ct).ConfigureAwait(false);

        Log.Information("Loaded {Count} selected packages for release {ReleaseId}", ctx.SelectedPackages.Count, ctx.Release.Id);
    }
}
