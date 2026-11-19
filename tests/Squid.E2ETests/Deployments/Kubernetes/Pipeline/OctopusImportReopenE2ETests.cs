using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for reopening an import after the page was reloaded part way through a read from Octopus. A real-socket stub
/// Octopus; the production session list, REST client, fetcher, bundle writer, planner, validator and confirmation run through the
/// mediator against the real database; the imported project deploys through the REAL pipeline (<c>ProcessAsync</c>) with only the
/// execution strategy captured, so no Kind cluster is required. Every piece of client state is thrown away after the first chunk: the
/// list alone says where the session stands and which server it reads, and the read resumes from the persisted cursor with the key
/// entered again. Another Squid user can neither see nor reopen the session. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusImportReopenE2ETests : IClassFixture<OctopusImportReopenE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-REOPEN-1";
    private const int OtherSquidUserId = 4343;

    private readonly ImportFixture _fixture;

    public OctopusImportReopenE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReloadMidRead_ListThenResumeWithKey_ImportsAndDeploys_OtherUserCannotSee()
    {
        var (nativeTaskId, squidProject) = await SeedPreviouslyImportedProjectAsync($"squid-e2e-{Guid.NewGuid():N}"[..18]);
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "0.9.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");
        var sessionId = await StartReadAsync(octopus);

        var listed = (await ListAsync()).ShouldHaveSingleItem(customMessage: "the reloaded page finds the session it was reading");

        listed.SessionId.ShouldBe(sessionId);
        listed.ResumeStep.ShouldBe(OctopusImportResumeStep.ResumeRead, customMessage: "a read that stopped part way asks for the key again");
        listed.ServerUrl.ShouldBe(octopus.BaseUri.ToString().TrimEnd('/'));
        listed.SelectedProjectCount.ShouldBe(1);
        (await ListAsync(OtherSquidUserId)).ShouldBeEmpty(customMessage: "another Squid user never sees the session");
        await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => StatusAsync(sessionId, OtherSquidUserId));

        var import = await _fixture.Run<IMediator, OctopusImportFlowResult>(async mediator =>
        {
            var fetch = await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, 1, sessionId, new Uri(listed.ServerUrl), ApiKey);
            return await OctopusImportHandlerFlow.FinishServerImportAsync(mediator, 1, sessionId, fetch);
        });

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(import));
        octopus.Requests.Count(r => r.PathAndQuery.StartsWith("/api/Spaces-1/projectgroups")).ShouldBe(1, customMessage: "the page read before the reload is not read again; inspect the stub's Requests log");
        import.PreviewPlan.Summary.Projects.Single().Destination.ProjectId.ShouldBe(squidProject.Id);
        (await ListAsync()).ShouldBeEmpty(customMessage: "a finished import is no longer offered to continue");

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(nativeTaskId, CancellationToken.None));

        (await TaskStateAsync(nativeTaskId)).ShouldBe(TaskState.Success, customMessage: "the project must still deploy after the reopened import; check the captured requests and the task log");
    }

    // Connect with the key, choose the project, read one page; the page is then reloaded, so nothing of this survives on the client.
    private Task<Guid> StartReadAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, Guid>(async mediator =>
        {
            var id = (await OctopusImportHandlerFlow.ConnectAsync(mediator, 1, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.SelectProjectsAsync(mediator, 1, id, ["Projects-1"]);
            (await OctopusImportHandlerFlow.FetchHistoryAsync(mediator, 1, id, octopus.BaseUri, ApiKey)).Data.Done.ShouldBeFalse();
            return id;
        });

    private Task<List<OctopusImportResumableSessionDto>> ListAsync(int? userId = null)
        => _fixture.Run<IMediator, List<OctopusImportResumableSessionDto>>(async mediator => (await mediator.RequestAsync<ListOctopusImportSessionsRequest, ListOctopusImportSessionsResponse>(new ListOctopusImportSessionsRequest { SpaceId = 1 })).Data, AsUser(userId));

    private Task<GetOctopusImportStatusResponse> StatusAsync(Guid sessionId, int userId)
        => _fixture.Run<IMediator, GetOctopusImportStatusResponse>(mediator => mediator.RequestAsync<GetOctopusImportStatusRequest, GetOctopusImportStatusResponse>(new GetOctopusImportStatusRequest { SpaceId = 1, SessionId = sessionId }), AsUser(userId));

    private static Action<ContainerBuilder> AsUser(int? userId) => builder =>
    {
        if (userId != null) builder.RegisterInstance<ICurrentUser>(new OtherUser(userId.Value)).SingleInstance();
    };

    // ── Seeding ──────────────────────────────────────────────────────────

    // The K8s scenario (project, TEST target, release 1.0.0 with a pending native deploy), recorded in the source map as Octopus
    // Projects-1 / Channels-1 / Environments-1 imported earlier from the stub's installation.
    private async Task<(int NativeTaskId, Project Project)> SeedPreviouslyImportedProjectAsync(string namespaceName)
    {
        _fixture.ExecutionCapture.Clear();
        var nativeTaskId = 0;
        Project seeded = null;

        await _fixture.Run<IRepository, IUnitOfWork, IDeploymentAccountDataProvider>(async (repository, unitOfWork, accounts) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork, accounts);
            await seeder.SeedAsync(createFeedSecrets: false, replicas: 0, namespaceName, "KubernetesApi");

            var project = await repository.Query<Project>().OrderByDescending(p => p.Id).FirstAsync();
            var channel = await repository.Query<Channel>(c => c.ProjectId == project.Id).OrderByDescending(c => c.Id).FirstAsync();
            var environment = await repository.Query<Environment>().OrderByDescending(e => e.Id).FirstAsync();

            nativeTaskId = seeder.ServerTaskId;
            seeded = project;
            await RecordImportedBeforeAsync(project.Id, channel.Id, environment.Id);
        });

        return (nativeTaskId, seeded);
    }

    private Task RecordImportedBeforeAsync(int projectId, int channelId, int environmentId)
        => _fixture.Run<IOctopusImportSourceMapDataProvider>(provider => provider.UpsertAsync(
        [
            new OctopusImportSourceMapRow(1, "Project", "Projects-1", projectId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId },
            new OctopusImportSourceMapRow(1, "Channel", "Channels-1", channelId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId },
            new OctopusImportSourceMapRow(1, "Environment", "Environments-1", environmentId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId }
        ], Guid.NewGuid()));

    // ── Queries ──────────────────────────────────────────────────────────

    private Task<string> TaskStateAsync(int taskId)
        => _fixture.Run<SquidDbContext, string>(db => db.Set<ServerTaskEntity>().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync());

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    private sealed class OtherUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => "octopus-import-e2e-other-user";
        public bool IsInternal => false;
    }

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host; one page per chunk, so a read stops part way.</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusImportReopenE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true, ChunkDuration = TimeSpan.Zero });
            builder.RegisterInstance(OpenAuthorization()).As<IAuthorizationService>();
        }

        private static IAuthorizationService OpenAuthorization()
        {
            var authorization = new Moq.Mock<IAuthorizationService>();
            authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            return authorization.Object;
        }
    }
}
