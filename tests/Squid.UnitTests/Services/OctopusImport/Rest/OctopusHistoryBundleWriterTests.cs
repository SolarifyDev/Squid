using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.UnitTests.Services.OctopusImport.SessionFiles;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public sealed class OctopusHistoryBundleWriterTests : IDisposable
{
    private readonly string _sessionDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"squid-bundle-writer-{Guid.NewGuid():N}")).FullName;
    private readonly InMemoryOctopusImportSessionFileStore _files = new();
    private readonly Guid _sessionId = Guid.NewGuid();

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
        var writer = new OctopusHistoryBundleWriter(_files, new OctopusImportSizeLimits());

        await writer.WriteJsonAsync(_sessionId, kind, new JsonObject { ["Id"] = id, ["Name"] = "n" });
        var bundle = await writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1");

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
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new OctopusHistoryBundleWriter(_files).WriteJsonAsync(_sessionId, kind, new JsonObject { ["Id"] = "x-1" }));

    [Fact]
    public async Task DocumentWithoutId_Rejected()
        => await Should.ThrowAsync<ArgumentException>(() => new OctopusHistoryBundleWriter(_files).WriteJsonAsync(_sessionId, OctopusResourceKind.Project, new JsonObject { ["Name"] = "x" }));

    [Theory]
    [InlineData(3, 1000, 1000, 4, "documents")]
    [InlineData(100, 200, 1000, 4, OctopusImportSizeLimits.MaxBytesEnvVar)]
    [InlineData(100, 1000, 64, 1, "one document")]
    public async Task CrossingALimit_FailsAtThatDocument_NamingTheLimit(int maxDocuments, long maxBytes, long maxDocumentBytes, int failingDocument, string limitName)
    {
        const int padding = 10; // each document is 65 bytes
        var writer = new OctopusHistoryBundleWriter(_files, new OctopusImportSizeLimits { MaxBytes = maxBytes, MaxDocumentCount = maxDocuments, MaxDocumentBytes = maxDocumentBytes });
        var staged = 0;

        var ex = await Should.ThrowAsync<OctopusRestException>(async () =>
        {
            for (var n = 1; n <= 10; n++, staged++) await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(n, padding));
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
        var writer = new OctopusHistoryBundleWriter(_files, limits);

        for (var attempt = 0; attempt < 5; attempt++) await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(1, 40));

        StagedFileCount().ShouldBe(1);
        await Should.ThrowAsync<OctopusRestException>(() => writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(2, 1)));
    }

    // Every chunk runs with a new writer (on any server); the count carries over from what the session has stored.
    [Fact]
    public async Task NextChunk_ContinuesTheCount()
    {
        var limits = new OctopusImportSizeLimits { MaxDocumentCount = 3 };

        for (var n = 1; n <= 3; n++) await new OctopusHistoryBundleWriter(_files, limits).WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(n, 1));

        var ex = await Should.ThrowAsync<OctopusRestException>(() => new OctopusHistoryBundleWriter(_files, limits).WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(4, 1)));

        ex.Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
    }

    // A fetch that ran to the limit must still be readable: the extractor gets the same limits plus room for the manifest.
    [Fact]
    public async Task BundleStagedUpToTheLimits_ReadBackWithTheSameLimits()
    {
        var limits = new OctopusImportSizeLimits { MaxDocumentCount = 4, MaxBytes = 4 * 300, MaxDocumentBytes = 300 };
        var writer = new OctopusHistoryBundleWriter(_files, limits);

        for (var n = 1; n <= 4; n++) await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(n, 300 - Release(n, 0).ToJsonString().Length));
        var bundle = await writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1");

        var inventory = await InventoryAsync(bundle, limits);

        inventory.Items.Count.ShouldBe(4);
        inventory.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ManifestOverItsLimit_TooLarge()
    {
        var writer = new OctopusHistoryBundleWriter(_files, new OctopusImportSizeLimits { MaxDocumentCount = 1 });

        await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Project, new JsonObject { ["Id"] = "Projects-1", ["Name"] = new string('n', 2000) });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1"));

        ex.LimitName.ShouldContain("manifest");
    }

    // Resuming a fetch must not change the bundle: same staged documents → byte-identical archive.
    // A bundle may stage hundreds of thousands of documents: they are counted by the store, never listed into memory to be counted here.
    [Fact]
    public async Task CountStaged_CountedByTheStorePerKind_NoHeadersListed()
    {
        var store = new Mock<IOctopusImportSessionFileStore>();
        store.Setup(s => s.CountByFolderAsync(_sessionId, OctopusImportSessionFilePaths.StagingPrefix, It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, int> { ["Release"] = 2, ["Project"] = 1, ["NotAKind"] = 9 });

        var staged = await new OctopusHistoryBundleWriter(store.Object).CountStagedAsync(_sessionId);

        (staged[OctopusResourceKind.Release], staged[OctopusResourceKind.Project], staged[OctopusResourceKind.Environment]).ShouldBe((2, 1, 0));
        store.Verify(s => s.ListAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SameStagedDocuments_ByteIdenticalBundle()
    {
        var writer = new OctopusHistoryBundleWriter(_files);

        foreach (var n in Enumerable.Range(1, 3)) await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(n, 5));

        var first = SHA256.HashData(await File.ReadAllBytesAsync(await writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1")));
        await Task.Delay(1100);
        var second = SHA256.HashData(await File.ReadAllBytesAsync(await new OctopusHistoryBundleWriter(_files).BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1")));

        second.ShouldBe(first);
    }

    [Fact]
    public async Task TypedAndJsonWrites_ShareOneStagingSlot()
    {
        var writer = new OctopusHistoryBundleWriter(_files);

        await writer.WriteDocumentAsync(_sessionId, OctopusResourceKind.Release, new OctopusReleaseDto { Id = "Releases-1", Version = "1.0.0", ReleaseNotes = "typed" });
        await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, new JsonObject { ["Id"] = "Releases-1", ["Version"] = "1.0.0", ["ReleaseNotes"] = "json" });

        using var zip = ZipFile.OpenRead(await writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1"));

        zip.Entries.Count.ShouldBe(2);
    }

    // Same staged documents in the same order as a directory listing gave before they moved to the database: each kind in the manifest's
    // kind order, each kind's documents in ordinal path order. Pinned, so an import of an unchanged fetch never sees a different archive.
    [Fact]
    public async Task BundleEntries_KindOrderThenOrdinalPath()
    {
        var writer = new OctopusHistoryBundleWriter(_files);

        foreach (var n in Enumerable.Range(1, 5)) await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(n, 1));
        await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Project, new JsonObject { ["Id"] = "Projects-1", ["Name"] = "p" });
        await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Environment, new JsonObject { ["Id"] = "Environments-1", ["Name"] = "e" });

        using var zip = ZipFile.OpenRead(await writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1"));
        var entries = zip.Entries.Select(e => e.FullName).Where(n => n != "manifest.json").ToList();
        var releases = Enumerable.Range(1, 5).Select(n => $"history/Release/{OctopusHistoryBundleWriter.StagedPath(OctopusResourceKind.Release, $"Releases-{n}")[OctopusHistoryBundleWriter.KindPrefix(OctopusResourceKind.Release).Length..]}").Order(StringComparer.Ordinal);

        entries.ShouldBe([$"history/Environment/{FileOf(OctopusResourceKind.Environment, "Environments-1")}", $"history/Project/{FileOf(OctopusResourceKind.Project, "Projects-1")}", .. releases]);
        zip.Entries.Last().FullName.ShouldBe("manifest.json");
    }

    // The bundle is stored with the session, so a server that never built it can read it.
    [Fact]
    public async Task Bundle_StoredWithTheSession_SameBytes()
    {
        var writer = new OctopusHistoryBundleWriter(_files);

        await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(1, 1));
        var bundle = await writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1");

        (await _files.ReadAsync(_sessionId, OctopusImportSourceReader.HistoryBundleFileName)).ShouldBe(await File.ReadAllBytesAsync(bundle));
    }

    // A document counted when the build started but gone when it is read (the session was cleaned up meanwhile) fails the build: never a smaller bundle.
    [Fact]
    public async Task DocumentVanishesWhileBundling_StagingIncomplete_NoBundle()
    {
        var writer = new OctopusHistoryBundleWriter(new VanishingReadStore(_files));

        foreach (var n in Enumerable.Range(1, 3)) await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(n, 1));

        var ex = await Should.ThrowAsync<Squid.Core.Services.OctopusImport.Exceptions.OctopusImportHistoryStagingIncompleteException>(() => writer.BuildBundleAsync(_sessionId, _sessionDirectory, "inst-1"));

        (ex.Kind, ex.Stored, ex.Expected).ShouldBe((nameof(OctopusResourceKind.Release), 2, 3));
        File.Exists(Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName)).ShouldBeFalse();
        (await _files.GetHeaderAsync(_sessionId, OctopusImportSourceReader.HistoryBundleFileName)).ShouldBeNull();
    }

    // Created is what the fetch counts for its integrity check: only a path stored for the first time.
    [Fact]
    public async Task Write_ReportsCreatedOnlyForANewPath()
    {
        var writer = new OctopusHistoryBundleWriter(_files);

        (await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(1, 1))).Created.ShouldBeTrue();
        (await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(1, 2))).Created.ShouldBeFalse();
        (await new OctopusHistoryBundleWriter(_files).WriteJsonAsync(_sessionId, OctopusResourceKind.Release, Release(1, 3))).Created.ShouldBeFalse();
    }

    // A reference is checked against the session's staging, which another chunk (on another server) wrote.
    [Fact]
    public async Task IsStaged_SeesDocumentsAnotherWriterStaged_AndItsOwn()
    {
        await new OctopusHistoryBundleWriter(_files).WriteJsonAsync(_sessionId, OctopusResourceKind.Channel, new JsonObject { ["Id"] = "Channels-1" });
        var writer = new OctopusHistoryBundleWriter(_files);

        (await writer.IsStagedAsync(_sessionId, OctopusResourceKind.Channel, "channels-1")).ShouldBeTrue();
        (await writer.IsStagedAsync(_sessionId, OctopusResourceKind.Channel, "Channels-2")).ShouldBeFalse();

        await writer.WriteJsonAsync(_sessionId, OctopusResourceKind.Channel, new JsonObject { ["Id"] = "Channels-2" });

        (await writer.IsStagedAsync(_sessionId, OctopusResourceKind.Channel, "Channels-2")).ShouldBeTrue();
    }

    // Staged documents are compressed in the database; the bundle limits still count their own bytes.
    [Fact]
    public async Task StagedDocuments_WrittenDeflated_CountedByTheirOwnLength()
    {
        var store = new Mock<IOctopusImportSessionFileStore>();
        var document = Release(1, 10);

        store.Setup(s => s.TotalsAsync(_sessionId, OctopusImportSessionFilePaths.StagingPrefix, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionFileTotals(0, 0));
        store.Setup(s => s.WriteAsync(_sessionId, It.IsAny<string>(), It.IsAny<Stream>(), OctopusImportSessionFileEncoding.Deflate, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionFileWrite(42, null));

        (await new OctopusHistoryBundleWriter(store.Object).WriteJsonAsync(_sessionId, OctopusResourceKind.Release, document)).Length.ShouldBe(42);
        store.Verify(s => s.WriteAsync(_sessionId, OctopusHistoryBundleWriter.StagedPath(OctopusResourceKind.Release, "Releases-1"), It.IsAny<Stream>(), OctopusImportSessionFileEncoding.Deflate, It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose() => Directory.Delete(_sessionDirectory, recursive: true);

    private static string FileOf(OctopusResourceKind kind, string id) => OctopusHistoryBundleWriter.StagedPath(kind, id)[OctopusHistoryBundleWriter.KindPrefix(kind).Length..];

    // Reads every staged document but the first of a kind: as if it was purged after the build counted it.
    private sealed class VanishingReadStore(InMemoryOctopusImportSessionFileStore inner) : IOctopusImportSessionFileStore
    {
        public Task<OctopusImportSessionFileWrite> WriteAsync(Guid sessionId, string path, Stream content, OctopusImportSessionFileEncoding encoding, CancellationToken ct = default) => inner.WriteAsync(sessionId, path, content, encoding, ct);

        public Task<OctopusImportSessionFileHeader> GetHeaderAsync(Guid sessionId, string path, CancellationToken ct = default) => inner.GetHeaderAsync(sessionId, path, ct);

        public Task<byte[]> ReadAsync(Guid sessionId, string path, CancellationToken ct = default) => inner.ReadAsync(sessionId, path, ct);

        public Task<OctopusImportSessionFileHeader> CopyToAsync(Guid sessionId, string path, Stream destination, CancellationToken ct = default) => inner.CopyToAsync(sessionId, path, destination, ct);

        public IAsyncEnumerable<OctopusImportSessionFileContent> ReadAllAsync(Guid sessionId, string prefix, CancellationToken ct = default) => AllButFirstAsync(inner.ReadAllAsync(sessionId, prefix, ct));

        private static async IAsyncEnumerable<OctopusImportSessionFileContent> AllButFirstAsync(IAsyncEnumerable<OctopusImportSessionFileContent> files)
        {
            var first = true;

            await foreach (var file in files)
            {
                if (!first) yield return file;

                first = false;
            }
        }

        public Task<IReadOnlyList<OctopusImportSessionFileHeader>> ListAsync(Guid sessionId, string prefix, CancellationToken ct = default) => inner.ListAsync(sessionId, prefix, ct);

        public Task<OctopusImportSessionFileTotals> TotalsAsync(Guid sessionId, string prefix, CancellationToken ct = default) => inner.TotalsAsync(sessionId, prefix, ct);

        public Task<IReadOnlyDictionary<string, int>> CountByFolderAsync(Guid sessionId, string prefix, CancellationToken ct = default) => inner.CountByFolderAsync(sessionId, prefix, ct);

        public Task<bool> PurgeAndMarkCleanedAsync(Guid sessionId, string temporaryUploadPath, DateTimeOffset cleanedAt, CancellationToken ct = default) => inner.PurgeAndMarkCleanedAsync(sessionId, temporaryUploadPath, cleanedAt, ct);

        public Task<IReadOnlySet<Guid>> PurgedAmongAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default) => inner.PurgedAmongAsync(sessionIds, ct);
    }

    private static JsonObject Release(int n, int padding) => new() { ["Id"] = $"Releases-{n}", ["Version"] = $"1.0.{n}", ["ReleaseNotes"] = new string('x', Math.Max(padding, 0)) };

    private int StagedFileCount() => _files.Paths(_sessionId).Count(p => p.StartsWith(OctopusImportSessionFilePaths.StagingPrefix, StringComparison.Ordinal));

    private static async Task<OctopusManifestInventoryResult> InventoryAsync(string bundle, OctopusImportSizeLimits limits)
    {
        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(), new OctopusInputExtractor(), limits).ReadAsync(bundle);

        return new OctopusManifestInventoryBuilder().Build(extraction);
    }
}
