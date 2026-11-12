using System.Linq.Expressions;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier (REAL Postgres, REAL container, REAL mediator pipeline) for a confirmation queued with
/// <c>RunInBackground</c>: the request only queues it, and the captured job — run in a fresh scope, as Hangfire
/// would — imports as the session owner and ends the session, whether it succeeds or is refused.
/// </summary>
public class OctopusImportBackgroundConfirmationIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string ExportPassword = "fake-export-password-for-tests";

    public OctopusImportBackgroundConfirmationIntegrationTests()
        : base("OctopusImportBackgroundConfirmation", "squid_it_octopus_import_background_confirmation")
    {
    }

    [Fact]
    public async Task BackgroundConfirm_CapturedJob_RunsToSucceeded()
    {
        var projectName = $"Background-{Guid.NewGuid():N}";
        var archivePath = OctopusExportSecretsArchive.Write(projectName);

        try
        {
            var sessionId = await ValidateAsync(archivePath);
            var job = await ConfirmInBackgroundAsync(sessionId);

            (await ProjectExistsAsync(projectName)).ShouldBeFalse("queueing must not import anything");

            await Run<IOctopusImportConfirmationJob>(runner => job.Compile()(runner), builder => builder.RegisterInstance(AllowAllAuthorization()).As<IAuthorizationService>());

            var stored = await StoredSessionAsync(sessionId);
            stored.State.ShouldBe(OctopusImportSessionState.Succeeded.ToString(), customMessage: stored.ResultJson);
            (await ProjectExistsAsync(projectName)).ShouldBeTrue();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task BackgroundConfirm_OwnerLacksPermission_EndsTheSessionFailedWithTheReason_ImportsNothing()
    {
        var projectName = $"BackgroundDenied-{Guid.NewGuid():N}";
        var archivePath = OctopusExportSecretsArchive.Write(projectName);

        try
        {
            var sessionId = await ValidateAsync(archivePath);
            var job = await ConfirmInBackgroundAsync(sessionId);

            // Real authorization: the integration owner has no team roles, and the job must not run as the
            // internal user (which would bypass every permission check).
            await Run<IOctopusImportConfirmationJob>(runner => job.Compile()(runner));

            var stored = await StoredSessionAsync(sessionId);
            stored.State.ShouldBe(OctopusImportSessionState.Failed.ToString());
            stored.ResultJson.ShouldContain(OctopusImportConfirmationDiagnosticCodes.BackgroundConfirmationFailed);
            (await ProjectExistsAsync(projectName)).ShouldBeFalse();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private async Task<Guid> ValidateAsync(string archivePath)
    {
        var sessionId = Guid.Empty;

        await Run<IMediator>(async mediator =>
        {
            var upload = await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archivePath, ExportPassword);
            upload.Code.ShouldBe(HttpStatusCode.OK, customMessage: upload.Msg);
            sessionId = upload.Data.Session.SessionId;
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

            var preview = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
            preview.Data.PreviewPlan.HasBlockers.ShouldBeFalse(OctopusImportHandlerFlow.DescribeBlockers(OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan)));

            var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview.Data.PreviewPlan);
            validate.Code.ShouldBe(HttpStatusCode.OK, customMessage: validate.Msg);
        });

        return sessionId;
    }

    // The mediator and the job client share one scope, so the job the handler queued is the one captured here.
    private async Task<Expression<Func<IOctopusImportConfirmationJob, Task>>> ConfirmInBackgroundAsync(Guid sessionId)
    {
        Expression<Func<IOctopusImportConfirmationJob, Task>> job = null;

        await Run<IMediator, ISquidBackgroundJobClient>(async (mediator, jobClient) =>
        {
            var confirm = await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId, runInBackground: true);

            confirm.Code.ShouldBe(HttpStatusCode.OK, customMessage: confirm.Msg);
            confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
            job = ((MockSquidBackgroundJobClient)jobClient).EnqueuedJobs.OfType<Expression<Func<IOctopusImportConfirmationJob, Task>>>().ShouldHaveSingleItem();
        });

        return job;
    }

    private Task<bool> ProjectExistsAsync(string projectName) => Run<SquidDbContext, bool>(db => db.Set<Project>().AnyAsync(p => p.Name == projectName));

    private Task<OctopusImportSession> StoredSessionAsync(Guid sessionId) => Run<SquidDbContext, OctopusImportSession>(db => db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId));

    private static IAuthorizationService AllowAllAuthorization()
    {
        var authorization = new Moq.Mock<IAuthorizationService>();
        authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return authorization.Object;
    }
}
