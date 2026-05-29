using Autofac;
using Moq;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Rollback;
using Squid.Core.Services.Deployments.Rollback.Exceptions;
using Squid.IntegrationTests.Base;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.IntegrationTests.Services.Deployments.Rollback;

/// <summary>
/// PR-13 — integration tests for the rollback action against a real Postgres
/// journal. The SUCCESS path resolves the target from the real journal and
/// delegates to a stubbed <see cref="IDeploymentService"/> (we assert the
/// resolved release flows into the standard deploy command). The REJECTION
/// paths use the real services — they must throw before any deployment is
/// created.
/// </summary>
public class RollbackDeploymentTests : TestBase
{
    public RollbackDeploymentTests() : base("DeploymentRollbackAction", "squid_it_deployment_rollback_action")
    {
    }

    [Fact]
    public async Task RollbackDeployment_AutoTarget_DeploysPreviousRelease()
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
            v1ReleaseId = v1.Id;

            var t0 = DateTimeOffset.UtcNow.AddHours(-1);
            await DeployAsync(b, ctx, envId, v1.Id, "Success", t0).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v2.Id, "Success", t0.AddMinutes(10)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var captured = await CaptureDelegatedDeploymentAsync(
            new RollbackDeploymentCommand { ProjectId = projectId, EnvironmentId = envId, ReleaseId = null }).ConfigureAwait(false);

        captured.ShouldNotBeNull();
        captured.ReleaseId.ShouldBe(v1ReleaseId, customMessage: "Auto rollback MUST re-deploy the release running before the current one.");
        captured.EnvironmentId.ShouldBe(envId);
    }

    [Fact]
    public async Task RollbackDeployment_ExplicitRelease_DeploysThatRelease()
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
            await DeployAsync(b, ctx, envId, v2.Id, "Success", t0.AddMinutes(10)).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v3.Id, "Success", t0.AddMinutes(20)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var captured = await CaptureDelegatedDeploymentAsync(
            new RollbackDeploymentCommand { ProjectId = projectId, EnvironmentId = envId, ReleaseId = v1ReleaseId }).ConfigureAwait(false);

        captured.ReleaseId.ShouldBe(v1ReleaseId, customMessage: "Operator-specified prior release MUST be the one re-deployed.");
    }

    [Fact]
    public async Task RollbackDeployment_SingleRelease_Throws()
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

        await ShouldRejectAsync(new RollbackDeploymentCommand { ProjectId = projectId, EnvironmentId = envId }).ConfigureAwait(false);
    }

    [Fact]
    public async Task RollbackDeployment_CurrentRelease_Throws()
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
            v2ReleaseId = v2.Id;

            var t0 = DateTimeOffset.UtcNow.AddHours(-1);
            await DeployAsync(b, ctx, envId, v1.Id, "Success", t0).ConfigureAwait(false);
            await DeployAsync(b, ctx, envId, v2.Id, "Success", t0.AddMinutes(10)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await ShouldRejectAsync(new RollbackDeploymentCommand { ProjectId = projectId, EnvironmentId = envId, ReleaseId = v2ReleaseId }).ConfigureAwait(false);
    }

    [Fact]
    public async Task RollbackDeployment_UnknownRelease_Throws()
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

        await ShouldRejectAsync(new RollbackDeploymentCommand { ProjectId = projectId, EnvironmentId = envId, ReleaseId = 999999 }).ConfigureAwait(false);
    }

    private async Task<CreateDeploymentCommand> CaptureDelegatedDeploymentAsync(RollbackDeploymentCommand command)
    {
        CreateDeploymentCommand captured = null;

        var deploymentService = new Mock<IDeploymentService>();
        deploymentService
            .Setup(service => service.CreateDeploymentAsync(It.IsAny<CreateDeploymentCommand>(), It.IsAny<CancellationToken>()))
            .Callback<CreateDeploymentCommand, CancellationToken>((create, _) => captured = create)
            .ReturnsAsync(new DeploymentCreatedEvent { TaskId = 1, Deployment = new DeploymentDto { Id = 1 } });

        await Run<IRollbackService>(
            async service => await service.RollbackDeploymentAsync(command, CancellationToken.None).ConfigureAwait(false),
            builder => builder.RegisterInstance(deploymentService.Object).As<IDeploymentService>()).ConfigureAwait(false);

        return captured;
    }

    private async Task ShouldRejectAsync(RollbackDeploymentCommand command)
        => await Run<IRollbackService>(async service =>
            await Should.ThrowAsync<RollbackNotAvailableException>(
                () => service.RollbackDeploymentAsync(command, CancellationToken.None)).ConfigureAwait(false)).ConfigureAwait(false);

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
