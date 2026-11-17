using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// A Config-as-Code project against the in-memory Octopus API: its project document is staged (so the preview can say why it is not
/// imported) but nothing below <c>projects/{id}/</c> and none of its history is ever requested — a real server answers those with 400 —
/// while the database-backed project beside it is read whole. The picker marks it from the listing it already reads.
/// </summary>
public sealed class OctopusRestConfigAsCodeFetchTests : IDisposable
{
    private const int SpaceId = 7;
    private const string CacProject = "Projects-2";

    private readonly List<string> _directories = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigAsCodeProject_StagedButNothingOfItsOwnRequested(bool chosen)
    {
        var server = SpaceWithConfigAsCodeProject();
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor, chosen ? ["Projects-1", CacProject] : null);

        server.Paths.ShouldNotContain(p => p.Contains($"/projects/{CacProject}/", StringComparison.OrdinalIgnoreCase) || p.Contains($"projects={CacProject}", StringComparison.OrdinalIgnoreCase), string.Join("\n", server.Paths));
        BundleIds(directory).ShouldContain(CacProject);
        cursor.ProjectIds.ShouldBe(["Projects-1"]);
    }

    [Fact]
    public async Task ConfigAsCodeProject_CountedOnce_NothingRejected_ProjectCountExcludesIt()
    {
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(SpaceWithConfigAsCodeProject(), NewSessionDirectory(), cursor, null);

        cursor.Counts.ConfigAsCodeProjects.ShouldBe(1);
        cursor.Counts.ItemsRejected.ShouldBe(0);
        cursor.SkippedItems.ShouldBeEmpty();
        OctopusRestHistoryFetcher.Progress(cursor).ProjectCount.ShouldBe(1);
    }

    [Fact]
    public async Task MixedSpace_DatabaseProjectFetchedWhole()
    {
        var server = SpaceWithConfigAsCodeProject();
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor, null);

        server.Paths.ShouldContain(p => p.Contains("projects/Projects-1/deploymentprocesses"));
        server.Paths.ShouldContain(p => p.Contains("projects/Projects-1/variables"));
        BundleIds(directory).ShouldContain("Releases-1");
        (cursor.Counts.Releases, cursor.Counts.Deployments).ShouldBe((1, 1));
    }

    // Producer → consumer: the staged bundle plans with the Config-as-Code project skipped for its reason, never blocking.
    [Fact]
    public async Task Bundle_PlansWithTheProjectSkippedForItsReason_NoNewBlocker()
    {
        var directory = NewSessionDirectory();

        await FetchUntilDoneAsync(SpaceWithConfigAsCodeProject(), directory, CatalogCursor(), null);

        var graph = await ReadBundleAsync(directory);
        var (preview, blockers) = await PlanAsync(graph);
        preview.Resources.Single(r => r.SourceId == CacProject).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported);
        blockers.ShouldBe([OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing]);
    }

    [Fact]
    public async Task StagedProjectDocument_HoldsNoGitUrlOrCredentials()
    {
        var directory = NewSessionDirectory();

        await FetchUntilDoneAsync(SpaceWithConfigAsCodeProject(), directory, CatalogCursor(), null);

        var document = BundleDocument(directory, CacProject);
        document.ShouldNotContain("git.example.com");
        document.ShouldNotContain("git-user");
        document.ShouldContain("VersionControlled");
    }

    [Fact]
    public async Task Listing_ConfigAsCodeProject_Flagged()
    {
        var listing = await Lister(SpaceWithConfigAsCodeProject()).ListProjectsAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, null));

        listing.Projects.Select(p => (p.Id, p.IsConfigAsCode)).ShouldBe([("Projects-1", false), (CacProject, true)]);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // Projects-2 becomes version-controlled; like a real server, everything below it answers 400.
    private static FakeOctopusServer SpaceWithConfigAsCodeProject()
    {
        var server = new FakeOctopusServer { SnapshotFeedId = "Feeds-1" }.WithFixtureSpace().Release("Projects-1", 1).Deployment("Projects-1", 1);
        var project = ((JsonArray)server.Resources["projects"]).OfType<JsonObject>().Single(p => p["Id"]!.GetValue<string>() == CacProject);

        project["IsVersionControlled"] = true;
        project["PersistenceSettings"] = JsonNode.Parse("""{"Type":"VersionControlled","Url":"https://git.example.com/acme/worker.git","DefaultBranch":"main","BasePath":".octopus","Credentials":{"Type":"UsernamePassword","Username":"git-user","Password":{"HasValue":true,"NewValue":null,"Hint":null}}}""");
        server.Override = request => request.RequestUri!.AbsolutePath.Contains($"/projects/{CacProject}/", StringComparison.OrdinalIgnoreCase) ? OctopusRestStubHandler.Status(HttpStatusCode.BadRequest) : null;

        return server;
    }

    private static OctopusImportHistoryFetchDto CatalogCursor() => new() { Phase = OctopusHistoryFetchPhase.Catalog };

    private static IOctopusRestProjectLister Lister(FakeOctopusServer server) => new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore());

    private string NewSessionDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"squid-config-as-code-{Guid.NewGuid():N}");
        _directories.Add(directory);
        return directory;
    }

    private static async Task FetchUntilDoneAsync(FakeOctopusServer server, string directory, OctopusImportHistoryFetchDto cursor, IReadOnlyCollection<string> chosen)
    {
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), new OctopusRestOptions { ChunkDuration = TimeSpan.FromSeconds(5) });

        for (var chunk = 0; !cursor.Done && chunk < 50; chunk++)
            await fetcher.FetchChunkAsync(new OctopusHistoryFetchContext { ServerUrl = FakeOctopusServer.Url, ApiKey = FakeOctopusServer.ApiKey, SessionDirectory = directory, Cursor = cursor, PriorMappings = OctopusImportPriorMappings.Empty, ChosenProjectIds = chosen });

        cursor.Done.ShouldBeTrue();
    }

    private static string BundlePath(string directory) => Path.Combine(directory, OctopusImportSourceReader.HistoryBundleFileName);

    private static List<string> BundleIds(string directory)
    {
        using var zip = ZipFile.OpenRead(BundlePath(directory));
        using var manifest = zip.GetEntry("manifest.json")!.Open();

        return JsonSerializer.Deserialize<OctopusExportManifestDto>(manifest).Entries.Select(e => e.Id).ToList();
    }

    private static string BundleDocument(string directory, string id)
    {
        using var zip = ZipFile.OpenRead(BundlePath(directory));
        using var stream = zip.GetEntry("manifest.json")!.Open();
        var entry = JsonSerializer.Deserialize<OctopusExportManifestDto>(stream).Entries.Single(e => e.Id == id);
        using var reader = new StreamReader(zip.GetEntry(entry.DocumentSource)!.Open());

        return reader.ReadToEnd();
    }

    private static async Task<OctopusResourceGraph> ReadBundleAsync(string directory)
    {
        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(), new OctopusInputExtractor(), new OctopusImportSizeLimits()).ReadAsync(BundlePath(directory));

        return new OctopusResourceGraphBuilder().Build(new OctopusManifestInventoryBuilder().Build(extraction));
    }

    private static async Task<(OctopusImportPreviewPlanDto Preview, List<string> Blockers)> PlanAsync(OctopusResourceGraph graph)
    {
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph);
        var conflicts = await new OctopusImportConflictDiscoveryService(EmptyDestination(), EmptySourceMap()).DiscoverAsync(SpaceId, graph);
        var accessor = new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.None };
        var preview = new OctopusImportPreviewPlanner(accessor).BuildPreviewPlan(dependencyPlan, conflicts);
        var validation = new OctopusImportPreviewValidator(accessor).Validate(graph, dependencyPlan, conflicts, preview);
        var blockers = preview.Diagnostics.Concat(preview.Resources.SelectMany(r => r.Diagnostics)).Concat(validation.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Code).Distinct().Order().ToList();

        return (preview, blockers);
    }

    private static IOctopusImportDestinationDataProvider EmptyDestination()
    {
        var destination = new Mock<IOctopusImportDestinationDataProvider>();
        destination.Setup(d => d.GetResourcesAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync([SquidBuiltInTeam(1, BuiltInTeamNames.Everyone), SquidBuiltInTeam(2, BuiltInTeamNames.Administrators)]);
        destination.Setup(d => d.GetReleaseKeysAsync(SpaceId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return destination.Object;
    }

    private static OctopusImportDestinationResource SquidBuiltInTeam(int id, string name) => new(id, 0, OctopusResourceKind.Team, name, null, DateTimeOffset.UtcNow.AddDays(-1)) { IsBuiltIn = true, MemberCount = 0 };

    private static IOctopusImportSourceMapDataProvider EmptySourceMap()
    {
        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();
        sourceMap.Setup(s => s.LoadAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(OctopusImportPriorMappings.Empty);
        return sourceMap.Object;
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }
}
