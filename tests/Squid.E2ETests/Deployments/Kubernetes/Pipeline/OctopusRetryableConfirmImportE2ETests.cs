using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the retryable confirmation. A real-socket stub Octopus is read by the production REST client and fetcher; the
/// plan is previewed and validated through the mediator into the real database. Faults are real <c>PostgresException</c>s raised by a
/// trigger on a destination table inside the real import transaction: a persistent one rolls the import back and leaves the session
/// Validated with the reason (the status endpoint describes it), and pressing Import again with the same plan — no re-read, no
/// re-validate — imports it; a transient one is retried by the confirmation itself without anyone pressing anything. Its own class
/// (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusRetryableConfirmImportE2ETests : IClassFixture<OctopusRetryableConfirmImportE2ETests.ImportFixture>
{
    private const int SpaceId = 1;
    private const string ApiKey = "API-E2E-RETRYABLE-CONFIRM-1";

    private readonly ImportFixture _fixture;

    public OctopusRetryableConfirmImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PersistentFault_LeavesSessionValidatedWithReason_ImportAgainWithoutRereadSucceeds()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");
        var sessionId = await ReadAndValidateAsync(octopus);
        var reads = octopus.Requests.Count;

        try
        {
            await InjectFaultAsync("RAISE EXCEPTION 'injected bug' USING ERRCODE = 'P0001'", int.MaxValue);

            var refused = await ConfirmAsync(sessionId);

            refused.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated, customMessage: "a rolled-back import leaves the session importable again, not Failed");
            refused.Data.Session.LastConfirmationFailure.ShouldNotBeNull().Reason.ShouldBe(OctopusImportConfirmationFailureReason.RolledBack);
            var status = await StatusAsync(sessionId);
            status.Data.Session.LastConfirmationFailure.ShouldNotBeNull("the page reopened after a reload still sees why");
            status.Data.BlockerSummary.HasBlockers.ShouldBeTrue();
            status.Data.Session.ConfirmationProgress.ShouldNotBeNull("the page shows how far the rolled-back attempt got").ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.RolledBack), p => p.Finished.ShouldBeTrue(), p => p.AttemptId.ShouldBe(status.Data.Session.LastConfirmationFailure.AttemptId));
            (await ImportedProjectsAsync()).ShouldBe(0, "nothing was written by the rolled-back attempt");
        }
        finally
        {
            await DropFaultAsync();
        }

        var again = await ConfirmAsync(sessionId);

        again.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: again.Msg);
        again.Data.Session.LastConfirmationFailure.ShouldBeNull();
        (await StatusAsync(sessionId)).Data.Session.ConfirmationProgress.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.Attempt.ShouldBe(1), p => p.ItemsDone.ShouldBe(p.ItemsTotal));
        octopus.Requests.Count.ShouldBe(reads, "importing again uses the staged read, never the Octopus server");
        (await ImportedProjectsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task TransientFault_RetriedByTheConfirmation_SucceedsWithoutUserAction()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0");
        var sessionId = await ReadAndValidateAsync(octopus);

        try
        {
            await InjectFaultAsync("RAISE EXCEPTION 'injected serialization failure' USING ERRCODE = '40001'", 1);

            var confirm = await ConfirmAsync(sessionId);

            confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: confirm.Msg);
            (await StatusAsync(sessionId)).Data.Session.ConfirmationProgress.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.Attempt.ShouldBe(2, "the transient retry counted again from zero"), p => p.ItemsDone.ShouldBe(p.ItemsTotal));
        }
        finally
        {
            await DropFaultAsync();
        }
    }

    // ── Flow ──────────────────────────────────────────────────────────────

    private async Task<Guid> ReadAndValidateAsync(StubOctopusServer octopus)
    {
        await ResetDestinationAsync();

        return await _fixture.Run<IMediator, Guid>(async mediator =>
        {
            var sessionId = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey);
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(System.Net.HttpStatusCode.OK);

            var preview = (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan;
            preview.HasBlockers.ShouldBeFalse(OctopusImportHandlerFlow.DescribeBlockers(OctopusImportHandlerFlow.AllDiagnostics(preview)));

            var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview);
            validate.Code.ShouldBe(System.Net.HttpStatusCode.OK, customMessage: validate.Msg);

            return sessionId;
        });
    }

    private Task<ConfirmOctopusImportResponse> ConfirmAsync(Guid sessionId) => _fixture.Run<IMediator, ConfirmOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId));

    private Task<GetOctopusImportStatusResponse> StatusAsync(Guid sessionId) => _fixture.Run<IMediator, GetOctopusImportStatusResponse>(mediator => mediator.RequestAsync<GetOctopusImportStatusRequest, GetOctopusImportStatusResponse>(new GetOctopusImportStatusRequest { SpaceId = SpaceId, SessionId = sessionId }));

    // Each test imports the same stub space into a destination of its own: the previous test's rows are removed first.
    private Task ResetDestinationAsync() => _fixture.Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DELETE FROM octopus_import_source_map; DELETE FROM project; DELETE FROM project_group;"));

    private Task<int> ImportedProjectsAsync() => _fixture.Run<SquidDbContext, int>(db => db.Set<Project>().CountAsync());

    // A real Postgres error from inside the real import transaction; the counter is a sequence, which a rollback does not undo.
    private Task InjectFaultAsync(string fault, int times) => _fixture.Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync(
        "DROP TRIGGER IF EXISTS e2e_injected_fault ON project_group; DROP SEQUENCE IF EXISTS e2e_injected_fault_seq; CREATE SEQUENCE e2e_injected_fault_seq; " +
        "CREATE OR REPLACE FUNCTION e2e_injected_fault() RETURNS trigger LANGUAGE plpgsql AS $fn$ BEGIN IF nextval('e2e_injected_fault_seq') <= " + times + " THEN " + fault + "; END IF; RETURN NEW; END $fn$; " +
        "CREATE TRIGGER e2e_injected_fault BEFORE INSERT ON project_group FOR EACH ROW EXECUTE FUNCTION e2e_injected_fault();"));

    private Task DropFaultAsync() => _fixture.Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS e2e_injected_fault ON project_group; DROP FUNCTION IF EXISTS e2e_injected_fault(); DROP SEQUENCE IF EXISTS e2e_injected_fault_seq;"));

    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusRetryableConfirmImportE2ETests>
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
