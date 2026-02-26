using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.IntegrationTests.Deployments.Snapshots;

public class IntegrationDeploymentSnapshot : SnapshotFixtureBase
{
    [Fact]
    public async Task CreateDeploymentAsync_InheritsReleaseSnapshotIds()
    {
        await Run<IRepository, IUnitOfWork, IReleaseService, IDeploymentService>(
            async (repository, unitOfWork, releaseService, deploymentService) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                // Set up lifecycle (must be first — Project and Channel reference LifecycleId = 1)
                var environment = await builder.CreateEnvironmentAsync();
                var lifecycle = await builder.CreateLifecycleAsync();
                await builder.CreateLifecyclePhaseAsync(lifecycle.Id, environment.Id);

                // Set up project with process + variables
                var variableSet = await builder.CreateVariableSetAsync();
                await builder.CreateVariableAsync(variableSet.Id, "AppName", "Squid");
                var project = await builder.CreateProjectAsync(variableSet.Id);
                var process = await builder.CreateDeploymentProcessAsync();
                await builder.UpdateProjectProcessIdAsync(project, process.Id);

                var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Step");
                await builder.CreateDeploymentActionAsync(step.Id, 1, "Run Script", "Octopus.Script");

                var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id);

                // Set up machine (required for deployment validation)
                await builder.CreateMachineAsync(environment.Id);

                // Create release — snapshots both process and variables
                var releaseEvent = await releaseService.CreateReleaseAsync(
                    new CreateReleaseCommand
                    {
                        ProjectId = project.Id,
                        ChannelId = channel.Id,
                        Version = "1.0.0"
                    });

                var release = await repository
                    .Query<Release>(r => r.Id == releaseEvent.Release.Id)
                    .FirstOrDefaultAsync();

                release.ShouldNotBeNull();
                release.ProjectDeploymentProcessSnapshotId.ShouldBeGreaterThan(0);
                release.ProjectVariableSetSnapshotId.ShouldBeGreaterThan(0);

                // Create deployment — should inherit release snapshot IDs
                var deploymentEvent = await deploymentService.CreateDeploymentAsync(
                    new CreateDeploymentCommand
                    {
                        ReleaseId = release.Id,
                        EnvironmentId = environment.Id,
                        DeployedBy = 1
                    });

                var deployment = await repository
                    .Query<Deployment>(d => d.Id == deploymentEvent.Deployment.Id)
                    .FirstOrDefaultAsync();

                deployment.ShouldNotBeNull();
                deployment.ProcessSnapshotId.ShouldBe(release.ProjectDeploymentProcessSnapshotId);
                deployment.VariableSetSnapshotId.ShouldBe(release.ProjectVariableSetSnapshotId);
            }).ConfigureAwait(false);
    }

    [Fact]
    public async Task CreateDeploymentAsync_SnapshotDataMatchesReleaseTimeData()
    {
        await Run<IRepository, IUnitOfWork, IReleaseService, IDeploymentService>(
            async (repository, unitOfWork, releaseService, deploymentService) =>
            {
                var builder = new TestDataBuilder(repository, unitOfWork);

                // Set up lifecycle (must be first — Project and Channel reference LifecycleId = 1)
                var environment = await builder.CreateEnvironmentAsync();
                var lifecycle = await builder.CreateLifecycleAsync();
                await builder.CreateLifecyclePhaseAsync(lifecycle.Id, environment.Id);

                // Set up project with process + variables
                var variableSet = await builder.CreateVariableSetAsync();
                await builder.CreateVariableAsync(variableSet.Id, "Env", "Production");
                var project = await builder.CreateProjectAsync(variableSet.Id);
                var process = await builder.CreateDeploymentProcessAsync();
                await builder.UpdateProjectProcessIdAsync(project, process.Id);

                var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Original Step");
                var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Original Action", "Octopus.Script");
                await builder.CreateActionPropertiesAsync(action.Id, ("Octopus.Action.Script.ScriptBody", "echo original"));

                var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id);
                await builder.CreateMachineAsync(environment.Id);

                // Create release — freezes "Original Step" + "Env=Production"
                var releaseEvent = await releaseService.CreateReleaseAsync(
                    new CreateReleaseCommand
                    {
                        ProjectId = project.Id,
                        ChannelId = channel.Id,
                        Version = "1.0.0"
                    });

                var release = await repository
                    .Query<Release>(r => r.Id == releaseEvent.Release.Id)
                    .FirstOrDefaultAsync();

                // Create deployment — should use the frozen snapshots
                var deploymentEvent = await deploymentService.CreateDeploymentAsync(
                    new CreateDeploymentCommand
                    {
                        ReleaseId = release.Id,
                        EnvironmentId = environment.Id,
                        DeployedBy = 1
                    });

                var deployment = await repository
                    .Query<Deployment>(d => d.Id == deploymentEvent.Deployment.Id)
                    .FirstOrDefaultAsync();

                // Verify deployment snapshot IDs point to release-time snapshots
                deployment.ProcessSnapshotId.ShouldBe(release.ProjectDeploymentProcessSnapshotId);
                deployment.VariableSetSnapshotId.ShouldBe(release.ProjectVariableSetSnapshotId);
            }).ConfigureAwait(false);
    }
}
