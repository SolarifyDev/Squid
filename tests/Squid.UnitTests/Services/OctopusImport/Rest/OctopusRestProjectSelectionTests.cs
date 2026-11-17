using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

using Squid.UnitTests.Services.OctopusImport.SessionFiles;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// Choosing projects before reading them, against the in-memory Octopus API: the listing reads projects and project groups only, and a
/// fetch with a selection still reads the space's shared catalog but reads the configuration and history of the chosen projects alone.
/// </summary>
public sealed class OctopusRestProjectSelectionTests : IDisposable
{
    private readonly List<string> _directories = [];

    // ── Fetch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Selection_StagesAndReadsOnlyTheChosenProject()
    {
        var server = FixtureSpace();
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor, ["Projects-1"]);

        cursor.ProjectIds.ShouldBe(["Projects-1"]);
        server.Paths.ShouldNotContain(p => p.Contains("projects/Projects-2/", StringComparison.OrdinalIgnoreCase) || p.Contains("projects=Projects-2", StringComparison.OrdinalIgnoreCase), "nothing of an unchosen project is read beyond the catalog listing");
        server.Paths.ShouldContain(p => p.Contains("projects/Projects-1/channels"));
        var bundled = BundleIds(directory);
        bundled.ShouldContain("Projects-1");
        bundled.ShouldNotContain("Projects-2", "an unchosen project is not staged");
        bundled.ShouldContain("Environments-1", "the shared catalog is read whole");
    }

    [Fact]
    public async Task NoSelection_EveryProjectRead()
    {
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(FixtureSpace(), NewSessionDirectory(), cursor, null);

        cursor.ProjectIds.ShouldBe(["Projects-1", "Projects-2"]);
    }

    // Ids compare ignoring case, as Octopus ids do.
    [Fact]
    public async Task Selection_MatchesIgnoringCase()
    {
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(FixtureSpace(), NewSessionDirectory(), cursor, ["projects-2"]);

        cursor.ProjectIds.ShouldBe(["Projects-2"]);
    }

    [Fact]
    public async Task SelectionMatchingNothing_GoesToClosure_StillBuildsTheBundle()
    {
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(FixtureSpace(), directory, cursor, ["Projects-99"]);

        cursor.ProjectIds.ShouldBeEmpty();
        File.Exists(Path.Combine(directory, OctopusImportSourceReader.HistoryBundleFileName)).ShouldBeTrue();
        BundleIds(directory).ShouldNotContain(id => id.StartsWith("Projects-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_CountsTheChosenProjects()
    {
        var server = FixtureSpace();
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor, ["Projects-1"]);

        OctopusRestHistoryFetcher.Progress(cursor).ProjectCount.ShouldBe(1);
    }

    // ── Listing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_EveryProjectAndGroupAcrossPages_NoPerProjectRequest()
    {
        var server = new FakeOctopusServer { ClientOptions = new OctopusRestOptions { PageSize = 1 } }.WithFixtureSpace();

        var listing = await Lister(server).ListProjectsAsync(Connection());

        listing.ShouldSatisfyAllConditions(l => l.InstallationId.ShouldBe("inst-1"), l => l.SpaceId.ShouldBe("Spaces-1"));
        listing.Projects.ShouldBe([new OctopusRestProjectItem("Projects-1", "Web App", "ProjectGroups-1", false), new OctopusRestProjectItem("Projects-2", "Worker Service", "ProjectGroups-1", false)]);
        listing.Groups.ShouldBe([new OctopusRestProjectGroupItem("ProjectGroups-1", "Default Project Group"), new OctopusRestProjectGroupItem("ProjectGroups-2", "Unused Group")]);
        server.Paths.Where(p => p.Contains("/projects?")).Count().ShouldBe(2, "one page per project with a page size of 1");
        server.Paths.ShouldNotContain(p => p.Contains("/projects/") || p.Contains("releases") || p.Contains("deployments") || p.Contains("/users/"));
    }

    // An id the fetch could never request is never offered.
    [Fact]
    public async Task Listing_LeavesOutIdsTheFetchCouldNeverRequest()
    {
        var server = new FakeOctopusServer().WithFixtureSpace();
        server.Resources["projects"] = new JsonArray(Project("Projects-1", "Fine", true), Project("../admin", "Traversal"), Project("a b", "Space"));

        var listing = await Lister(server).ListProjectsAsync(Connection());

        listing.Projects.ShouldBe([new OctopusRestProjectItem("Projects-1", "Fine", "ProjectGroups-1", true)]);
    }

    [Fact]
    public async Task Listing_SpacelessServer_ReadsTheRootCollections()
    {
        var server = new FakeOctopusServer { Spaceless = true };
        server.Resources["projects"] = new JsonArray(Project("Projects-7", "Old"));
        server.Resources["projectgroups"] = new JsonArray(new JsonObject { ["Id"] = "ProjectGroups-1", ["Name"] = "All" });

        var listing = await Lister(server).ListProjectsAsync(Connection());

        listing.SpaceId.ShouldBeNull();
        listing.Projects.Single().Id.ShouldBe("Projects-7");
        server.Paths.ShouldContain(p => p.StartsWith("/api/projects?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listing_NamedSpaceTheServerDoesNotHave_NotFound()
    {
        var ex = await Should.ThrowAsync<OctopusRestException>(() => Lister(new FakeOctopusServer().WithFixtureSpace()).ListProjectsAsync(Connection("Spaces-99")));

        ex.Kind.ShouldBe(OctopusRestErrorKind.NotFound);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static FakeOctopusServer FixtureSpace() => new FakeOctopusServer { SnapshotFeedId = "Feeds-1" }.WithFixtureSpace().Release("Projects-1", 1).Deployment("Projects-1", 1);

    private static OctopusImportHistoryFetchDto CatalogCursor() => new() { Phase = OctopusHistoryFetchPhase.Catalog };

    private static OctopusServerConnection Connection(string space = null) => new(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, space);

    private static IOctopusRestProjectLister Lister(FakeOctopusServer server) => new OctopusRestHistoryFetcher(server.Factory, TestSessionFiles.Writer(), TestSessionFiles.Details(), TestSessionFiles.Store);

    private static JsonObject Project(string id, string name, bool disabled = false) => new() { ["Id"] = id, ["Name"] = name, ["ProjectGroupId"] = "ProjectGroups-1", ["IsDisabled"] = disabled };

    private string NewSessionDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"squid-project-selection-{Guid.NewGuid():N}");
        _directories.Add(directory);
        return directory;
    }

    private static async Task FetchUntilDoneAsync(FakeOctopusServer server, string directory, OctopusImportHistoryFetchDto cursor, IReadOnlyCollection<string> chosen)
    {
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, TestSessionFiles.Writer(), TestSessionFiles.Details(), TestSessionFiles.Store, new OctopusRestOptions { ChunkDuration = TimeSpan.FromSeconds(5) });

        for (var chunk = 0; !cursor.Done && chunk < 50; chunk++)
            await fetcher.FetchChunkAsync(new OctopusHistoryFetchContext { ServerUrl = FakeOctopusServer.Url, ApiKey = FakeOctopusServer.ApiKey, SessionId = TestSessionFiles.IdOf(directory), SessionDirectory = directory, Cursor = cursor, PriorMappings = OctopusImportPriorMappings.Empty, ChosenProjectIds = chosen });

        cursor.Done.ShouldBeTrue();
    }

    private static List<string> BundleIds(string directory)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(directory, OctopusImportSourceReader.HistoryBundleFileName));
        using var manifest = zip.GetEntry("manifest.json")!.Open();

        return JsonSerializer.Deserialize<OctopusExportManifestDto>(manifest).Entries.Select(e => e.Id).ToList();
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }
}
