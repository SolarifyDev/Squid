using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public sealed class OctopusHistoryBundleWriterTests : IDisposable
{
    private readonly string _sessionDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"squid-bundle-writer-{Guid.NewGuid():N}")).FullName;

    public static TheoryData<OctopusResourceKind, string, OctopusDocumentKind> StageableKinds => new()
    {
        { OctopusResourceKind.ProjectGroup, "ProjectGroups-1", OctopusDocumentKind.ProjectGroup },
        { OctopusResourceKind.Environment, "Environments-1", OctopusDocumentKind.Environment },
        { OctopusResourceKind.Lifecycle, "Lifecycles-1", OctopusDocumentKind.Lifecycle },
        { OctopusResourceKind.Feed, "feeds-docker-hub", OctopusDocumentKind.Feed },
        { OctopusResourceKind.Account, "Accounts-1", OctopusDocumentKind.Account },
        { OctopusResourceKind.Team, "teams-everyone", OctopusDocumentKind.Team },
        { OctopusResourceKind.LibraryVariableSet, "LibraryVariableSets-1", OctopusDocumentKind.LibraryVariableSet },
        { OctopusResourceKind.VariableSet, "variableset-Projects-1", OctopusDocumentKind.VariableSet },
        { OctopusResourceKind.VariableSetSnapshot, "variableset-Projects-1-s-2-ABCDE", OctopusDocumentKind.VariableSetSnapshot },
        { OctopusResourceKind.ActionTemplate, "ActionTemplates-1", OctopusDocumentKind.ActionTemplate },
        { OctopusResourceKind.Machine, "Machines-1", OctopusDocumentKind.Machine },
        { OctopusResourceKind.Certificate, "Certificates-1", OctopusDocumentKind.Certificate },
        { OctopusResourceKind.WorkerPool, "WorkerPools-1", OctopusDocumentKind.WorkerPool },
        { OctopusResourceKind.Tenant, "Tenants-1", OctopusDocumentKind.Tenant },
        { OctopusResourceKind.Project, "Projects-1", OctopusDocumentKind.Project },
        { OctopusResourceKind.Channel, "Channels-1", OctopusDocumentKind.Channel },
        { OctopusResourceKind.DeploymentProcess, "deploymentprocess-Projects-1", OctopusDocumentKind.DeploymentProcess },
        { OctopusResourceKind.DeploymentProcessSnapshot, "deploymentprocess-Projects-1-s-4-ABCDE", OctopusDocumentKind.DeploymentProcessSnapshot },
        { OctopusResourceKind.DeploymentSettings, "deploymentsettings-Projects-1", OctopusDocumentKind.DeploymentSettings },
        { OctopusResourceKind.Runbook, "Runbooks-1", OctopusDocumentKind.Runbook },
        { OctopusResourceKind.Trigger, "ProjectTriggers-1", OctopusDocumentKind.Trigger },
        { OctopusResourceKind.Release, "Releases-1", OctopusDocumentKind.Release },
        { OctopusResourceKind.Deployment, "Deployments-1", OctopusDocumentKind.Deployment },
        { OctopusResourceKind.ServerTask, "ServerTasks-1", OctopusDocumentKind.ServerTask }
    };

    // The manifest type and the document id must agree on the kind, or the inventory warns and the graph mis-files it.
    [Theory]
    [MemberData(nameof(StageableKinds))]
    public async Task EveryDocumentKind_StagedAndClassifiedTheSameByManifestAndDocument(OctopusResourceKind kind, string id, OctopusDocumentKind expected)
    {
        var writer = new OctopusHistoryBundleWriter(new OctopusImportSizeLimits());

        await writer.WriteJsonAsync(_sessionDirectory, kind, new JsonObject { ["Id"] = id, ["Name"] = "n" });
        var bundle = await writer.BuildBundleAsync(_sessionDirectory, "inst-1");

        var inventory = await InventoryAsync(bundle, new OctopusImportSizeLimits());
        var item = inventory.Items.Single();

        inventory.Diagnostics.ShouldBeEmpty();
        item.Classification.Kind.ShouldBe(expected);
        item.Document.Classification.Kind.ShouldBe(expected);
    }

    [Fact]
    public void EveryStageableKind_IsCovered()
        => OctopusHistoryBundleWriter.ManifestDocumentTypes.Keys.Order().ShouldBe(StageableKinds.Select(row => (OctopusResourceKind)row[0]).Order());

    // Children travel inside their parent document; staging one alone would produce a document no classifier maps.
    [Theory]
    [InlineData(OctopusResourceKind.Unknown)]
    [InlineData(OctopusResourceKind.LifecyclePhase)]
    [InlineData(OctopusResourceKind.DeploymentStep)]
    [InlineData(OctopusResourceKind.DeploymentAction)]
    [InlineData(OctopusResourceKind.Variable)]
    public async Task ChildKinds_Rejected(OctopusResourceKind kind)
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new OctopusHistoryBundleWriter().WriteJsonAsync(_sessionDirectory, kind, new JsonObject { ["Id"] = "x-1" }));

    [Fact]
    public async Task DocumentWithoutId_Rejected()
        => await Should.ThrowAsync<ArgumentException>(() => new OctopusHistoryBundleWriter().WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Project, new JsonObject { ["Name"] = "x" }));

    [Theory]
    [InlineData(3, 1000, 1000, 4, "documents")]
    [InlineData(100, 200, 1000, 4, OctopusImportSizeLimits.MaxBytesEnvVar)]
    [InlineData(100, 1000, 64, 1, "one document")]
    public async Task CrossingALimit_FailsAtThatDocument_NamingTheLimit(int maxDocuments, long maxBytes, long maxDocumentBytes, int failingDocument, string limitName)
    {
        const int padding = 10; // each document is 65 bytes
        var writer = new OctopusHistoryBundleWriter(new OctopusImportSizeLimits { MaxBytes = maxBytes, MaxDocumentCount = maxDocuments, MaxDocumentBytes = maxDocumentBytes });
        var staged = 0;

        var ex = await Should.ThrowAsync<OctopusRestException>(async () =>
        {
            for (var n = 1; n <= 10; n++, staged++) await writer.WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, Release(n, padding));
        });

        ex.Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
        ex.LimitName.ShouldContain(limitName);
        ex.Message.ShouldContain(limitName);
        staged.ShouldBe(failingDocument - 1);
        StagedFileCount().ShouldBe(failingDocument - 1);
    }

    // A page read twice overwrites; it must not count twice against the document or byte limits.
    [Fact]
    public async Task Overwrite_ReplacesTheCountedBytes_NotAddsToThem()
    {
        var limits = new OctopusImportSizeLimits { MaxDocumentCount = 1, MaxBytes = 100 };
        var writer = new OctopusHistoryBundleWriter(limits);

        for (var attempt = 0; attempt < 5; attempt++) await writer.WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, Release(1, 40));

        StagedFileCount().ShouldBe(1);
        await Should.ThrowAsync<OctopusRestException>(() => writer.WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, Release(2, 1)));
    }

    // Every chunk runs with a new writer; the count must carry over, from the totals file or, without one, from disk.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextChunk_ContinuesTheCount(bool totalsFileLost)
    {
        var limits = new OctopusImportSizeLimits { MaxDocumentCount = 3 };

        for (var n = 1; n <= 3; n++) await new OctopusHistoryBundleWriter(limits).WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, Release(n, 1));

        if (totalsFileLost) File.Delete(Path.Combine(_sessionDirectory, OctopusHistoryBundleWriter.StagingDirectoryName, OctopusHistoryBundleWriter.TotalsFileName));

        var ex = await Should.ThrowAsync<OctopusRestException>(() => new OctopusHistoryBundleWriter(limits).WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, Release(4, 1)));

        ex.Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
    }

    // A fetch that ran to the limit must still be readable: the extractor gets the same limits plus room for the manifest.
    [Fact]
    public async Task BundleStagedUpToTheLimits_ReadBackWithTheSameLimits()
    {
        var limits = new OctopusImportSizeLimits { MaxDocumentCount = 4, MaxBytes = 4 * 300, MaxDocumentBytes = 300 };
        var writer = new OctopusHistoryBundleWriter(limits);

        for (var n = 1; n <= 4; n++) await writer.WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, Release(n, 300 - Release(n, 0).ToJsonString().Length));
        var bundle = await writer.BuildBundleAsync(_sessionDirectory, "inst-1");

        var inventory = await InventoryAsync(bundle, limits);

        inventory.Items.Count.ShouldBe(4);
        inventory.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ManifestOverItsLimit_TooLarge()
    {
        var writer = new OctopusHistoryBundleWriter(new OctopusImportSizeLimits { MaxDocumentCount = 1 });

        await writer.WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Project, new JsonObject { ["Id"] = "Projects-1", ["Name"] = new string('n', 2000) });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => writer.BuildBundleAsync(_sessionDirectory, "inst-1"));

        ex.LimitName.ShouldContain("manifest");
    }

    // Resuming a fetch must not change the bundle: same staged documents → byte-identical archive.
    [Fact]
    public async Task SameStagedDocuments_ByteIdenticalBundle()
    {
        var writer = new OctopusHistoryBundleWriter();

        foreach (var n in Enumerable.Range(1, 3)) await writer.WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, Release(n, 5));

        var first = SHA256.HashData(await File.ReadAllBytesAsync(await writer.BuildBundleAsync(_sessionDirectory, "inst-1")));
        await Task.Delay(1100);
        var second = SHA256.HashData(await File.ReadAllBytesAsync(await new OctopusHistoryBundleWriter().BuildBundleAsync(_sessionDirectory, "inst-1")));

        second.ShouldBe(first);
    }

    [Fact]
    public async Task TypedAndJsonWrites_ShareOneStagingSlot()
    {
        var writer = new OctopusHistoryBundleWriter();

        await writer.WriteDocumentAsync(_sessionDirectory, OctopusResourceKind.Release, new OctopusReleaseDto { Id = "Releases-1", Version = "1.0.0", ReleaseNotes = "typed" });
        await writer.WriteJsonAsync(_sessionDirectory, OctopusResourceKind.Release, new JsonObject { ["Id"] = "Releases-1", ["Version"] = "1.0.0", ["ReleaseNotes"] = "json" });

        using var zip = ZipFile.OpenRead(await writer.BuildBundleAsync(_sessionDirectory, "inst-1"));

        zip.Entries.Count.ShouldBe(2);
    }

    public void Dispose() => Directory.Delete(_sessionDirectory, recursive: true);

    private static JsonObject Release(int n, int padding) => new() { ["Id"] = $"Releases-{n}", ["Version"] = $"1.0.{n}", ["ReleaseNotes"] = new string('x', Math.Max(padding, 0)) };

    private int StagedFileCount()
    {
        var staging = Path.Combine(_sessionDirectory, OctopusHistoryBundleWriter.StagingDirectoryName);

        return Directory.Exists(staging) ? Directory.EnumerateFiles(staging, "*.json", SearchOption.AllDirectories).Count() : 0;
    }

    private static async Task<OctopusManifestInventoryResult> InventoryAsync(string bundle, OctopusImportSizeLimits limits)
    {
        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(), new OctopusInputExtractor(), limits).ReadAsync(bundle);

        return new OctopusManifestInventoryBuilder().Build(extraction);
    }
}
