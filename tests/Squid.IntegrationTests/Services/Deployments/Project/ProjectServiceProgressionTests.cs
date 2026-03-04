using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Project;
using Squid.IntegrationTests.Helpers;
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

    [Fact]
    public async Task GetProgression_Top3PerChannel_LimitsReleasesCorrectly()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int channelId = 0;
        var releaseVersions = new List<string>();

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            channelId = channel.Id;

            for (var i = 1; i <= 5; i++)
            {
                var version = $"1.0.{i}";
                releaseVersions.Add(version);
                await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, version).ConfigureAwait(false);
            }
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

        channelReleases.Count.ShouldBe(3);
        channelReleases.ShouldContain(r => r.Release.Version == "1.0.5");
        channelReleases.ShouldContain(r => r.Release.Version == "1.0.4");
        channelReleases.ShouldContain(r => r.Release.Version == "1.0.3");
    }

    [Fact]
    public async Task GetProgression_CurrentlyDeployedRelease_IncludedEvenBeyondTop3()
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
                LastModified = DateTimeOffset.UtcNow,
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
        deployment.Created.ShouldNotBe(default);
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
