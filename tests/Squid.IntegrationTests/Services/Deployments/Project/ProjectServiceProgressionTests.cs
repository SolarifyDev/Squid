using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Project;
using Squid.IntegrationTests.Helpers;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Project;

public class ProjectServiceProgressionTests : TestBase
{
    public ProjectServiceProgressionTests()
        : base("ProjectServiceProgression", "squid_it_project_progression")
    {
    }

    [Fact]
    public async Task GetProgression_NoReleases_ReturnsEmptyReleasesWithEnvironments()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.ShouldNotBeNull();
        result.Data.Releases.Count.ShouldBe(0);
        result.Data.Environments.Count.ShouldBe(2);
        result.Data.Environments.ShouldContain(e => e.Id == seed.DevEnvId);
        result.Data.Environments.ShouldContain(e => e.Id == seed.ProdEnvId);
    }

    [Fact]
    public async Task GetProgression_SingleRelease_ReturnsReleaseWithChannelInfo()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0, channelId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            channelId = channel.Id;

            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.Releases.Count.ShouldBeGreaterThanOrEqualTo(1);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);

        releaseDto.Release.Version.ShouldBe("1.0.0");
        releaseDto.Release.ChannelId.ShouldBe(channelId);
        releaseDto.Channel.ShouldNotBeNull();
        releaseDto.Channel.Id.ShouldBe(channelId);
    }

    [Fact]
    public async Task GetProgression_ReleaseWithDeployment_ReturnsDeploymentPerEnvironment()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0, deploymentId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            var deployment = await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task.Id, channel.Id).ConfigureAwait(false);
            deploymentId = deployment.Id;
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);

        releaseDto.Deployments.ShouldContainKey(seed.DevEnvId);
        releaseDto.Deployments[seed.DevEnvId].Count.ShouldBe(1);
        releaseDto.Deployments[seed.DevEnvId][0].DeploymentId.ShouldBe(deploymentId);
        releaseDto.Deployments[seed.DevEnvId][0].State.ShouldBe("Success");
        releaseDto.Deployments[seed.DevEnvId][0].ReleaseVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task GetProgression_MultipleDeploymentsSameEnvironment_OrderedByCreatedDesc()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0, firstDeploymentId = 0, secondDeploymentId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            var task1 = await builder.CreateServerTaskAsync("Failed").ConfigureAwait(false);
            var deploy1 = await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task1.Id, channel.Id).ConfigureAwait(false);
            firstDeploymentId = deploy1.Id;

            var task2 = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            var deploy2 = await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task2.Id, channel.Id).ConfigureAwait(false);
            secondDeploymentId = deploy2.Id;
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);
        var devDeployments = releaseDto.Deployments[seed.DevEnvId];

        devDeployments.Count.ShouldBe(2);
        devDeployments[0].DeploymentId.ShouldBe(secondDeploymentId);
        devDeployments[1].DeploymentId.ShouldBe(firstDeploymentId);
    }

    [Fact]
    public async Task GetProgression_IsCurrentMarking_NewestDeploymentPerEnvironmentIsCurrent()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            var task1 = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task1.Id, channel.Id).ConfigureAwait(false);

            var task2 = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task2.Id, channel.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);
        var devDeployments = releaseDto.Deployments[seed.DevEnvId];

        devDeployments[0].IsCurrent.ShouldBeTrue();
        devDeployments[1].IsCurrent.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, new[] { "1.0.5", "1.0.4", "1.0.3" })]
    [InlineData(5, new[] { "1.0.5", "1.0.4", "1.0.3", "1.0.2", "1.0.1" })]
    public async Task GetProgression_UndeployedReleases_ReturnsMostRecentWindowPerChannel(int? historyCount, string[] expectedVersions)
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int channelId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            channelId = channel.Id;

            for (var i = 1; i <= 5; i++)
                await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, $"1.0.{i}").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await GetProgressionAsync(seed.ProjectId, historyCount).ConfigureAwait(false);

        result.Data.Releases.Where(r => r.Release.ChannelId == channelId).Select(r => r.Release.Version).ShouldBe(expectedVersions);
        result.Data.ChannelReleaseCounts[channelId].ShouldBe(5);
    }

    [Fact]
    public async Task GetProgression_DeployedRelease_IsIncluded()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int oldReleaseId = 0, channelId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            channelId = channel.Id;

            // Old release (will be #4, outside top 3)
            var oldRelease = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "0.1.0").ConfigureAwait(false);
            oldReleaseId = oldRelease.Id;

            // Deploy the old release to prod and mark as successful completion
            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            var deployment = await builder.CreateDeploymentAsync(seed.ProjectId, seed.ProdEnvId, oldRelease.Id, task.Id, channel.Id).ConfigureAwait(false);
            await builder.CreateDeploymentCompletionAsync(deployment.Id, "Success").ConfigureAwait(false);

            // 3 newer releases
            for (var i = 1; i <= 3; i++)
                await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, $"1.0.{i}").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var channelReleases = result.Data.Releases
            .Where(r => r.Release.ChannelId == channelId)
            .ToList();

        channelReleases.Count.ShouldBe(4);
        channelReleases.ShouldContain(r => r.Release.Id == oldReleaseId);
    }

    [Fact]
    public async Task GetProgression_ChannelEnvironments_MapsCorrectEnvironmentsPerChannel()
    {
        int channelAId = 0, channelBId = 0, devEnvId = 0, prodEnvId = 0, stagingEnvId = 0, projectId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var devEnv = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            var stagingEnv = await builder.CreateEnvironmentAsync("Staging").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Prod").ConfigureAwait(false);

            devEnvId = devEnv.Id;
            stagingEnvId = stagingEnv.Id;
            prodEnvId = prodEnv.Id;

            // Lifecycle A: Dev → Staging
            var lifecycleA = await builder.CreateLifecycleAsync("Lifecycle A").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleA.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleA.Id, stagingEnv.Id, "Staging Phase").ConfigureAwait(false);

            // Lifecycle B: Dev → Prod
            var lifecycleB = await builder.CreateLifecycleAsync("Lifecycle B").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleB.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleB.Id, prodEnv.Id, "Prod Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycleA.Id).ConfigureAwait(false);
            projectId = project.Id;

            // Channel A uses lifecycle A
            var channelA = await builder.CreateChannelAsync(project.Id, lifecycleA.Id).ConfigureAwait(false);
            channelAId = channelA.Id;

            // Channel B uses lifecycle B
            var channelB = await builder.CreateChannelAsync(project.Id, lifecycleB.Id).ConfigureAwait(false);
            channelBId = channelB.Id;
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = projectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.ChannelEnvironments.ShouldContainKey(channelAId);
        result.Data.ChannelEnvironments.ShouldContainKey(channelBId);

        result.Data.ChannelEnvironments[channelAId].ShouldContain(devEnvId);
        result.Data.ChannelEnvironments[channelAId].ShouldContain(stagingEnvId);
        result.Data.ChannelEnvironments[channelAId].ShouldNotContain(prodEnvId);

        result.Data.ChannelEnvironments[channelBId].ShouldContain(devEnvId);
        result.Data.ChannelEnvironments[channelBId].ShouldContain(prodEnvId);
        result.Data.ChannelEnvironments[channelBId].ShouldNotContain(stagingEnvId);
    }

    [Fact]
    public async Task GetProgression_NextDeployments_ReflectsLifecycleProgression()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int channelId = 0, releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            channelId = channel.Id;

            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);

        // No deployments yet → first phase (Dev) should be allowed
        releaseDto.NextDeployments.ShouldContain(seed.DevEnvId);
    }

    [Fact]
    public async Task GetProgression_AfterDevDeployment_NextDeploymentsIncludesProd()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int channelId = 0, releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            channelId = channel.Id;

            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            // Deploy to Dev successfully
            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            var deployment = await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task.Id, channel.Id).ConfigureAwait(false);
            await builder.CreateDeploymentCompletionAsync(deployment.Id, "Success").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);

        // After Dev deployed → both Dev and Prod should be allowed
        releaseDto.NextDeployments.ShouldContain(seed.DevEnvId);
        releaseDto.NextDeployments.ShouldContain(seed.ProdEnvId);
    }

    [Fact]
    public async Task GetProgression_DeploymentHasWarningsOrErrors_ReflectedInDto()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            var task = new ServerTask
            {
                Name = "DeploymentTask",
                Description = "Task with warnings",
                QueueTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-4),
                CompletedTime = DateTimeOffset.UtcNow,
                State = "Success",
                HasWarningsOrErrors = true,
                ServerNodeId = Guid.Empty,
                ProjectId = 0,
                EnvironmentId = 0,
                DurationSeconds = 60,
                BatchId = 0,
                JSON = "{}",
                DataVersion = Array.Empty<byte>(),
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                ConcurrencyTag = string.Empty,
                ErrorMessage = string.Empty,
                BusinessProcessState = string.Empty,
                ServerTaskType = string.Empty,
                StateOrder = 0,
                Weight = 0,
                JobId = string.Empty
            };

            await repository.InsertAsync(task).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task.Id, channel.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);

        releaseDto.Deployments[seed.DevEnvId][0].HasWarningsOrErrors.ShouldBeTrue();
    }

    [Fact]
    public async Task GetProgression_FailedDeployment_StateReflectedCorrectly()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            var task = await builder.CreateServerTaskAsync("Failed").ConfigureAwait(false);
            await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task.Id, channel.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);

        releaseDto.Deployments[seed.DevEnvId][0].State.ShouldBe("Failed");
    }

    [Fact]
    public async Task GetProgression_ReleaseSortedByAssembledDesc()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);

            await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "2.0.0").ConfigureAwait(false);
            await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "3.0.0").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var versions = result.Data.Releases.Select(r => r.Release.Version).ToList();

        versions[0].ShouldBe("3.0.0");
        versions[1].ShouldBe("2.0.0");
        versions[2].ShouldBe("1.0.0");
    }

    [Fact]
    public async Task GetProgression_DeploymentsAcrossEnvironments_GroupedByEnvironmentId()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            var task1 = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task1.Id, channel.Id).ConfigureAwait(false);

            var task2 = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            await builder.CreateDeploymentAsync(seed.ProjectId, seed.ProdEnvId, release.Id, task2.Id, channel.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);

        releaseDto.Deployments.Keys.Count.ShouldBe(2);
        releaseDto.Deployments.ShouldContainKey(seed.DevEnvId);
        releaseDto.Deployments.ShouldContainKey(seed.ProdEnvId);
        releaseDto.Deployments[seed.DevEnvId].Count.ShouldBe(1);
        releaseDto.Deployments[seed.ProdEnvId].Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetProgression_CompletedTimeAndCreated_PopulatedFromServerTask()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;

            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task.Id, channel.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = seed.ProjectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var releaseDto = result.Data.Releases.First(r => r.Release.Id == releaseId);
        var deployment = releaseDto.Deployments[seed.DevEnvId][0];

        deployment.CompletedTime.ShouldNotBeNull();
        deployment.CreatedDate.ShouldNotBe(default);
    }

    [Fact]
    public async Task GetProgression_EnvironmentsList_UnionOfAllChannelLifecycles()
    {
        int devEnvId = 0, stagingEnvId = 0, prodEnvId = 0, projectId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var devEnv = await builder.CreateEnvironmentAsync("Dev").ConfigureAwait(false);
            var stagingEnv = await builder.CreateEnvironmentAsync("Staging").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Prod").ConfigureAwait(false);

            devEnvId = devEnv.Id;
            stagingEnvId = stagingEnv.Id;
            prodEnvId = prodEnv.Id;

            // Lifecycle A: Dev only
            var lifecycleA = await builder.CreateLifecycleAsync("Lifecycle A").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleA.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);

            // Lifecycle B: Staging → Prod
            var lifecycleB = await builder.CreateLifecycleAsync("Lifecycle B").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleB.Id, stagingEnv.Id, "Staging Phase").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycleB.Id, prodEnv.Id, "Prod Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycleA.Id).ConfigureAwait(false);
            projectId = project.Id;

            await builder.CreateChannelAsync(project.Id, lifecycleA.Id).ConfigureAwait(false);
            await builder.CreateChannelAsync(project.Id, lifecycleB.Id).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IProjectService, GetProjectProgressionResponse>(async service =>
        {
            return await service.GetProjectProgressionAsync(
                new GetProjectProgressionRequest { ProjectId = projectId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var envIds = result.Data.Environments.Select(e => e.Id).ToList();

        envIds.ShouldContain(devEnvId);
        envIds.ShouldContain(stagingEnvId);
        envIds.ShouldContain(prodEnvId);
    }

    [Fact]
    public async Task GetProgression_OldReleaseHoldsCurrentDeployment_IncludedWithCorrectIsCurrent()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int oldReleaseId = 0, newestReleaseId = 0, oldDevDeploymentId = 0, oldProdDeploymentId = 0, newDevDeploymentId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var oldRelease = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "0.1.0").ConfigureAwait(false);
            oldReleaseId = oldRelease.Id;

            oldDevDeploymentId = (await DeployAsync(builder, seed.ProjectId, seed.DevEnvId, oldRelease.Id, channel.Id, "Success").ConfigureAwait(false)).Id;
            oldProdDeploymentId = (await DeployAsync(builder, seed.ProjectId, seed.ProdEnvId, oldRelease.Id, channel.Id, "Success").ConfigureAwait(false)).Id;

            for (var i = 1; i <= 4; i++)
                newestReleaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, $"1.0.{i}").ConfigureAwait(false)).Id;

            newDevDeploymentId = (await DeployAsync(builder, seed.ProjectId, seed.DevEnvId, newestReleaseId, channel.Id, "Success").ConfigureAwait(false)).Id;
        }).ConfigureAwait(false);

        var result = await GetProgressionAsync(seed.ProjectId).ConfigureAwait(false);

        result.Data.Releases.Select(r => r.Release.Version).ShouldBe(new[] { "1.0.4", "1.0.3", "1.0.2", "0.1.0" });

        var oldRelease = result.Data.Releases.Single(r => r.Release.Id == oldReleaseId);
        oldRelease.Deployments[seed.ProdEnvId].Single(d => d.DeploymentId == oldProdDeploymentId).IsCurrent.ShouldBeTrue();
        oldRelease.Deployments[seed.DevEnvId].Single(d => d.DeploymentId == oldDevDeploymentId).IsCurrent.ShouldBeFalse();

        var newestRelease = result.Data.Releases.Single(r => r.Release.Id == newestReleaseId);
        newestRelease.Deployments[seed.DevEnvId].Single(d => d.DeploymentId == newDevDeploymentId).IsCurrent.ShouldBeTrue();
    }

    [Fact]
    public async Task GetProgression_CurrentDeploymentFailed_IncludesPreviousSuccessfulReleaseOnly()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int olderSuccessReleaseId = 0, previousSuccessReleaseId = 0, olderFailedReleaseId = 0, failedReleaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);

            olderSuccessReleaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "0.0.1").ConfigureAwait(false)).Id;
            await DeployAsync(builder, seed.ProjectId, seed.ProdEnvId, olderSuccessReleaseId, channel.Id, "Success").ConfigureAwait(false);

            previousSuccessReleaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "0.1.0").ConfigureAwait(false)).Id;
            await DeployAsync(builder, seed.ProjectId, seed.ProdEnvId, previousSuccessReleaseId, channel.Id, "Success").ConfigureAwait(false);

            olderFailedReleaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "0.2.0").ConfigureAwait(false)).Id;
            await DeployAsync(builder, seed.ProjectId, seed.ProdEnvId, olderFailedReleaseId, channel.Id, "Failed").ConfigureAwait(false);

            for (var i = 1; i <= 4; i++)
                failedReleaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, $"1.0.{i}").ConfigureAwait(false)).Id;

            await DeployAsync(builder, seed.ProjectId, seed.ProdEnvId, failedReleaseId, channel.Id, "Failed").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await GetProgressionAsync(seed.ProjectId).ConfigureAwait(false);
        var releaseIds = result.Data.Releases.Select(r => r.Release.Id).ToList();

        releaseIds.ShouldContain(previousSuccessReleaseId, customMessage: "the release still running in Prod (last success before a failed deploy) must stay on the dashboard, as Octopus' dashboard.Get(includePreviousSuccessful: true) does");
        releaseIds.ShouldNotContain(olderSuccessReleaseId, customMessage: "only the single most recent previous success per environment is a dashboard release");
        releaseIds.ShouldNotContain(olderFailedReleaseId, customMessage: "a superseded failed deployment is neither current nor previous successful");
        result.Data.Releases.Single(r => r.Release.Id == failedReleaseId).Deployments[seed.ProdEnvId][0].IsCurrent.ShouldBeTrue();
        result.Data.Releases.Single(r => r.Release.Id == previousSuccessReleaseId).Deployments[seed.ProdEnvId][0].IsCurrent.ShouldBeFalse();
    }

    [Fact]
    public async Task GetProgression_CurrentDeploymentSucceeded_StillIncludesPreviousSuccessfulRelease()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int previousReleaseId = 0, newestReleaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);

            previousReleaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "0.1.0").ConfigureAwait(false)).Id;
            await DeployAsync(builder, seed.ProjectId, seed.ProdEnvId, previousReleaseId, channel.Id, "Success").ConfigureAwait(false);

            for (var i = 1; i <= 4; i++)
                newestReleaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, $"1.0.{i}").ConfigureAwait(false)).Id;

            await DeployAsync(builder, seed.ProjectId, seed.ProdEnvId, newestReleaseId, channel.Id, "Success").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await GetProgressionAsync(seed.ProjectId).ConfigureAwait(false);

        result.Data.Releases.Select(r => r.Release.Version).ShouldBe(new[] { "1.0.4", "1.0.3", "1.0.2", "0.1.0" }, customMessage: "the previous successful release of Prod (the rollback target) stays on the dashboard, matching Octopus");
    }

    [Fact]
    public async Task GetProgression_MultipleChannels_WindowAppliedPerChannelWithTotals()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int defaultChannelId = 0, hotfixChannelId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var defaultChannel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId, "Default").ConfigureAwait(false);
            var hotfixChannel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId, "Hotfix").ConfigureAwait(false);
            defaultChannelId = defaultChannel.Id;
            hotfixChannelId = hotfixChannel.Id;

            for (var i = 1; i <= 6; i++)
                await builder.CreateReleaseAsync(seed.ProjectId, defaultChannel.Id, $"2.0.{i}").ConfigureAwait(false);

            await builder.CreateReleaseAsync(seed.ProjectId, hotfixChannel.Id, "2.0.1-hotfix").ConfigureAwait(false);
            await builder.CreateReleaseAsync(seed.ProjectId, hotfixChannel.Id, "2.0.2-hotfix").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await GetProgressionAsync(seed.ProjectId).ConfigureAwait(false);

        result.Data.Releases.Where(r => r.Release.ChannelId == defaultChannelId).Select(r => r.Release.Version).ShouldBe(new[] { "2.0.6", "2.0.5", "2.0.4" });
        result.Data.Releases.Where(r => r.Release.ChannelId == hotfixChannelId).Select(r => r.Release.Version).ShouldBe(new[] { "2.0.2-hotfix", "2.0.1-hotfix" });
        result.Data.Releases.ShouldAllBe(r => r.Deployments.Count == 0);
        result.Data.ChannelReleaseCounts[defaultChannelId].ShouldBe(6);
        result.Data.ChannelReleaseCounts[hotfixChannelId].ShouldBe(2);
    }

    [Fact]
    public async Task GetProgression_OtherProjectDeployments_NeitherLeakNorStealCurrent()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        var otherSeed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0, deploymentId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            releaseId = (await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false)).Id;
            deploymentId = (await DeployAsync(builder, seed.ProjectId, seed.DevEnvId, releaseId, channel.Id, "Success").ConfigureAwait(false)).Id;

            var otherChannel = await builder.CreateChannelAsync(otherSeed.ProjectId, otherSeed.LifecycleId).ConfigureAwait(false);

            for (var i = 1; i <= 4; i++)
            {
                var otherRelease = await builder.CreateReleaseAsync(otherSeed.ProjectId, otherChannel.Id, $"9.0.{i}").ConfigureAwait(false);
                await DeployAsync(builder, otherSeed.ProjectId, seed.DevEnvId, otherRelease.Id, otherChannel.Id, "Success").ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        var result = await GetProgressionAsync(seed.ProjectId).ConfigureAwait(false);

        result.Data.Releases.Select(r => r.Release.Id).ShouldBe(new[] { releaseId });
        result.Data.Releases[0].Deployments[seed.DevEnvId].Single(d => d.DeploymentId == deploymentId).IsCurrent.ShouldBeTrue();
        result.Data.ChannelReleaseCounts.Values.Sum().ShouldBe(1);
    }

    [Fact]
    public async Task GetProgression_LargeImportedHistory_ReturnsBoundedWindowQuickly()
    {
        var seed = await SeedLargeHistoryAsync(releaseCount: 5000, deploymentCount: 6500).ConfigureAwait(false);
        var expectedCurrentIds = await QueryCurrentDeploymentIdsAsync(seed.ProjectId).ConfigureAwait(false);

        await GetProgressionAsync(seed.ProjectId).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var result = await GetProgressionAsync(seed.ProjectId).ConfigureAwait(false);
        stopwatch.Stop();

        var maxReleases = 2 * GetProjectProgressionRequest.DefaultReleaseHistoryCount + 2 * seed.EnvironmentIds.Length;
        var returnedDeployments = result.Data.Releases.SelectMany(r => r.Deployments.Values).SelectMany(d => d).ToList();

        result.Data.Releases.Count.ShouldBeLessThanOrEqualTo(maxReleases, customMessage: "dashboard must load the recent window + dashboard releases, never the whole history");
        returnedDeployments.Count.ShouldBeLessThan(50, customMessage: "deployments must be loaded only for the selected releases");
        returnedDeployments.Where(d => d.IsCurrent).Select(d => d.DeploymentId).OrderBy(id => id).ToList().ShouldBe(expectedCurrentIds, customMessage: "IsCurrent must match the newest deployment per environment across ALL project deployments");
        result.Data.ChannelReleaseCounts[seed.DefaultChannelId].ShouldBe(4000);
        result.Data.ChannelReleaseCounts[seed.HotfixChannelId].ShouldBe(1000);
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(3000, customMessage: $"progression took {stopwatch.ElapsedMilliseconds}ms for 5000 releases / 6500 deployments — check the dashboard queries hit the 20261117 indexes (EXPLAIN on release/deployment)");
    }

    private async Task<GetProjectProgressionResponse> GetProgressionAsync(int projectId, int? releaseHistoryCount = null)
    {
        var request = new GetProjectProgressionRequest { ProjectId = projectId, ReleaseHistoryCount = releaseHistoryCount };

        return await Run<IProjectService, GetProjectProgressionResponse>(service => service.GetProjectProgressionAsync(request, CancellationToken.None)).ConfigureAwait(false);
    }

    private static async Task<Deployment> DeployAsync(TestDataBuilder builder, int projectId, int environmentId, int releaseId, int channelId, string state)
    {
        var task = await builder.CreateServerTaskAsync(state).ConfigureAwait(false);

        return await builder.CreateDeploymentAsync(projectId, environmentId, releaseId, task.Id, channelId).ConfigureAwait(false);
    }

    private record LargeHistorySeed(int ProjectId, int DefaultChannelId, int HotfixChannelId, int[] EnvironmentIds);

    private async Task<LargeHistorySeed> SeedLargeHistoryAsync(int releaseCount, int deploymentCount)
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        LargeHistorySeed large = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var stagingEnv = await builder.CreateEnvironmentAsync("Staging").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(seed.LifecycleId, stagingEnv.Id, "Staging Phase").ConfigureAwait(false);

            var defaultChannel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId, "Default").ConfigureAwait(false);
            var hotfixChannel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId, "Hotfix").ConfigureAwait(false);

            large = new LargeHistorySeed(seed.ProjectId, defaultChannel.Id, hotfixChannel.Id, new[] { seed.DevEnvId, stagingEnv.Id, seed.ProdEnvId });
        }).ConfigureAwait(false);

        await Run<DbContext>(ctx => BulkInsertHistoryAsync(ctx, large, releaseCount, deploymentCount)).ConfigureAwait(false);

        return large;
    }

    // Release g (1 = newest) goes to Hotfix every 5th, Default otherwise. Deployment n targets release
    // ((n - 1) % releaseCount) + 1 so the newest releases get a second deployment, rotating through the
    // environments; every 7th task Failed so "previous successful" is exercised at scale.
    private static async Task BulkInsertHistoryAsync(DbContext ctx, LargeHistorySeed seed, int releaseCount, int deploymentCount)
    {
        var marker = $"bulk-{seed.ProjectId}-";

        await ctx.Database.ExecuteSqlRawAsync("INSERT INTO release (version, created_date, project_id, project_variable_set_snapshot_id, project_deployment_process_snapshot_id, channel_id, space_id, last_modified_date) SELECT {0} || g, now() - make_interval(hours => g), {1}, 0, 0, CASE WHEN g % 5 = 0 THEN {2} ELSE {3} END, 1, now() FROM generate_series(1, {4}) g", marker, seed.ProjectId, seed.HotfixChannelId, seed.DefaultChannelId, releaseCount).ConfigureAwait(false);

        await ctx.Database.ExecuteSqlRawAsync("INSERT INTO server_task (name, description, queue_time, start_time, completed_time, state, has_warnings_or_errors, server_node_id, project_id, environment_id, duration_seconds, batch_id, json, space_id, concurrency_tag, error_message, business_process_state) SELECT {0} || g, '', now(), now(), now(), CASE WHEN g % 7 = 0 THEN 'Failed' ELSE 'Success' END, false, {2}, 0, 0, 1, 0, {3}, 1, '', '', '' FROM generate_series(1, {1}) g", marker, deploymentCount, Guid.Empty, "{}").ConfigureAwait(false);

        await ctx.Database.ExecuteSqlRawAsync("INSERT INTO deployment (name, task_id, space_id, channel_id, project_id, release_id, environment_id, machine_id, json, deployed_by, deployed_to_machine_ids, created_date) SELECT 'bulk', t.id, 1, r.channel_id, {1}, r.id, ({2})[1 + t.n % 3], 0, {3}, 0, '', r.created_date + make_interval(mins => 1 + (t.n - 1) / {4}) FROM (SELECT id, CAST(substring(name FROM length({0}) + 1) AS int) AS n FROM server_task WHERE name LIKE {0} || '%') t JOIN (SELECT id, channel_id, created_date, CAST(substring(version FROM length({0}) + 1) AS int) AS g FROM release WHERE project_id = {1}) r ON r.g = ((t.n - 1) % {4}) + 1", marker, seed.ProjectId, seed.EnvironmentIds, "{}", releaseCount).ConfigureAwait(false);
    }

    private async Task<List<int>> QueryCurrentDeploymentIdsAsync(int projectId)
    {
        var ids = new List<int>();

        await Run<DbContext>(async ctx => ids = await ctx.Database.SqlQueryRaw<int>("SELECT DISTINCT ON (environment_id) id AS \"Value\" FROM deployment WHERE project_id = {0} ORDER BY environment_id, created_date DESC, id DESC", projectId).ToListAsync().ConfigureAwait(false)).ConfigureAwait(false);

        return ids.OrderBy(id => id).ToList();
    }

    private record ProjectSeed(int ProjectId, int LifecycleId, int DevEnvId, int ProdEnvId);

    private async Task<ProjectSeed> SeedProjectWithLifecycleAsync()
    {
        var seed = new ProjectSeed(0, 0, 0, 0);

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var devEnv = await builder.CreateEnvironmentAsync("Development").ConfigureAwait(false);
            var prodEnv = await builder.CreateEnvironmentAsync("Production").ConfigureAwait(false);

            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, devEnv.Id, "Dev Phase").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, prodEnv.Id, "Prod Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);

            seed = new ProjectSeed(project.Id, lifecycle.Id, devEnv.Id, prodEnv.Id);
        }).ConfigureAwait(false);

        return seed;
    }
}
