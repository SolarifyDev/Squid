using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Rollback;
using Squid.IntegrationTests.Base;
using Squid.IntegrationTests.Helpers;

namespace Squid.IntegrationTests.Services.Deployments.Rollback;

/// <summary>
/// PR-12 — integration tests for the rollback-target resolver against a real
/// Postgres database. Seeds the deployment journal (Deployment +
/// DeploymentCompletion) and asserts the resolver picks the prior distinct
/// release, scoped to the environment and ignoring failed deployments.
/// </summary>
public class RollbackTargetResolutionTests : TestBase
{
    public RollbackTargetResolutionTests() : base("DeploymentRollback", "squid_it_deployment_rollback")
    {
    }

    [Fact]
    public async Task GetRollbackTarget_LinearReleaseHistory_ResolvesImmediatelyPrecedingRelease()
    {
        int projectId = 0, envId = 0, v2ReleaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var b = new TestDataBuilder(repository, unitOfWork);
            var ctx = await SeedProjectAsync(b).ConfigureAwait(false);
            projectId = ctx.ProjectId;
            envId = ctx.EnvId;

            var v1 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "1.0.0").ConfigureAwait(false);
            var v2 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "2.0.0").ConfigureAwait(false);
            var v3 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "3.0.0").ConfigureAwait(false);
            v2ReleaseId = v2.Id;

            var t0 = DateTimeOffset.UtcNow.AddHours(-1);
            await DeployAsync(b, ctx, envId, v1.Id, "Success", t0).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v2.Id, "Success", t0.AddMinutes(10)).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v3.Id, "Success", t0.AddMinutes(20)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var target = await ResolveTargetAsync(projectId, envId).ConfigureAwait(false);

        target.ShouldNotBeNull();
        target.ReleaseId.ShouldBe(v2ReleaseId);
        target.ReleaseVersion.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task GetSuccessfulReleaseHistory_ReturnsNewestFirstWithVersions()
    {
        int projectId = 0, envId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var b = new TestDataBuilder(repository, unitOfWork);
            var ctx = await SeedProjectAsync(b).ConfigureAwait(false);
            projectId = ctx.ProjectId;
            envId = ctx.EnvId;

            var v1 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "1.0.0").ConfigureAwait(false);
            var v2 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "2.0.0").ConfigureAwait(false);

            var t0 = DateTimeOffset.UtcNow.AddHours(-1);
            await DeployAsync(b, ctx, envId, v1.Id, "Success", t0).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v2.Id, "Success", t0.AddMinutes(10)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        List<RollbackReleaseHistoryEntry> history = null;
        await Run<IDeploymentCompletionDataProvider>(async provider =>
            history = await provider.GetSuccessfulReleaseHistoryAsync(projectId, envId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        history.ShouldNotBeNull();
        history.Count.ShouldBe(2);
        history[0].ReleaseVersion.ShouldBe("2.0.0", customMessage: "Newest successful deployment MUST be first.");
        history[1].ReleaseVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task GetRollbackTarget_IgnoresOtherEnvironments()
    {
        int projectId = 0, envAId = 0, envBId = 0, v1ReleaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var b = new TestDataBuilder(repository, unitOfWork);
            var ctx = await SeedProjectAsync(b, "Environment A").ConfigureAwait(false);
            var envB = await b.CreateEnvironmentAsync("Environment B").ConfigureAwait(false);
            projectId = ctx.ProjectId;
            envAId = ctx.EnvId;
            envBId = envB.Id;

            var v1 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "1.0.0").ConfigureAwait(false);
            var v2 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "2.0.0").ConfigureAwait(false);
            var v3 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "3.0.0").ConfigureAwait(false);
            v1ReleaseId = v1.Id;

            var t0 = DateTimeOffset.UtcNow.AddHours(-1);
            await DeployAsync(b, ctx, envAId, v1.Id, "Success", t0).ConfigureAwait(false);
            await DeployAsync(b, ctx, envAId, v2.Id, "Success", t0.AddMinutes(10)).ConfigureAwait(false);
            // env B gets a LATER deployment of a different release — must not
            // leak into env A's rollback resolution.
            await DeployAsync(b, ctx, envBId, v3.Id, "Success", t0.AddMinutes(20)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var targetA = await ResolveTargetAsync(projectId, envAId).ConfigureAwait(false);
        var targetB = await ResolveTargetAsync(projectId, envBId).ConfigureAwait(false);

        targetA.ShouldNotBeNull();
        targetA.ReleaseId.ShouldBe(v1ReleaseId,
            customMessage: "Env A's previous release is 1.0.0; env B's later 3.0.0 deployment MUST NOT affect it.");
        targetB.ShouldBeNull(customMessage: "Env B has only one release — nothing to roll back to.");
    }

    [Fact]
    public async Task GetRollbackTarget_ExcludesFailedDeployments()
    {
        int projectId = 0, envId = 0, v1ReleaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var b = new TestDataBuilder(repository, unitOfWork);
            var ctx = await SeedProjectAsync(b).ConfigureAwait(false);
            projectId = ctx.ProjectId;
            envId = ctx.EnvId;

            var v1 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "1.0.0").ConfigureAwait(false);
            var v2 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "2.0.0").ConfigureAwait(false);
            var v3 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "3.0.0").ConfigureAwait(false);
            v1ReleaseId = v1.Id;

            var t0 = DateTimeOffset.UtcNow.AddHours(-1);
            await DeployAsync(b, ctx, envId, v1.Id, "Success", t0).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v2.Id, "Failed", t0.AddMinutes(10)).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v3.Id, "Success", t0.AddMinutes(20)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var target = await ResolveTargetAsync(projectId, envId).ConfigureAwait(false);

        // Successful history is v3 (current) then v1 — the failed v2 is skipped,
        // so rollback targets 1.0.0, not the never-successful 2.0.0.
        target.ShouldNotBeNull();
        target.ReleaseId.ShouldBe(v1ReleaseId);
        target.ReleaseVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task GetRollbackTarget_SingleRelease_ReturnsNull()
    {
        int projectId = 0, envId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var b = new TestDataBuilder(repository, unitOfWork);
            var ctx = await SeedProjectAsync(b).ConfigureAwait(false);
            projectId = ctx.ProjectId;
            envId = ctx.EnvId;

            var v1 = await b.CreateReleaseAsync(ctx.ProjectId, ctx.ChannelId, "1.0.0").ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v1.Id, "Success", DateTimeOffset.UtcNow.AddMinutes(-10)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var target = await ResolveTargetAsync(projectId, envId).ConfigureAwait(false);

        target.ShouldBeNull();
    }

    private async Task<RollbackReleaseHistoryEntry> ResolveTargetAsync(int projectId, int environmentId)
    {
        RollbackReleaseHistoryEntry target = null;
        await Run<IRollbackService>(async svc =>
            target = await svc.GetRollbackTargetAsync(projectId, environmentId, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        return target;
    }

    private record SeedContext(int ProjectId, int EnvId, int ChannelId);

    private static async Task<SeedContext> SeedProjectAsync(TestDataBuilder b, string envName = "Production")
    {
        var lifecycle = await b.CreateLifecycleAsync().ConfigureAwait(false);
        var group = await b.CreateProjectGroupAsync().ConfigureAwait(false);
        var env = await b.CreateEnvironmentAsync(envName).ConfigureAwait(false);
        var varSet = await b.CreateVariableSetAsync().ConfigureAwait(false);
        var project = await b.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);
        var channel = await b.CreateChannelAsync(project.Id, lifecycle.Id).ConfigureAwait(false);
        return new SeedContext(project.Id, env.Id, channel.Id);
    }

    private static async Task DeployAsync(TestDataBuilder b, SeedContext ctx, int environmentId, int releaseId, string state, DateTimeOffset completedTime)
    {
        var task = await b.CreateServerTaskAsync(state).ConfigureAwait(false);
        var deployment = await b.CreateDeploymentAsync(ctx.ProjectId, environmentId, releaseId, task.Id, ctx.ChannelId).ConfigureAwait(false);
        await b.CreateDeploymentCompletionAsync(deployment.Id, state, completedTime).ConfigureAwait(false);
    }
}
