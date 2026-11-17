using System.Linq.Expressions;
using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.IntegrationTests.Base;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the live progress of a confirmation. A real-socket stub Octopus with many releases and deployments is read by
/// the production REST client and fetcher, previewed and validated through the mediator into the real database; the confirmation is
/// queued as the page queues it and the captured job runs in a fresh scope, as Hangfire would. Meanwhile the page's own status request
/// is polled, exactly what the frontend does: the progress it returns never goes back and ends complete. A second test pauses the import
/// inside its open transaction (a real Postgres advisory lock behind a trigger) and reloads the page: the resumable list offers to follow
/// the import and the status carries its live progress. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusImportConfirmProgressE2ETests : IClassFixture<OctopusImportConfirmProgressE2ETests.ImportFixture>
{
    private const int SpaceId = 1;
    private const int ReleaseCount = 40;
    private const long ProjectInsertGate = 918_273_646;
    private const string ApiKey = "API-E2E-CONFIRM-PROGRESS-1";

    private readonly ImportFixture _fixture;

    public OctopusImportConfirmProgressE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BackgroundConfirm_ManyReleases_StatusProgressNeverGoesBack_EndsComplete()
    {
        await using var octopus = StubWithHistory();
        var sessionId = await ReadAndValidateAsync(octopus);
        var job = await QueueConfirmationAsync(sessionId);

        var run = _fixture.Run<IOctopusImportConfirmationJob>(runner => job.Compile()(runner));
        var seen = await PollProgressUntilAsync(sessionId, run);

        await run;
        var final = (await StatusAsync(sessionId)).Data.Session;
        final.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var progress = final.ConfirmationProgress.ShouldNotBeNull("the final progress stays readable after the import ends");
        progress.ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.Finished.ShouldBeTrue(), p => p.ItemsDone.ShouldBe(p.ItemsTotal), p => p.RecentLog.Count.ShouldBeLessThanOrEqualTo(20));
        progress.Phases.ShouldContain(p => p.Phase == OctopusImportConfirmationPhase.Releases && p.Total == ReleaseCount && p.Done == ReleaseCount);
        progress.Phases.ShouldContain(p => p.Phase == OctopusImportConfirmationPhase.Deployments && p.Total == ReleaseCount);
        seen.Zip(seen.Skip(1)).ShouldAllBe(pair => pair.Second.ItemsDone >= pair.First.ItemsDone, "the progress the page polls must never go back within one attempt");
    }

    // The page is reloaded while the import is paused inside its transaction: the list says to follow it, the status shows how far it got.
    [Fact]
    public async Task ImportPausedMidway_ReloadedPage_ListsFollowImport_StatusCarriesLiveProgress()
    {
        await using var octopus = StubWithHistory();
        var sessionId = await ReadAndValidateAsync(octopus);

        try
        {
            await InstallProjectInsertGateAsync();

            await _fixture.Run<SquidDbContext>(async gate =>
            {
                await gate.Database.OpenConnectionAsync();
                await gate.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({ProjectInsertGate})");

                var confirm = _fixture.Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId));
                var midway = await WaitForProgressAsync(sessionId, p => p.Current?.Kind == "Project");

                (await ListAsync()).ShouldContain(s => s.SessionId == sessionId && s.ResumeStep == OctopusImportResumeStep.FollowImport, customMessage: "the reloaded page must offer to follow the running import");
                midway.ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Importing), p => p.ItemsDone.ShouldBeGreaterThan(0), p => p.ItemsDone.ShouldBeLessThan(p.ItemsTotal), p => p.Current.Name.ShouldNotBeNullOrWhiteSpace());

                await gate.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({ProjectInsertGate})");
                (await confirm).Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
            });
        }
        finally
        {
            await DropProjectInsertGateAsync();
        }

        (await StatusAsync(sessionId)).Data.Session.ConfirmationProgress.ShouldNotBeNull().Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded);
    }

    // ── Flow ──────────────────────────────────────────────────────────────

    private static StubOctopusServer StubWithHistory()
    {
        var octopus = StubOctopusServer.Start(ApiKey);

        for (var i = 1; i <= ReleaseCount; i++) octopus.Release("Projects-1", $"Releases-{i}", $"1.0.{i}").Deployment("Projects-1", $"Deployments-{i}", $"Releases-{i}", $"ServerTasks-{i}");

        return octopus;
    }

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

    // The page's own request: queued, the session stays Validated until the job admits it.
    private async Task<Expression<Func<IOctopusImportConfirmationJob, Task>>> QueueConfirmationAsync(Guid sessionId)
    {
        Expression<Func<IOctopusImportConfirmationJob, Task>> job = null;

        await _fixture.Run<IMediator, ISquidBackgroundJobClient>(async (mediator, jobs) =>
        {
            var confirm = await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId, runInBackground: true);
            confirm.Code.ShouldBe(System.Net.HttpStatusCode.OK, customMessage: confirm.Msg);

            job = ((MockSquidBackgroundJobClient)jobs).EnqueuedJobs.OfType<Expression<Func<IOctopusImportConfirmationJob, Task>>>().Last();
        });

        return job;
    }

    // Polls the status as the page does, every 100 ms until the job ends, keeping every progress it saw.
    private async Task<List<OctopusImportConfirmationProgressDto>> PollProgressUntilAsync(Guid sessionId, Task job)
    {
        var seen = new List<OctopusImportConfirmationProgressDto>();

        while (!job.IsCompleted)
        {
            if ((await StatusAsync(sessionId)).Data.Session.ConfirmationProgress is { Finished: false } progress) seen.Add(progress);

            await Task.WhenAny(job, Task.Delay(100));
        }

        return seen;
    }

    private async Task<OctopusImportConfirmationProgressDto> WaitForProgressAsync(Guid sessionId, Func<OctopusImportConfirmationProgressDto, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if ((await StatusAsync(sessionId)).Data.Session.ConfirmationProgress is { } progress && condition(progress)) return progress;

            await Task.Delay(100);
        }

        throw new TimeoutException($"Session {sessionId} showed no live confirmation progress within 60s while the import was paused: the progress writer did not land outside the import transaction. Inspect octopus_import_session.confirmation_progress_json manually.");
    }

    private Task<GetOctopusImportStatusResponse> StatusAsync(Guid sessionId) => _fixture.Run<IMediator, GetOctopusImportStatusResponse>(mediator => mediator.RequestAsync<GetOctopusImportStatusRequest, GetOctopusImportStatusResponse>(new GetOctopusImportStatusRequest { SpaceId = SpaceId, SessionId = sessionId }));

    private Task<List<OctopusImportResumableSessionDto>> ListAsync() => _fixture.Run<IMediator, List<OctopusImportResumableSessionDto>>(async mediator => (await mediator.RequestAsync<ListOctopusImportSessionsRequest, ListOctopusImportSessionsResponse>(new ListOctopusImportSessionsRequest { SpaceId = SpaceId })).Data);

    // Each test imports the same stub space into a destination of its own: the previous test's rows are removed first.
    private Task ResetDestinationAsync() => _fixture.Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DELETE FROM octopus_import_source_map; DELETE FROM project; DELETE FROM project_group;"));

    // Every project insert waits while the test holds the advisory lock: the import pauses inside its open transaction.
    private Task InstallProjectInsertGateAsync() => _fixture.Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync(
        "CREATE OR REPLACE FUNCTION e2e_project_insert_gate() RETURNS trigger LANGUAGE plpgsql AS $fn$ BEGIN PERFORM pg_advisory_lock_shared(" + ProjectInsertGate + "); PERFORM pg_advisory_unlock_shared(" + ProjectInsertGate + "); RETURN NEW; END $fn$; " +
        "DROP TRIGGER IF EXISTS e2e_project_insert_gate ON project; CREATE TRIGGER e2e_project_insert_gate BEFORE INSERT ON project FOR EACH ROW EXECUTE FUNCTION e2e_project_insert_gate();"));

    private Task DropProjectInsertGateAsync() => _fixture.Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS e2e_project_insert_gate ON project; DROP FUNCTION IF EXISTS e2e_project_insert_gate();"));

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host, and a job client that keeps what it queues.</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusImportConfirmProgressE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true, ChunkDuration = TimeSpan.Zero });
            builder.RegisterInstance(OpenAuthorization()).As<IAuthorizationService>();
            builder.RegisterInstance(new MockSquidBackgroundJobClient()).As<ISquidBackgroundJobClient>();
        }

        private static IAuthorizationService OpenAuthorization()
        {
            var authorization = new Moq.Mock<IAuthorizationService>();
            authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            return authorization.Object;
        }
    }
}
