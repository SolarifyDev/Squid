using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Core.Services.Security;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for filling secrets the Octopus API withheld (D7, D8, D10). A real-socket stub Octopus serves a space whose
/// secrets the API never returns; the production REST client, fetcher, normaliser, planner, validator and confirmation import it
/// through the mediator into the real database (values empty, rows <c>Missing</c>). Then the export file of the same installation
/// is imported: the first pairing is refused until the user confirms it, then every still-empty value is filled through the
/// services' compare-and-set and reaches the variable snapshot a release takes; repeated imports from either source write nothing,
/// and a value a user sets afterwards is never overwritten. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusSecretFillImportE2ETests : IClassFixture<OctopusSecretFillImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-SECRET-FILL-KEY-1";
    private const string ExportPassword = "e2e-export-password";
    private const string Fingerprint = "5E5E5E5E00000000AAAAAAAA12345678";

    private readonly ImportFixture _fixture;

    public OctopusSecretFillImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApiThenFile_PairingConfirmed_FillsOnlyEmptyValues_SnapshotCarriesThem_RepeatsAndUserEditsNeverOverwritten()
    {
        await EnsureSpaceAsync();
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();
        var archive = SameInstallationExport();

        (await ImportFromServerAsync(octopus)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        (await SecretRowsAsync()).ShouldBe(["FeedSecret:Missing", "VariableSecret:Missing", "VariableSecret:Missing"], ignoreOrder: true);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ImportFromFileAsync(archive, confirmPairing: false));
        refused.Message.ShouldContain(OctopusImportPreviewDiagnosticCodes.SecretFillPairingUnconfirmed, customMessage: "a first pairing with an export file is never assumed");
        (await DecryptedAsync("Db.Password")).ShouldBeNullOrEmpty();

        var fill = await ImportFromFileAsync(archive, confirmPairing: true);

        fill.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(fill));
        (await DecryptedAsync("Db.Password")).ShouldBe(OctopusWithheldSecretResources.DbPassword);
        (await DecryptedAsync("Smtp.Password")).ShouldBe(OctopusWithheldSecretResources.SmtpPassword);
        (await FeedCredentialsAsync()).ShouldBe(("robot", OctopusWithheldSecretResources.RegistryPassword));
        (await SecretRowsAsync()).ShouldBe(["FeedSecret:Filled", "VariableSecret:Filled", "VariableSecret:Filled"], ignoreOrder: true);
        (await SnapshotValueAsync("Db.Password")).ShouldBe(OctopusWithheldSecretResources.DbPassword, customMessage: "a release created after the fill snapshots the filled value");

        var settled = await RowCountsAsync();

        (await ImportFromFileAsync(archive, confirmPairing: false)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: "the recorded fingerprint proves the same file now");
        (await ImportFromServerAsync(octopus)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        (await RowCountsAsync()).ShouldBe(settled);

        await SetDbPasswordInSquidAsync("rotated-in-squid");
        (await ImportFromFileAsync(archive, confirmPairing: false)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        (await ImportFromServerAsync(octopus)).Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);

        (await DecryptedAsync("Db.Password")).ShouldBe("rotated-in-squid", customMessage: "a value set in Squid is never overwritten by any later import");
    }

    private Task<OctopusImportFlowResult> ImportFromServerAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

    private Task<OctopusImportFlowResult> ImportFromFileAsync(string archive, bool confirmPairing)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, 1, archive, ExportPassword, confirmPairing));

    // ── Squid state ──────────────────────────────────────────────────────

    // The space's own source-map rows (installation, export fingerprint) are live only while the space row exists.
    private Task EnsureSpaceAsync()
        => _fixture.Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == 1)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

    private async Task<string> DecryptedAsync(string name)
    {
        var variable = await _fixture.Run<SquidDbContext, Variable>(db => db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == name));

        return string.IsNullOrEmpty(variable.Value) ? variable.Value : await _fixture.Run<IVariableEncryptionService, string>(e => e.DecryptAsync(variable.Value, variable.VariableSetId));
    }

    // What a release would carry: the real snapshot service over the variable set the value lives in.
    private async Task<string> SnapshotValueAsync(string name)
    {
        var variable = await _fixture.Run<SquidDbContext, Variable>(db => db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == name));
        var snapshot = await _fixture.Run<IDeploymentSnapshotService, Squid.Message.Models.Deployments.Snapshots.VariableSetSnapshotDto>(s => s.SnapshotVariableSetFromIdsAsync([variable.VariableSetId]));
        var value = snapshot.Data.Variables.Single(v => v.Name == name).Value;

        return await _fixture.Run<IVariableEncryptionService, string>(e => e.DecryptAsync(value, variable.VariableSetId));
    }

    private async Task<(string Username, string Password)> FeedCredentialsAsync()
    {
        var feed = await _fixture.Run<SquidDbContext, ExternalFeed>(db => db.Set<ExternalFeed>().AsNoTracking().SingleAsync(f => f.Name == "Private registry"));

        return (feed.Username, await _fixture.Run<IAtRestSecretProtector, string>(p => p.UnprotectAsync(feed.Password, 0)));
    }

    private Task<List<string>> SecretRowsAsync()
        => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<OctopusImportSourceMap>().Where(m => OctopusImportSourceMapQueries.SecretSourceTypes.Contains(m.SourceType)).Select(m => m.SourceType + ":" + m.OutcomeState).ToListAsync());

    private Task<(int SourceMap, int Variables, int VariableSetVersions, int Feeds)> RowCountsAsync()
        => _fixture.Run<SquidDbContext, (int, int, int, int)>(async db => (await db.Set<OctopusImportSourceMap>().CountAsync(), await db.Set<Variable>().CountAsync(), await db.Set<VariableSet>().SumAsync(s => s.Version), await db.Set<ExternalFeed>().CountAsync()));

    private Task SetDbPasswordInSquidAsync(string value)
        => _fixture.Run<SquidDbContext, IVariableEncryptionService>(async (db, encryption) =>
        {
            var variable = await db.Set<Variable>().SingleAsync(v => v.Name == "Db.Password");
            variable.Value = encryption.EncryptAsync(value, variable.VariableSetId);
            await db.SaveChangesAsync();
        });

    // ── The export file of the stub's installation ───────────────────────

    // A real export: no manifest installation id, its fingerprint on every SpaceId, the secrets encrypted with the export password,
    // and the very process step/action GUIDs the API serves.
    internal static string SameInstallationExport()
    {
        var documents = new List<(string Id, string DocumentType, string Json)>
        {
            ("ProjectGroups-1", "ProjectGroup", """{"Id":"ProjectGroups-1","Name":"Server group","Slug":"server-group"}"""),
            ("Environments-1", "StaticDeploymentEnvironment", """{"Id":"Environments-1","Name":"Server TEST","Slug":"server-test"}"""),
            ("Lifecycles-1", "Lifecycle", """{"Id":"Lifecycles-1","Name":"Server lifecycle","Slug":"server-lifecycle","Phases":[{"Id":"Phases-1","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}"""),
            ("Projects-1", "Project", """{"Id":"Projects-1","Name":"Server project","Slug":"server-project","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
            ("Channels-1", "Channel", """{"Id":"Channels-1","Name":"Default","Slug":"default","ProjectId":"Projects-1","IsDefault":true}"""),
            OctopusWithheldSecretResources.ExportProcessDocument()
        };
        documents.AddRange(OctopusWithheldSecretResources.ExportSecretDocuments(ExportPassword));

        var path = Path.Combine(Path.GetTempPath(), $"squid-e2e-fill-{Guid.NewGuid():N}.zip");
        WriteArchive(path, documents.Select(d => (d.Id, d.DocumentType, WithSpaceId(d.Json, $"Spaces-1-{Fingerprint}"))).ToList());

        return path;
    }

    private static string WithSpaceId(string json, string spaceId)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node["SpaceId"] = spaceId;

        return node.ToJsonString();
    }

    private static void WriteArchive(string path, IReadOnlyList<(string Id, string DocumentType, string Json)> documents)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", DocumentSource = $"{d.Id}.json", ParentId = (string)null, Hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.Json))).ToLowerInvariant() })
        });

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "manifest.json", manifest);

        foreach (var document in documents)
            Add(zip, $"{document.Id}.json", Encoding.UTF8.GetBytes(document.Json));
    }

    private static void Add(ZipArchive zip, string entryPath, byte[] content)
    {
        using var stream = zip.CreateEntry(entryPath).Open();
        stream.Write(content);
    }

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>
    /// The pipeline fixture plus what the mediator-driven import needs in a test host: the HTTP context accessor that
    /// resolving the current user consults, loopback allowed for the stub, and open authorization.
    /// </summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusSecretFillImportE2ETests>
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
