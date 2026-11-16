using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the origin check of export files whose fingerprint the space does not know. A space is imported from the
/// export file of server Y; then, through the production upload, extraction, planner, validator and mediator: a file of server Z that
/// shares every id but none of Y's process GUIDs is refused (nothing linked or written, Z never recorded); a re-export of Y with a new
/// fingerprint asks for the pairing, is refused until it is confirmed, then links to Y's rows and is recorded as another fingerprint of
/// Y, so its next import is proven without asking. One sequential scenario in its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusImportExportFingerprintE2ETests : IClassFixture<OctopusImportExportFingerprintE2ETests.ImportFixture>
{
    private const string ExportPassword = "e2e-export-password";
    private const string FingerprintOfY = "0E1E2E3E00000000CCCCCCCC12345678";
    private const string FingerprintOfZ = "0E1E2E3E00000000DDDDDDDD12345678";
    private const string ReExportFingerprintOfY = "0E1E2E3E00000000EEEEEEEE12345678";

    private readonly ImportFixture _fixture;

    public OctopusImportExportFingerprintE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FileOfAnotherFingerprint_RefusedWithoutProcessIds_PairedOnceWithThem_ThenProven()
    {
        await EnsureSpaceAsync();
        (await ImportFromFileAsync(Export(FingerprintOfY, withProcess: true), confirmPairing: false)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var before = await RowCountsAsync();

        var otherServer = await PreviewFileAsync(Export(FingerprintOfZ, withProcess: false));

        otherServer.FillPairing.ShouldBeNull("no process GUID backs a pairing, so none is offered");
        otherServer.Origin.ShouldNotBeNull("the preview carries the refused verdict the page renders").ShouldSatisfyAllConditions(
            o => o.State.ShouldBe(OctopusImportFillEvidenceState.Unproven),
            o => o.Basis.ShouldBe("export-fingerprint-mismatch"),
            o => o.Source.ShouldContain(FingerprintOfZ, Case.Insensitive),
            o => o.Recorded.ShouldContain(FingerprintOfY, Case.Insensitive));
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ImportFromFileAsync(Export(FingerprintOfZ, withProcess: false), confirmPairing: true));
        refused.Message.ShouldContain(OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven);
        (await RowCountsAsync()).ShouldBe(before, "nothing of server Z is linked or written");
        (await OriginRowsAsync()).ShouldBe([FingerprintOfY], "server Z's fingerprint is never recorded");

        var reExport = await PreviewFileAsync(Export(ReExportFingerprintOfY, withProcess: true));

        reExport.HasBlockers.ShouldBeFalse();
        reExport.FillPairing.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Required.ShouldBeTrue(), p => p.FileExportSourceId.ShouldBe(ReExportFingerprintOfY), p => p.RecordedExportSourceId.ShouldBe(FingerprintOfY), p => p.MatchingStructureIds.ShouldBe(2));
        (reExport.Origin?.State, reExport.Origin?.Basis).ShouldBe((OctopusImportFillEvidenceState.PairingRequired, "export-fingerprint-mismatch"));
        await Should.ThrowAsync<InvalidOperationException>(() => ImportFromFileAsync(Export(ReExportFingerprintOfY, withProcess: true), confirmPairing: false));
        (await OriginRowsAsync()).ShouldBe([FingerprintOfY], "an unconfirmed pairing records nothing");

        (await ImportFromFileAsync(Export(ReExportFingerprintOfY, withProcess: true), confirmPairing: true)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        (await RowCountsAsync()).ShouldBe(before, "the re-export links to Y's rows, nothing duplicated");
        (await OriginRowsAsync()).ShouldBe([FingerprintOfY, ReExportFingerprintOfY], ignoreOrder: true);

        var proven = await ImportFromFileAsync(Export(ReExportFingerprintOfY, withProcess: true), confirmPairing: false);

        proven.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        proven.PreviewPlan.FillPairing.ShouldBeNull("the recorded fingerprint proves the re-export now");
        (proven.PreviewPlan.Origin?.State, proven.PreviewPlan.Origin?.Basis).ShouldBe((OctopusImportFillEvidenceState.Proven, "export-fingerprint"));
    }

    private Task<OctopusImportFlowResult> ImportFromFileAsync(string archive, bool confirmPairing)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, 1, archive, ExportPassword, confirmPairing));

    private Task<OctopusImportPreviewPlanDto> PreviewFileAsync(string archive)
        => _fixture.Run<IMediator, OctopusImportPreviewPlanDto>(async mediator =>
        {
            var sessionId = (await OctopusImportHandlerFlow.UploadAsync(mediator, 1, archive, ExportPassword)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.ExtractAsync(mediator, 1, sessionId);

            return (await OctopusImportHandlerFlow.PreviewAsync(mediator, 1, sessionId)).Data.PreviewPlan;
        });

    // The space's own source-map rows (installation, export fingerprint) are live only while the space row exists.
    private Task EnsureSpaceAsync()
        => _fixture.Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == 1)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

    private Task<(int Projects, int Environments, int Lifecycles, int Steps, int Actions)> RowCountsAsync()
        => _fixture.Run<SquidDbContext, (int, int, int, int, int)>(async db => (await db.Set<Project>().CountAsync(), await db.Set<Squid.Core.Persistence.Entities.Deployments.Environment>().CountAsync(), await db.Set<Lifecycle>().CountAsync(), await db.Set<DeploymentStep>().CountAsync(), await db.Set<DeploymentAction>().CountAsync()));

    private Task<List<string>> OriginRowsAsync()
        => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<OctopusImportSourceMap>().Where(m => m.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType).Select(m => m.SourceId).ToListAsync());

    // An export with the given fingerprint: the same ids on every server (Projects-1, Environments-1, ...); with the process, the
    // step and action GUIDs of server Y.
    private static string Export(string fingerprint, bool withProcess)
    {
        var documents = new List<(string Id, string DocumentType, string Json)>
        {
            ("ProjectGroups-1", "ProjectGroup", """{"Id":"ProjectGroups-1","Name":"Server group","Slug":"server-group"}"""),
            ("Environments-1", "StaticDeploymentEnvironment", """{"Id":"Environments-1","Name":"Server TEST","Slug":"server-test"}"""),
            ("Lifecycles-1", "Lifecycle", """{"Id":"Lifecycles-1","Name":"Server lifecycle","Slug":"server-lifecycle","Phases":[{"Id":"Phases-1","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}"""),
            ("Projects-1", "Project", """{"Id":"Projects-1","Name":"Server project","Slug":"server-project","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
            ("variableset-Projects-1", "ProjectVariables", """{"Id":"variableset-Projects-1","OwnerId":"Projects-1","OwnerType":"Project","Variables":[]}"""),
            withProcess ? OctopusWithheldSecretResources.ExportProcessDocument() : ("deploymentprocess-Projects-1", "DeploymentProcess", """{"Id":"deploymentprocess-Projects-1","OwnerId":"Projects-1","Steps":[]}"""),
            ("Channels-1", "Channel", """{"Id":"Channels-1","Name":"Default","Slug":"default","ProjectId":"Projects-1","IsDefault":true}""")
        };

        var path = Path.Combine(Path.GetTempPath(), $"squid-e2e-fingerprint-{Guid.NewGuid():N}.zip");
        OctopusSecretFillImportE2ETests.WriteArchive(path, documents.Select(d => (d.Id, d.DocumentType, OctopusSecretFillImportE2ETests.WithSpaceId(d.Json, $"Spaces-1-{fingerprint}"))).ToList());

        return path;
    }

    /// <summary>
    /// The pipeline fixture plus what the mediator-driven import needs in a test host: the HTTP context accessor that resolving the
    /// current user consults, and open authorization.
    /// </summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusImportExportFingerprintE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
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
