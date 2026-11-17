using System.Net;
using System.Text.Json;
using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Squid.Api.Filters;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Requests.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the import error kinds. Real failures (a real-socket stub Octopus, the production REST client,
/// permission gate, fetcher, session store and real database through the mediator) go through the production
/// <see cref="GlobalExceptionFilter"/> and are serialized with the API's web JSON defaults, exactly the body the import page
/// reads: a restricted key is refused at connect as <c>KeyRestricted</c>; a fetch failing on an unreachable server answers
/// <c>Unreachable</c>, is resumed on the same session and imports everything; the finished session then answers
/// <c>SessionStateConflict</c> (not resumable); a session past its maximum age answers <c>SessionExpired</c> before the sweep closes
/// it with the reason. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusImportErrorKindE2ETests : IClassFixture<OctopusImportErrorKindE2ETests.ImportFixture>
{
    private const int SpaceId = 1;
    private const string ApiKey = "API-E2E-ERROR-KIND-FULL-1";
    private const string RestrictedApiKey = "API-E2E-ERROR-KIND-LIMITED-1";

    private readonly ImportFixture _fixture;

    public OctopusImportErrorKindE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RestrictedKey_ThenUnreachableServerResumed_ThenFinishedSession_EachAnswersItsErrorKind()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).AcceptKey(RestrictedApiKey, StubOctopusServer.DefaultUserId).RestrictKey(RestrictedApiKey, "DeploymentView").Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");

        var restricted = await EnvelopeOfAsync(() => _fixture.Run<IMediator, ConnectOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, RestrictedApiKey)));

        restricted.ShouldBe((HttpStatusCode.BadRequest, "KeyRestricted"), customMessage: "a key limited to some projects is refused at connect with kind KeyRestricted");

        var sessionId = await _fixture.Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId);
        octopus.FailNext("/projects");

        var unreachable = await EnvelopeOfAsync(() => _fixture.Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

        unreachable.ShouldBe((HttpStatusCode.BadRequest, "Unreachable"), customMessage: "a server that kept failing is reported as Unreachable, which the page resumes");

        var import = await _fixture.Run<IMediator, OctopusImportFlowResult>(async mediator => await OctopusImportHandlerFlow.FinishServerImportAsync(mediator, SpaceId, sessionId, await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: "the resumed session imports everything");
        import.Fetch.Counts.Deployments.ShouldBe(1);

        var finished = await EnvelopeOfAsync(() => _fixture.Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

        finished.ShouldBe((HttpStatusCode.Conflict, "SessionStateConflict"), customMessage: "a finished session cannot be resumed; the page must start a new one");
    }

    // A session past its maximum age is refused with its own kind before the sweep reaches it, writes nothing, and the next sweep closes
    // it with the reason the page shows.
    [Fact]
    public async Task ServerFetch_SessionPastMaxAge_ChunkRefusedWithSessionExpiredKind()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");
        var sessionId = await _fixture.Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId);
        var createdDate = DateTimeOffset.UtcNow.AddDays(-8);
        await _fixture.Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedDate, createdDate)));
        var versionBefore = await DataVersionAsync(sessionId);

        var refused = await EnvelopeOfAsync(() => _fixture.Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

        refused.ShouldBe((HttpStatusCode.Conflict, "SessionExpired"), customMessage: "a session past its maximum age is not resumable; the page must start a new one");
        (await DataVersionAsync(sessionId)).ShouldBe(versionBefore, "a refused chunk writes nothing to the session");

        var sweep = await _fixture.Run<IMediator, EnforceOctopusImportTemporaryUploadCleanupResponse>(mediator => mediator.SendAsync<EnforceOctopusImportTemporaryUploadCleanupCommand, EnforceOctopusImportTemporaryUploadCleanupResponse>(new EnforceOctopusImportTemporaryUploadCleanupCommand()));
        var session = await _fixture.Run<IMediator, GetOctopusImportStatusResponse>(mediator => mediator.RequestAsync<GetOctopusImportStatusRequest, GetOctopusImportStatusResponse>(new GetOctopusImportStatusRequest { SpaceId = SpaceId, SessionId = sessionId }));

        sweep.Data.OverAgeSessions.ShouldBeGreaterThanOrEqualTo(1);
        session.Data.Session.State.ShouldBe(OctopusImportSessionState.Expired);
        session.Data.Session.Result.Diagnostics.ShouldContain(d => d.Code == "octopus.session.max_age_reached");
    }

    private Task<byte[]> DataVersionAsync(Guid sessionId) => _fixture.Run<SquidDbContext, byte[]>(async db => (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).DataVersion);

    // The body the API returns for the failure: the production exception filter, serialized as ASP.NET Core does.
    private static async Task<(HttpStatusCode Code, string ErrorKind)> EnvelopeOfAsync(Func<Task> call)
    {
        var exception = await Should.ThrowAsync<Exception>(call);
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary()), []) { Exception = exception };

        new GlobalExceptionFilter().OnException(context);

        var json = JsonSerializer.Serialize(((OkObjectResult)context.Result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var body = JsonDocument.Parse(json);

        return ((HttpStatusCode)body.RootElement.GetProperty("code").GetInt32(), body.RootElement.GetProperty("errorKind").GetString());
    }

    /// <summary>The pipeline fixture plus the HTTP context accessor, loopback allowed for the stub, a one-page chunk budget and open Squid authorization.</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusImportErrorKindE2ETests>
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
