using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Shouldly;
using Xunit;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for D16. A real-socket stub Octopus withholds a sensitive variable; the production REST client, planner and
/// confirmation import it empty through the mediator into the real database. A release of that project is then deployed through the
/// real <see cref="IDeploymentTaskExecutor"/>: the preflight fails the task before any step runs and names the variable, its project
/// and its scope. The export file of the same installation fills the value; a release created after the fill deploys, while the
/// release snapshotted before it keeps failing with "update the release". Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusMissingSecretDeploymentE2ETests : IClassFixture<OctopusMissingSecretDeploymentE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-MISSING-SECRET-KEY-1";
    private const string ExportPassword = "e2e-export-password";

    private readonly ImportFixture _fixture;

    public OctopusMissingSecretDeploymentE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApiImport_DeployFailsPreflightNamingTheSecret_FileFill_NewReleaseDeploys_OldReleaseStillRefused()
    {
        await EnsureSpaceAsync();
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();
        (await ImportFromServerAsync(octopus)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var before = await CreateReleaseAsync("1.0.0");

        var refused = await DeployAsync(before);

        refused.State.ShouldBe(TaskState.Failed, customMessage: "a deployment that would read a withheld secret must not run");
        refused.Exception.ShouldBeOfType<ImportedSecretMissingException>().Message.ShouldContain("- \"Db.Password\" (Environment: Server TEST) in project \"Server project\": secret not imported from Octopus");

        var fill = await _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, 1, OctopusSecretFillImportE2ETests.SameInstallationExport(), ExportPassword, true));
        fill.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);

        var stale = await DeployAsync(before);
        stale.Exception.ShouldBeOfType<ImportedSecretMissingException>().Secrets.Single().Reason.ShouldBe(MissingImportedSecretReason.SnapshotPredatesValue);

        var deployed = await DeployAsync(await CreateReleaseAsync("1.0.1"));
        deployed.Exception.ShouldBeNull();
        deployed.State.ShouldBe(TaskState.Success);
    }

    private Task<OctopusImportFlowResult> ImportFromServerAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

    private Task EnsureSpaceAsync()
        => _fixture.Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == 1)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

    private async Task<int> CreateReleaseAsync(string version)
    {
        var releaseId = 0;

        await _fixture.Run<SquidDbContext, IReleaseService>(async (db, releases) =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync(p => p.Name == "Server project");
            var channel = await db.Set<Channel>().AsNoTracking().SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);

            releaseId = (await releases.CreateReleaseAsync(new CreateReleaseCommand { SpaceId = 1, ProjectId = project.Id, ChannelId = channel.Id, Version = version })).Release.Id;
        });

        return releaseId;
    }

    // Created and executed in separate scopes, as the API and the background runner do.
    private async Task<(Exception Exception, string State)> DeployAsync(int releaseId)
    {
        Exception failure = null;
        var taskId = 0;

        await _fixture.Run<SquidDbContext, IDeploymentService>(async (db, deployments) =>
        {
            var environment = await db.Set<SquidEnvironment>().AsNoTracking().SingleAsync(e => e.Name == "Server TEST");
            taskId = (await deployments.CreateDeploymentAsync(new CreateDeploymentCommand { SpaceId = 1, ReleaseId = releaseId, EnvironmentId = environment.Id, Name = $"Deploy {releaseId}" })).TaskId;
        });

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            try
            {
                await executor.ProcessAsync(taskId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        var state = await _fixture.Run<SquidDbContext, string>(db => db.Set<ServerTask>().AsNoTracking().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync());

        return (failure, state);
    }

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host (see <see cref="OctopusSecretFillImportE2ETests.ImportFixture"/>).</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusMissingSecretDeploymentE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true });

            var authorization = new Moq.Mock<IAuthorizationService>();
            authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            builder.RegisterInstance(authorization.Object).As<IAuthorizationService>();
        }
    }
}
