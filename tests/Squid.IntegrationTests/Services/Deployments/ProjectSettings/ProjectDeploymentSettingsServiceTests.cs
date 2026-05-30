using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Project;
using Squid.IntegrationTests.Base;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Project;
using ProjectEntity = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.ProjectSettings;

/// <summary>
/// Integration coverage for project DeploymentSettings persistence against a real
/// Postgres DB: a project with no settings reads back all-defaults (today's behaviour),
/// and a saved configuration round-trips through the <c>deployment_settings_json</c> column.
/// </summary>
public class ProjectDeploymentSettingsServiceTests : TestBase
{
    public ProjectDeploymentSettingsServiceTests() : base("ProjectDeploymentSettings", "squid_it_project_deploy_settings")
    {
    }

    [Fact]
    public async Task Get_NoStoredSettings_ReturnsDefaults()
    {
        var projectId = await SeedProjectAsync(deploymentSettingsJson: null).ConfigureAwait(false);

        var settings = await Run<IProjectDeploymentSettingsService, DeploymentSettingsDto>(s => s.GetAsync(projectId)).ConfigureAwait(false);

        settings.TransientDeploymentTargets.UnavailableDeploymentTargets.ShouldBe(UnavailableDeploymentTargetBehavior.SkipAndContinue);
        settings.TransientDeploymentTargets.UnhealthyDeploymentTargets.ShouldBe(UnhealthyDeploymentTargetBehavior.Exclude);
    }

    [Fact]
    public async Task Save_ThenGet_RoundTripsThroughDb()
    {
        var projectId = await SeedProjectAsync(deploymentSettingsJson: null).ConfigureAwait(false);

        var toSave = new DeploymentSettingsDto
        {
            TransientDeploymentTargets = new TransientDeploymentTargetsDto
            {
                UnavailableDeploymentTargets = UnavailableDeploymentTargetBehavior.FailDeployment,
                UnhealthyDeploymentTargets = UnhealthyDeploymentTargetBehavior.DoNotExclude
            }
        };

        await Run<IProjectDeploymentSettingsService, DeploymentSettingsDto>(s => s.SaveAsync(projectId, toSave)).ConfigureAwait(false);

        var reloaded = await Run<IProjectDeploymentSettingsService, DeploymentSettingsDto>(s => s.GetAsync(projectId)).ConfigureAwait(false);

        reloaded.TransientDeploymentTargets.UnavailableDeploymentTargets.ShouldBe(UnavailableDeploymentTargetBehavior.FailDeployment);
        reloaded.TransientDeploymentTargets.UnhealthyDeploymentTargets.ShouldBe(UnhealthyDeploymentTargetBehavior.DoNotExclude);

        // The JSON physically persisted to the new column.
        var project = await Run<IProjectDataProvider, ProjectEntity>(p => p.GetProjectByIdAsync(projectId, CancellationToken.None)).ConfigureAwait(false);
        project.DeploymentSettingsJson.ShouldNotBeNullOrWhiteSpace();
    }

    private async Task<int> SeedProjectAsync(string deploymentSettingsJson)
    {
        var projectId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var project = new ProjectEntity
            {
                Name = $"deploy-settings-proj-{Guid.NewGuid():N}",
                Slug = $"deploy-settings-proj-{Guid.NewGuid():N}",
                SpaceId = 1,
                Json = string.Empty,
                IncludedLibraryVariableSetIds = "[]",
                DeploymentSettingsJson = deploymentSettingsJson
            };
            await repo.InsertAsync(project, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            projectId = project.Id;
        }).ConfigureAwait(false);

        return projectId;
    }
}
