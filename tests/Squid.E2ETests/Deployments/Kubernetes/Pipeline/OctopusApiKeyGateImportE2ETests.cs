using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the API-key gate (D11). A real-socket stub Octopus answers <c>users/{id}/permissions</c> in the
/// 2026.1 shape; the production REST client, permission evaluator, connect handler and session store run through the
/// mediator against the real database. Proves a key limited to some projects is refused at connect with the permissions
/// named and nothing created or read from the space, and that the same server then imports in full with a key that can
/// read the whole space. Its own class (own database) so the import cannot make another class's project "already imported".
/// </summary>
[Trait("Category", "E2E")]
public class OctopusApiKeyGateImportE2ETests : IClassFixture<OctopusApiKeyGateImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-KEY-GATE-FULL-1";
    private const string RestrictedApiKey = "API-E2E-KEY-GATE-LIMITED-1";

    private readonly ImportFixture _fixture;

    public OctopusApiKeyGateImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RestrictedKey_RefusedAtConnect_NothingCreated_ThenAWholeSpaceKeyImportsEverything()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).AcceptKey(RestrictedApiKey, StubOctopusServer.DefaultUserId).RestrictKey(RestrictedApiKey, "VariableView").Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");

        var refused = await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => _fixture.Run<IMediator, ConnectOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConnectAsync(mediator, 1, octopus.BaseUri, RestrictedApiKey)));

        refused.Permissions.Restricted.ShouldBe(["VariableView"], customMessage: "the refusal names the permission the key is limited on");
        (await SessionsAsync()).ShouldBe(0, customMessage: "a refused key leaves no session behind");
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.StartsWith("/api/Spaces-1/"), customMessage: "a refused key read nothing from the space");

        var import = await _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        import.Fetch.Counts.Deployments.ShouldBe(1);
        (await _fixture.Run<SquidDbContext, int>(db => db.Set<Project>().CountAsync())).ShouldBe(1);
    }

    private Task<int> SessionsAsync() => _fixture.Run<SquidDbContext, int>(db => db.Set<OctopusImportSession>().CountAsync());

    /// <summary>The pipeline fixture plus the HTTP context accessor, loopback allowed for the stub, and open Squid authorization.</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusApiKeyGateImportE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true });
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
