using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;

namespace Squid.Core.Services.Deployments.Validation.Rules;

public sealed class ProjectAvailabilityValidationRule : IDeploymentValidationRule
{
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;

    public ProjectAvailabilityValidationRule(
        IReleaseDataProvider releaseDataProvider,
        IProjectDataProvider projectDataProvider)
    {
        _releaseDataProvider = releaseDataProvider;
        _projectDataProvider = projectDataProvider;
    }

    public int Order => 150;

    public bool Supports(DeploymentValidationStage stage) =>
        stage == DeploymentValidationStage.Precheck || stage == DeploymentValidationStage.Create;

    public async Task EvaluateAsync(DeploymentValidationContext context, DeploymentValidationReport report, CancellationToken cancellationToken = default)
    {
        var release = await _releaseDataProvider
            .GetReleaseByIdAsync(context.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
            return;

        var project = await _projectDataProvider
            .GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);

        if (project == null)
            return;

        if (!project.IsDisabled)
            return;

        report.AddBlockingIssue(DeploymentValidationIssueCode.ProjectDisabled, $"Project {project.Id} ({project.Name}) is disabled and cannot be deployed.");
    }
}
