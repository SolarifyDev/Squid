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
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the origin check of a whole import. Ids like <c>Projects-1</c> repeat across Octopus servers: a space is
/// first imported from the export file of server Y, then a real-socket stub Octopus X that shares every id (but none of Y's process
/// GUIDs) is imported through the production REST client, fetcher, planner, validator and mediator. The preview refuses it, naming
/// both origins, and nothing is linked or written — X's history never lands on Y's projects. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusImportOriginE2ETests : IClassFixture<OctopusImportOriginE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-ORIGIN-KEY-1";
    private const string ExportPassword = "e2e-export-password";
    private const string FingerprintOfY = "0E1E2E3E00000000BBBBBBBB12345678";

    private readonly ImportFixture _fixture;

    public OctopusImportOriginE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FileOfServerY_ThenServerX_SharingEveryId_RefusedNothingLinkedOrWritten()
    {
        await EnsureSpaceAsync();
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();

        var first = await ImportFromFileAsync(ExportOfServerY());
        first.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var before = await RowCountsAsync();

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ImportFromServerAsync(octopus));

        refused.Message.ShouldContain(OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven);
        refused.Message.ShouldContain($"Octopus installation {StubOctopusServer.InstallationId}", Case.Insensitive);
        refused.Message.ShouldContain($"an export file with fingerprint {FingerprintOfY}", Case.Insensitive);
        (await RowCountsAsync()).ShouldBe(before, "nothing of server X is linked or written");
        (await OriginRowsAsync()).ShouldBe([$"{OctopusImportSourceMapQueries.ExportSourceSourceType}:{FingerprintOfY}"], "server X's installation is never recorded");
    }

    private Task<OctopusImportFlowResult> ImportFromServerAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey, confirmPairing: true));

    private Task<OctopusImportFlowResult> ImportFromFileAsync(string archive)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, 1, archive, ExportPassword, confirmPairing: true));

    // The space's own source-map rows (installation, export fingerprint) are live only while the space row exists.
    private Task EnsureSpaceAsync()
        => _fixture.Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == 1)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

    private Task<(int SourceMap, int Projects, int Environments, int Releases, int Deployments)> RowCountsAsync()
        => _fixture.Run<SquidDbContext, (int, int, int, int, int)>(async db => (await db.Set<OctopusImportSourceMap>().CountAsync(), await db.Set<Project>().CountAsync(), await db.Set<Squid.Core.Persistence.Entities.Deployments.Environment>().CountAsync(), await db.Set<Release>().CountAsync(), await db.Set<Deployment>().CountAsync()));

    private Task<List<string>> OriginRowsAsync()
        => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<OctopusImportSourceMap>().Where(m => m.SourceType == OctopusImportSourceMapQueries.InstallationSourceType || m.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType).Select(m => m.SourceType + ":" + m.SourceId).ToListAsync());

    // Server Y's export: the same ids as server X's API (Projects-1, Environments-1, ...), its own fingerprint, no process GUIDs of X.
    private static string ExportOfServerY()
    {
        var documents = new List<(string Id, string DocumentType, string Json)>
        {
            ("ProjectGroups-1", "ProjectGroup", """{"Id":"ProjectGroups-1","Name":"Server group","Slug":"server-group"}"""),
            ("Environments-1", "StaticDeploymentEnvironment", """{"Id":"Environments-1","Name":"Server TEST","Slug":"server-test"}"""),
            ("Lifecycles-1", "Lifecycle", """{"Id":"Lifecycles-1","Name":"Server lifecycle","Slug":"server-lifecycle","Phases":[{"Id":"Phases-1","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}"""),
            ("Projects-1", "Project", """{"Id":"Projects-1","Name":"Server project","Slug":"server-project","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
            ("variableset-Projects-1", "ProjectVariables", """{"Id":"variableset-Projects-1","OwnerId":"Projects-1","OwnerType":"Project","Variables":[]}"""),
            ("deploymentprocess-Projects-1", "DeploymentProcess", """{"Id":"deploymentprocess-Projects-1","OwnerId":"Projects-1","Steps":[]}"""),
            ("Channels-1", "Channel", """{"Id":"Channels-1","Name":"Default","Slug":"default","ProjectId":"Projects-1","IsDefault":true}""")
        };

        var path = Path.Combine(Path.GetTempPath(), $"squid-e2e-origin-{Guid.NewGuid():N}.zip");
        OctopusSecretFillImportE2ETests.WriteArchive(path, documents.Select(d => (d.Id, d.DocumentType, OctopusSecretFillImportE2ETests.WithSpaceId(d.Json, $"Spaces-1-{FingerprintOfY}"))).ToList());

        return path;
    }

    /// <summary>
    /// The pipeline fixture plus what the mediator-driven import needs in a test host: the HTTP context accessor that
    /// resolving the current user consults, loopback allowed for the stub, and open authorization.
    /// </summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusImportOriginE2ETests>
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
