using System.Threading;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variables;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Events.Deployments.Project;
using Squid.Message.Models.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Project;

public class ProjectServiceCreateTests : TestBase
{
    public ProjectServiceCreateTests()
        : base("ProjectServiceCreate", "squid_it_project_create")
    {
    }

    [Fact]
    public async Task CreateProject_FullFlow_AllChildEntitiesCreatedWithCorrectFKs()
    {
        var projectGroupId = await SeedProjectGroupAsync().ConfigureAwait(false);
        await SeedLifecycleAsync().ConfigureAwait(false);

        var result = await Run<IProjectService, ProjectCreatedEvent>(async service =>
        {
            var command = new CreateProjectCommand
            {
                Project = new ProjectDto
                {
                    Name = "My New Project",
                    LifecycleId = 1,
                    ProjectGroupId = projectGroupId,
                    SpaceId = 1
                }
            };

            return await service.CreateProjectAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var projectId = result.Data.Id;

        // Returned DTO FK sanity
        result.Data.ShouldNotBeNull();
        projectId.ShouldBeGreaterThan(0);
        result.Data.DeploymentProcessId.ShouldBeGreaterThan(0);
        result.Data.VariableSetId.ShouldBeGreaterThan(0);
        result.Data.ProjectGroupId.ShouldBe(projectGroupId);

        // Re-read project from DB — confirm persisted FKs match returned DTO
        await Run<IProjectDataProvider>(async projectProvider =>
        {
            var persisted = await projectProvider.GetProjectByIdAsync(projectId, CancellationToken.None).ConfigureAwait(false);

            persisted.ShouldNotBeNull();
            persisted.DeploymentProcessId.ShouldBe(result.Data.DeploymentProcessId);
            persisted.VariableSetId.ShouldBe(result.Data.VariableSetId);
            persisted.ProjectGroupId.ShouldBe(projectGroupId);
        }).ConfigureAwait(false);

        // Verify DeploymentProcess entity exists and points back to project
        await Run<IDeploymentProcessDataProvider>(async processProvider =>
        {
            var process = await processProvider.GetDeploymentProcessByIdAsync(
                result.Data.DeploymentProcessId, CancellationToken.None).ConfigureAwait(false);

            process.ShouldNotBeNull();
            process.ProjectId.ShouldBe(projectId);
            process.Version.ShouldBe(1);
        }).ConfigureAwait(false);

        // Verify VariableSet entity exists and points back to project
        await Run<IVariableDataProvider>(async variableProvider =>
        {
            var variableSet = await variableProvider.GetVariableSetByIdAsync(
                result.Data.VariableSetId, CancellationToken.None).ConfigureAwait(false);

            variableSet.ShouldNotBeNull();
            variableSet.OwnerId.ShouldBe(projectId);
        }).ConfigureAwait(false);

        // Verify default Channel created
        await Run<IChannelDataProvider>(async channelProvider =>
        {
            var channel = await channelProvider.GetDefaultChannelByProjectIdAsync(
                projectId, CancellationToken.None).ConfigureAwait(false);

            channel.ShouldNotBeNull();
            channel.Name.ShouldBe("Default");
            channel.IsDefault.ShouldBeTrue();
            channel.ProjectId.ShouldBe(projectId);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task CreateProject_NoSlug_GeneratesSlugFromName()
    {
        var projectGroupId = await SeedProjectGroupAsync().ConfigureAwait(false);
        await SeedLifecycleAsync().ConfigureAwait(false);

        var result = await Run<IProjectService, ProjectCreatedEvent>(async service =>
        {
            var command = new CreateProjectCommand
            {
                Project = new ProjectDto
                {
                    Name = "My Cool Project",
                    LifecycleId = 1,
                    ProjectGroupId = projectGroupId,
                    SpaceId = 1
                }
            };

            return await service.CreateProjectAsync(command, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.Slug.ShouldBe("my-cool-project");
    }

    private async Task<int> SeedProjectGroupAsync()
    {
        var group = default(Squid.Core.Persistence.Entities.Deployments.ProjectGroup);

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        return group!.Id;
    }

    private async Task SeedLifecycleAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            await builder.CreateLifecycleAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
