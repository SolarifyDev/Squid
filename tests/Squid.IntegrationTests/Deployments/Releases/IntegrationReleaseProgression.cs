using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Release;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.IntegrationTests.Deployments.Releases;

public class IntegrationReleaseProgression : TestBase
{
    public IntegrationReleaseProgression()
        : base("ReleaseProgression", "squid_it_release_progression")
    {
    }

    [Fact]
    public async Task NewRelease_NoDeployments_OnlyFirstPhaseCanDeploy()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            releaseId = release.Id;
        }).ConfigureAwait(false);

        var result = await Run<IReleaseService, GetReleaseProgressionResponse>(async service =>
        {
            return await service.GetReleaseProgressionAsync(
                new GetReleaseProgressionRequest { ReleaseId = releaseId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result.Data.ShouldNotBeNull();
        result.Data.ReleaseId.ShouldBe(releaseId);
        result.Data.ReleaseVersion.ShouldBe("1.0.0");
        result.Data.Phases.Count.ShouldBe(2);

        var devPhase = result.Data.Phases[0];
        devPhase.Progress.ShouldBe("Current");
        devPhase.IsComplete.ShouldBeFalse();
        devPhase.Environments.Count.ShouldBe(1);
        devPhase.Environments[0].EnvironmentId.ShouldBe(seed.DevEnvId);
        devPhase.Environments[0].CanDeploy.ShouldBeTrue();

        var prodPhase = result.Data.Phases[1];
        prodPhase.Progress.ShouldBe("Pending");
        prodPhase.IsComplete.ShouldBeFalse();
        prodPhase.Environments[0].EnvironmentId.ShouldBe(seed.ProdEnvId);
        prodPhase.Environments[0].CanDeploy.ShouldBeFalse();
    }

    [Fact]
    public async Task AfterFirstPhaseDeployed_SecondPhaseBecomesCanDeploy()
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
            var deployment = await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release.Id, task.Id, channel.Id).ConfigureAwait(false);
            await builder.CreateDeploymentCompletionAsync(deployment.Id, "Success").ConfigureAwait(false);
        }).ConfigureAwait(false);

        var result = await Run<IReleaseService, GetReleaseProgressionResponse>(async service =>
        {
            return await service.GetReleaseProgressionAsync(
                new GetReleaseProgressionRequest { ReleaseId = releaseId },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var devPhase = result.Data.Phases[0];
        devPhase.Progress.ShouldBe("Complete");
        devPhase.IsComplete.ShouldBeTrue();
        devPhase.Environments[0].CanDeploy.ShouldBeTrue();
        devPhase.Environments[0].Deployment.ShouldNotBeNull();
        devPhase.Environments[0].Deployment.State.ShouldBe("Success");

        var prodPhase = result.Data.Phases[1];
        prodPhase.Progress.ShouldBe("Current");
        prodPhase.IsComplete.ShouldBeFalse();
        prodPhase.Environments[0].CanDeploy.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoReleases_EvaluatedIndependently()
    {
        var seed = await SeedProjectWithLifecycleAsync().ConfigureAwait(false);
        int release1Id = 0, release2Id = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var channel = await builder.CreateChannelAsync(seed.ProjectId, seed.LifecycleId).ConfigureAwait(false);

            var release1 = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "1.0.0").ConfigureAwait(false);
            release1Id = release1.Id;

            var release2 = await builder.CreateReleaseAsync(seed.ProjectId, channel.Id, "2.0.0").ConfigureAwait(false);
            release2Id = release2.Id;

            // Only release1 has been deployed to Dev
            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            var deployment = await builder.CreateDeploymentAsync(seed.ProjectId, seed.DevEnvId, release1.Id, task.Id, channel.Id).ConfigureAwait(false);
            await builder.CreateDeploymentCompletionAsync(deployment.Id, "Success").ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Release 1: Dev deployed → Prod allowed
        var result1 = await Run<IReleaseService, GetReleaseProgressionResponse>(async service =>
        {
            return await service.GetReleaseProgressionAsync(
                new GetReleaseProgressionRequest { ReleaseId = release1Id },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result1.Data.Phases[0].IsComplete.ShouldBeTrue();
        result1.Data.Phases[1].Environments[0].CanDeploy.ShouldBeTrue();

        // Release 2: no deployments → Prod NOT allowed
        var result2 = await Run<IReleaseService, GetReleaseProgressionResponse>(async service =>
        {
            return await service.GetReleaseProgressionAsync(
                new GetReleaseProgressionRequest { ReleaseId = release2Id },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        result2.Data.Phases[0].IsComplete.ShouldBeFalse();
        result2.Data.Phases[1].Environments[0].CanDeploy.ShouldBeFalse();
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
