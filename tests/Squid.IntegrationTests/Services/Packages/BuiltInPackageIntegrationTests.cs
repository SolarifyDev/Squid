using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.ExternalFeeds.Exceptions;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Publishing;
using Squid.Message.Models.Publishing;
using Squid.Message.Requests.Publishing;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.Spaces;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Commands.Spaces;
using Squid.Message.Constants;
using Squid.Message.Requests.BuiltInPackages;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.IntegrationTests.Services.Packages;

/// <summary>
/// Integration tier: the real Postgres schema (the real migration, its unique indexes and foreign keys), the real EF data provider, the real
/// FileSystem package store in a temp folder, the real version assigner and counter, and the real container wiring. All the tests share one class so the
/// temp-file leak check cannot be disturbed by another test pushing at the same time.
/// </summary>
public class BuiltInPackageIntegrationTests : TestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-it-builtin-{Guid.NewGuid():N}");

    public BuiltInPackageIntegrationTests() : base("BuiltInPackage", "squid_it_builtin_package")
    {
    }

    void IDisposable.Dispose()
    {
        Dispose();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // === fixture helpers ===

    private static byte[] Zip(string content)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // A fixed time: CreateEntry stamps "now" at 2 s granularity, so the same content would be different bytes across a boundary.
            var entry = zip.CreateEntry("index.html");

            entry.LastWriteTime = new DateTimeOffset(2024, 1, 2, 3, 4, 6, TimeSpan.Zero);

            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));

            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private static byte[] TarGz(string content)
    {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "app/index.html") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });

        return buffer.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Setup(int SpaceId, int FeedId, int StoreId);

    private async Task<Setup> SetUpAsync(string spaceSlug = null)
    {
        var storeId = await CreateStoreAsync("Disk", _root, makeDefault: true);
        var spaceId = await CreateSpaceRowAsync(spaceSlug ?? $"s{Guid.NewGuid():N}"[..12]);

        await Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(spaceId, CancellationToken.None));

        var feed = await Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(spaceId, CancellationToken.None));

        return new Setup(spaceId, feed.Id, storeId);
    }

    private async Task<int> CreateStoreAsync(string name, string root, bool makeDefault)
    {
        var dto = await Run<IPackageStoreService, Squid.Message.Models.PackageStorage.PackageStoreDto>(s => s.CreateAsync(new CreatePackageStoreCommand { Name = name, Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = root } }, CancellationToken.None));

        if (makeDefault) await Run<IPackageStoreService, Squid.Message.Models.PackageStorage.PackageStoreDto>(s => s.SetDefaultAsync(dto.Id, CancellationToken.None));

        return dto.Id;
    }

    private async Task<int> CreateSpaceRowAsync(string slug)
    {
        var id = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var space = new Space { Name = slug, Slug = slug, Json = "{}" };

            await repository.InsertAsync(space);
            await unitOfWork.SaveChangesAsync();

            id = space.Id;
        });

        return id;
    }

    private Task<PushBuiltInPackageResponseData> PushAsync(int spaceId, byte[] bytes, string fileName = "Acme.Web.1.0.0.zip", string packageId = null, string version = null, bool replace = false)
    {
        var command = new PushBuiltInPackageCommand { SpaceId = spaceId, FileName = fileName, PackageId = packageId, Version = version, Replace = replace, Content = new MemoryStream(bytes), DeclaredSizeBytes = bytes.Length };

        return Run<IBuiltInPackagePushService, PushBuiltInPackageResponseData>(push => push.PushAsync(command, CancellationToken.None));
    }

    private Task<List<BuiltInPackage>> RowsAsync() => Run<IRepository, List<BuiltInPackage>>(r => r.QueryNoTracking<BuiltInPackage>().OrderBy(p => p.Id).ToListAsync());

    private Task<long> ScalarAsync(string sql) => Run<IRepository, long>(r => r.Database.SqlQueryRaw<long>(sql).SingleAsync());

    private string[] BlobFiles() => Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".squid-tmp")).ToArray() : [];

    private static string[] StagedUploads() => Directory.GetFiles(Path.GetTempPath(), "squid-builtin-push-*");

    // === feed provisioning ===

    [Fact]
    public async Task Seeder_GivesEverySpaceItsFeed_AndRunningItAgainAddsNone()
    {
        var spaces = new[] { await CreateSpaceRowAsync("a"), await CreateSpaceRowAsync("b"), await CreateSpaceRowAsync("c") };

        await new BuiltInFeedSeeder().SeedAsync(CurrentScope);
        await new BuiltInFeedSeeder().SeedAsync(CurrentScope);

        var feeds = await Run<IRepository, List<ExternalFeed>>(r => r.QueryNoTracking<ExternalFeed>(f => f.FeedType == BuiltInFeed.FeedType).ToListAsync());

        feeds.Select(f => f.SpaceId).OrderBy(i => i).ShouldBe(spaces.OrderBy(i => i));
        feeds.ShouldAllBe(f => f.Name == BuiltInFeedService.FeedName && f.Slug == BuiltInFeedService.FeedSlug && f.FeedUri == string.Empty);
    }

    [Fact]
    public async Task Seeder_LeavesAnExistingBuiltInFeedAsItWas()
    {
        var space = await CreateSpaceRowAsync("a");

        await Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(space, CancellationToken.None));
        var properties = "{\"StoreId\":\"9\"}";

        await Run<IRepository>(r => r.Database.ExecuteSqlInterpolatedAsync($"UPDATE external_feed SET name = 'Renamed', properties = {properties}"));

        await new BuiltInFeedSeeder().SeedAsync(CurrentScope);

        var feed = await Run<IRepository, ExternalFeed>(r => r.QueryNoTracking<ExternalFeed>().SingleAsync());

        feed.Name.ShouldBe("Renamed");
        feed.Properties.ShouldBe("{\"StoreId\":\"9\"}");
    }

    [Fact]
    public async Task Ensure_CalledConcurrentlyForOneSpace_CreatesOneFeed()
    {
        var space = await CreateSpaceRowAsync("a");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(space, CancellationToken.None))));

        (await Run<IRepository, int>(r => r.QueryNoTracking<ExternalFeed>().CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task CreatingASpace_ThroughTheSpaceService_GivesItItsBuiltInFeed()
    {
        var dto = await Run<ISpaceService, Squid.Message.Models.Spaces.SpaceDto>(s => s.CreateAsync(new CreateSpaceCommand { Name = "Fresh", Slug = "fresh" }, CancellationToken.None));

        var feed = await Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(dto.Id, CancellationToken.None));

        feed.FeedType.ShouldBe("BuiltIn");
        feed.SpaceId.ShouldBe(dto.Id);
    }

    [Fact]
    public async Task Locator_ForASpaceWithoutAFeed_Throws()
    {
        var space = await CreateSpaceRowAsync("bare");

        await Should.ThrowAsync<BuiltInFeedNotFoundException>(() => Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(space, CancellationToken.None)));
    }

    [Fact]
    public async Task TheBuiltInFeed_CannotBeDeleted_RetypedOrDuplicated_ThroughTheFeedCommands()
    {
        var setup = await SetUpAsync();
        var other = await Run<IExternalFeedService, int>(async s => (await s.CreateExternalFeedAsync(new CreateExternalFeedCommand { FeedType = "NuGet", FeedUri = "https://x", Name = "ext", Slug = "ext", SpaceId = setup.SpaceId }, CancellationToken.None)).Data.Id);

        await Should.ThrowAsync<BuiltInFeedDeletionException>(() => Run<IExternalFeedService>(s => s.DeleteExternalFeedsAsync(new DeleteExternalFeedsCommand { Ids = [other, setup.FeedId] }, CancellationToken.None)));
        await Should.ThrowAsync<BuiltInFeedTypeChangeException>(() => Run<IExternalFeedService>(s => s.UpdateExternalFeedAsync(new UpdateExternalFeedCommand { Id = setup.FeedId, FeedType = "NuGet", FeedUri = "https://x", Name = "n", Slug = "s", SpaceId = setup.SpaceId }, CancellationToken.None)));
        await Should.ThrowAsync<BuiltInFeedTypeChangeException>(() => Run<IExternalFeedService>(s => s.UpdateExternalFeedAsync(new UpdateExternalFeedCommand { Id = other, FeedType = "BuiltIn", Name = "n", Slug = "s", SpaceId = setup.SpaceId }, CancellationToken.None)));
        await Should.ThrowAsync<BuiltInFeedCreationException>(() => Run<IExternalFeedService>(s => s.CreateExternalFeedAsync(new CreateExternalFeedCommand { FeedType = "BuiltIn", Name = "second", Slug = "second", SpaceId = setup.SpaceId }, CancellationToken.None)));

        (await Run<IRepository, int>(r => r.QueryNoTracking<ExternalFeed>().CountAsync())).ShouldBe(2, "nothing was deleted, not even the ordinary feed named in the same command");
    }

    [Fact]
    public async Task TheBuiltInFeed_StillAcceptsAnEditThatKeepsItsType()
    {
        var setup = await SetUpAsync();

        await Run<IExternalFeedService>(s => s.UpdateExternalFeedAsync(new UpdateExternalFeedCommand { Id = setup.FeedId, FeedType = "BuiltIn", Name = "Team packages", Slug = "builtin", SpaceId = setup.SpaceId, Properties = new Dictionary<string, string> { ["StoreId"] = setup.StoreId.ToString() } }, CancellationToken.None));

        var feed = await Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(setup.SpaceId, CancellationToken.None));

        feed.Name.ShouldBe("Team packages");
        ExternalFeedProperties.GetString(feed, "StoreId").ShouldBe(setup.StoreId.ToString());
    }

    // === push ===

    [Fact]
    public async Task Push_ThenPushingTheSameBytesAgain_KeepsOneRowAndOneBlob_AndDoesNotAdvanceTheCounter()
    {
        var setup = await SetUpAsync();
        var bytes = Zip("one");

        var first = await PushAsync(setup.SpaceId, bytes);
        var again = await PushAsync(setup.SpaceId, bytes);

        first.Package.Version.ShouldBe("1.0.1");
        again.Deduplicated.ShouldBeTrue();
        again.Package.Version.ShouldBe("1.0.1");
        (await RowsAsync()).Count.ShouldBe(1);
        BlobFiles().Length.ShouldBe(1);
        (await ScalarAsync("SELECT COALESCE(MAX(value), 0) AS \"Value\" FROM package_version_counter")).ShouldBe(1);

        var next = await PushAsync(setup.SpaceId, Zip("two"));

        next.Package.Version.ShouldBe("1.0.2", "the deduplicated push must not have used up 1.0.2");
    }

    [Fact]
    public async Task Push_StoresTheFileInTheFeedsStore_ByContentKey_AndRecordsWhereItWent()
    {
        var setup = await SetUpAsync();
        var bytes = TarGz("tar");

        var result = await PushAsync(setup.SpaceId, bytes, fileName: "Acme.Web.1.0.0.tgz");

        var key = $"sha256/{Sha(bytes)[..2]}/{Sha(bytes)}.tar.gz";
        var row = (await RowsAsync()).Single();

        row.StoreId.ShouldBe(setup.StoreId);
        row.BlobKey.ShouldBe(key);
        row.FileExtension.ShouldBe(".tar.gz");
        row.SizeBytes.ShouldBe(bytes.Length);
        row.Sha256.ShouldBe(Sha(bytes));
        row.SpaceId.ShouldBe(setup.SpaceId);
        File.ReadAllBytes(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar))).ShouldBe(bytes);
        result.Package.Source.ShouldBe("api");
    }

    [Fact]
    public async Task Push_TwoUploadsWithDifferentCasing_ShareOneCanonicalId_AndOneCounter()
    {
        var setup = await SetUpAsync();

        var first = await PushAsync(setup.SpaceId, Zip("one"), packageId: "Acme.Web");
        var second = await PushAsync(setup.SpaceId, Zip("two"), packageId: "ACME.WEB");
        var third = await PushAsync(setup.SpaceId, Zip("three"), fileName: "acme.web.9.9.9.zip");

        new[] { first, second, third }.Select(r => r.Package.PackageId).Distinct().ShouldBe(["Acme.Web"]);
        new[] { first, second, third }.Select(r => r.Package.Version).ShouldBe(["1.0.1", "1.0.2", "1.0.3"]);
        (await ScalarAsync("SELECT COUNT(*) AS \"Value\" FROM builtin_package_name")).ShouldBe(1);
        (await ScalarAsync("SELECT COUNT(*) AS \"Value\" FROM package_version_counter")).ShouldBe(1, "one counter, because every push used the one canonical id string");
    }

    [Fact]
    public async Task Push_ConcurrentFirstPushesWithDifferentCasing_AgreeOnOneCanonicalId()
    {
        var setup = await SetUpAsync();
        var ids = new[] { "Acme.Web", "ACME.WEB", "acme.web", "aCmE.wEb" };

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => PushAsync(setup.SpaceId, Zip($"c{i}"), packageId: ids[i % ids.Length])));

        results.Select(r => r.Package.PackageId).Distinct().Count().ShouldBe(1, "two first pushes racing must not end up with two spellings");
        (await RowsAsync()).Select(r => r.PackageId).Distinct().Count().ShouldBe(1);
        results.Select(r => r.Package.Version).Distinct().Count().ShouldBe(8);
    }

    [Fact]
    public async Task Push_ConcurrentPushesOfDifferentContent_GetDistinctVersions()
    {
        var setup = await SetUpAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => PushAsync(setup.SpaceId, Zip($"build {i}"))));

        results.Select(r => r.Package.Version).OrderBy(v => v).ShouldBe(Enumerable.Range(1, 10).Select(i => $"1.0.{i}").OrderBy(v => v));
        (await RowsAsync()).Count.ShouldBe(10);
        BlobFiles().Length.ShouldBe(10);
    }

    [Fact]
    public async Task Push_ConcurrentPushesOfTheSameExplicitVersion_ExactlyOneWins()
    {
        var setup = await SetUpAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try { return (await PushAsync(setup.SpaceId, Zip($"v{i}"), version: "2.0.0")).Package.Version; }
            catch (PackageVersionAlreadyExistsException) { return null; }
        }));

        outcomes.Count(o => o != null).ShouldBe(1);
        (await RowsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_ReplaceOfAVersionAReleaseSelects_IsRefused_ButItIsAllowedOnceNoReleaseSelectsIt()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, Zip("v1"), version: "1.0.0");
        await InsertSelectionAsync(setup.FeedId, "acme.web", "1.0.0");

        await Should.ThrowAsync<PackageVersionInUseException>(() => PushAsync(setup.SpaceId, Zip("v1b"), version: "1.0.0", replace: true));

        (await RowsAsync()).Single().Sha256.ShouldBe(Sha(Zip("v1")));

        await InsertSelectionAsync(setup.FeedId, "Acme.Web", "9.9.9");
        await PushAsync(setup.SpaceId, Zip("v2"), version: "2.0.0");

        var replaced = await PushAsync(setup.SpaceId, Zip("v2b"), version: "2.0.0", replace: true);

        replaced.Replaced.ShouldBeTrue();
        (await RowsAsync()).Single(r => r.Version == "2.0.0").Sha256.ShouldBe(Sha(Zip("v2b")));
    }

    [Fact]
    public async Task UsedByARelease_IsDecidedByFeedPackageIgnoringCaseAndExactVersion()
    {
        var setup = await SetUpAsync();

        await InsertSelectionAsync(setup.FeedId, "ACME.web", "1.0.0");

        Task<bool> Used(int feed, string package, string version) => Run<IBuiltInPackageUsage, bool>(u => u.IsVersionSelectedByReleaseAsync(feed, package, version, CancellationToken.None));

        (await Used(setup.FeedId, "Acme.Web", "1.0.0")).ShouldBeTrue();
        (await Used(setup.FeedId, "Acme.Web", "1.0.1")).ShouldBeFalse("another version");
        (await Used(setup.FeedId, "Acme.Api", "1.0.0")).ShouldBeFalse("another package");
        (await Used(setup.FeedId + 1, "Acme.Web", "1.0.0")).ShouldBeFalse("another feed");
    }

    private async Task InsertSelectionAsync(int feedId, string packageReferenceName, string version)
    {
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(new ReleaseSelectedPackage { ReleaseId = 1, FeedId = feedId, ActionName = "Deploy", PackageReferenceName = packageReferenceName, Version = version });
            await unitOfWork.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Push_RefusedAndSucceededAndDeduplicated_LeaveNoStagedFileBehind()
    {
        var setup = await SetUpAsync();
        var before = StagedUploads();

        await PushAsync(setup.SpaceId, Zip("ok"));
        await PushAsync(setup.SpaceId, Zip("ok"));
        await Should.ThrowAsync<UnsupportedPackageFormatException>(() => PushAsync(setup.SpaceId, Encoding.UTF8.GetBytes("not an archive")));
        await Should.ThrowAsync<PackageVersionAlreadyExistsException>(() => PushAsync(setup.SpaceId, Zip("other"), version: "1.0.1"));

        StagedUploads().Except(before).ShouldBeEmpty();
    }

    // === store delete guard ===

    [Fact]
    public async Task AStoreHoldingPackages_CannotBeDeleted_AndAnEmptyOneCan()
    {
        var setup = await SetUpAsync();
        var spare = await CreateStoreAsync("Spare", Path.Combine(_root, "spare"), makeDefault: false);

        await PushAsync(setup.SpaceId, Zip("one"));
        await Run<IPackageStoreService, int>(async s => { await s.SetDefaultAsync(spare, CancellationToken.None); return 0; });

        await Should.ThrowAsync<PackageStoreInUseException>(() => Run<IPackageStoreService>(s => s.DeleteAsync(setup.StoreId, CancellationToken.None)));

        (await Run<IPackageStoreService, List<Squid.Message.Models.PackageStorage.PackageStoreDto>>(s => s.ListAsync(CancellationToken.None))).Select(s => s.Id).ShouldContain(setup.StoreId);

        await Run<IPackageStoreService, int>(async s => { await s.SetDefaultAsync(setup.StoreId, CancellationToken.None); return 0; });
        await Run<IPackageStoreService>(s => s.DeleteAsync(spare, CancellationToken.None));
    }

    [Fact]
    public async Task AStoreHoldingPackages_CannotMoveItsRoot_ButCanBeRenamed_AndTheirPackagesStayReadable()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, Zip("one"));

        var moved = new UpdatePackageStoreCommand { Id = setup.StoreId, Name = "Disk", Properties = new Dictionary<string, string> { ["Root"] = Path.Combine(_root, "elsewhere") } };

        await Should.ThrowAsync<PackageStoreLocationChangeException>(() => Run<IPackageStoreService>(s => s.UpdateAsync(moved, CancellationToken.None)));

        var renamed = new UpdatePackageStoreCommand { Id = setup.StoreId, Name = "Renamed", Properties = new Dictionary<string, string> { ["Root"] = _root } };
        var dto = await Run<IPackageStoreService, Squid.Message.Models.PackageStorage.PackageStoreDto>(s => s.UpdateAsync(renamed, CancellationToken.None));

        dto.Name.ShouldBe("Renamed");
        dto.Properties["Root"].ShouldBe(_root);

        var spare = await CreateStoreAsync("Spare", Path.Combine(_root, "spare"), makeDefault: false);
        var spareMoved = new UpdatePackageStoreCommand { Id = spare, Name = "Spare", Properties = new Dictionary<string, string> { ["Root"] = Path.Combine(_root, "spare2") } };

        (await Run<IPackageStoreService, Squid.Message.Models.PackageStorage.PackageStoreDto>(s => s.UpdateAsync(spareMoved, CancellationToken.None))).Properties["Root"].ShouldEndWith("spare2");
    }

    [Fact]
    public async Task TheDatabase_RefusesToDeleteAStoreAPackagePointsAt_EvenWithoutTheServiceCheck()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, Zip("one"));

        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(() => Run<IRepository>(r => r.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM package_store WHERE id = {setup.StoreId}")));

        ex.SqlState.ShouldBe("23503");
    }

    // === the feed seen from the rest of the system, through the real container ===

    [Fact]
    public async Task TheFeedIsServedThroughTheExistingFeedServices_AndTheRoutedPackageSource()
    {
        var setup = await SetUpAsync();
        var one = Zip("one");

        await PushAsync(setup.SpaceId, one, packageId: "Acme.Web");
        await PushAsync(setup.SpaceId, Zip("two"), packageId: "Acme.Web");
        await PushAsync(setup.SpaceId, Zip("api"), packageId: "Acme.Api");

        var versions = await Run<IExternalFeedPackageVersionService, SearchFeedPackageVersionsResponseData>(s => s.ListVersionsAsync(setup.FeedId, "acme.web", 30, true, null, CancellationToken.None));
        var search = await Run<IExternalFeedPackageSearchService, SearchFeedPackagesResponseData>(s => s.SearchAsync(setup.FeedId, "acme", 10, CancellationToken.None));
        var notes = await Run<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedPackageNotesService, GetPackageNotesResponseData>(s => s.GetNotesAsync([new PackageNotesQuery { FeedId = setup.FeedId, PackageId = "Acme.Web", Version = "1.0.1" }], CancellationToken.None));

        versions.Versions.ShouldBe(["1.0.2", "1.0.1"]);
        search.Packages.ShouldBe(["Acme.Api", "Acme.Web"]);
        notes.Packages.Single().Succeeded.ShouldBeTrue();
        notes.Packages.Single().Published.ShouldNotBeNull();

        var feed = await Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(setup.SpaceId, CancellationToken.None));
        var fetched = await Run<IPackageContentFetcher, PackageFetchResult>(f => f.FetchAsync(feed, "ACME.WEB", "1.0.1", CancellationToken.None));

        fetched.RawBytes.ShouldBe(one);
        fetched.FileExtension.ShouldBe(".zip");
    }

    [Fact]
    public async Task TheQueryService_ListsPackagesAndVersions_AndReportsTheMissing()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, Zip("one"), packageId: "Acme.Web");
        await PushAsync(setup.SpaceId, Zip("two"), packageId: "Acme.Web", version: "1.10.0");
        await PushAsync(setup.SpaceId, Zip("three"), packageId: "Acme.Web", version: "1.2.0");

        var packages = await Run<IBuiltInPackageQueryService, ListBuiltInPackagesResponseData>(q => q.ListPackagesAsync(new ListBuiltInPackagesRequest { SpaceId = setup.SpaceId }, CancellationToken.None));
        var versions = await Run<IBuiltInPackageQueryService, ListBuiltInPackageVersionsResponseData>(q => q.ListVersionsAsync(new ListBuiltInPackageVersionsRequest { SpaceId = setup.SpaceId, PackageId = "acme.web" }, CancellationToken.None));
        var one = await Run<IBuiltInPackageQueryService, Squid.Message.Models.BuiltInPackages.BuiltInPackageVersionDto>(q => q.GetVersionAsync(new GetBuiltInPackageVersionRequest { SpaceId = setup.SpaceId, PackageId = "ACME.WEB", Version = "1.2.0" }, CancellationToken.None));

        packages.FeedId.ShouldBe(setup.FeedId);
        packages.Packages.Single().VersionCount.ShouldBe(3);
        versions.Versions.Select(v => v.Version).ShouldBe(["1.10.0", "1.2.0", "1.0.1"], "newest first by version, not by text or push time");
        one.Version.ShouldBe("1.2.0");

        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Run<IBuiltInPackageQueryService, ListBuiltInPackageVersionsResponseData>(q => q.ListVersionsAsync(new ListBuiltInPackageVersionsRequest { SpaceId = setup.SpaceId, PackageId = "nope" }, CancellationToken.None)));
        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Run<IBuiltInPackageQueryService, Squid.Message.Models.BuiltInPackages.BuiltInPackageVersionDto>(q => q.GetVersionAsync(new GetBuiltInPackageVersionRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web", Version = "9.9.9" }, CancellationToken.None)));
    }

    [Fact]
    public async Task TheFeedOfAnotherSpace_IsNeverReached()
    {
        var one = await SetUpAsync("one");
        var other = await CreateSpaceRowAsync("two");

        await Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(other, CancellationToken.None));
        await PushAsync(one.SpaceId, Zip("one"), packageId: "Acme.Web");

        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Run<IBuiltInPackageQueryService, ListBuiltInPackageVersionsResponseData>(q => q.ListVersionsAsync(new ListBuiltInPackageVersionsRequest { SpaceId = other, PackageId = "Acme.Web" }, CancellationToken.None)));
    }

    [Fact]
    public async Task Push_FeedNamingAStoreThatDoesNotExist_IsRefused_AndStoresNothing()
    {
        var setup = await SetUpAsync();

        await Run<IRepository>(r => r.Database.ExecuteSqlInterpolatedAsync($"UPDATE external_feed SET properties = '{{\"StoreId\":\"98765\"}}' WHERE id = {setup.FeedId}"));

        await Should.ThrowAsync<PackageStoreNotFoundException>(() => PushAsync(setup.SpaceId, Zip("one")));

        (await RowsAsync()).ShouldBeEmpty();
        BlobFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_AfterTheDefaultStoreMoves_KeepsEarlierPackagesReadableFromTheirOwnStore()
    {
        var setup = await SetUpAsync();
        var other = Path.Combine(_root, "second");
        var one = Zip("one");

        await PushAsync(setup.SpaceId, one, packageId: "Acme.Web");

        var second = await CreateStoreAsync("Second", other, makeDefault: true);

        await PushAsync(setup.SpaceId, Zip("two"), packageId: "Acme.Web");

        (await RowsAsync()).Select(r => r.StoreId).ShouldBe([setup.StoreId, second]);

        var feed = await Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(setup.SpaceId, CancellationToken.None));

        (await Run<IPackageContentFetcher, PackageFetchResult>(f => f.FetchAsync(feed, "Acme.Web", "1.0.1", CancellationToken.None))).RawBytes.ShouldBe(one);
    }

    // === inspection facts ===

    private static byte[] SiteZip()
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("my-site/index.html", "<html/>"), ("my-site/package.json", "{\"scripts\":{\"build\":\"x\"}}"), ("my-site/squid.yaml", "recipe: Static") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));

                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    // === hardening ===

    [Fact]
    public async Task Push_SameFilesAsTarGzAfterZip_IsADuplicate_ThroughTheRealIndex()
    {
        var setup = await SetUpAsync();
        var zip = Zip("same files");
        var first = await PushAsync(setup.SpaceId, zip, packageId: "Acme.Web");
        var again = await PushAsync(setup.SpaceId, TarGzOf("index.html", "same files"), fileName: "Acme.Web.1.0.0.tar.gz", packageId: "Acme.Web");

        again.Deduplicated.ShouldBeTrue();
        again.Package.Version.ShouldBe(first.Package.Version);
        (await RowsAsync()).Single().ContentFingerprint.ShouldNotBeNullOrEmpty();
        (await ScalarAsync("SELECT COALESCE(MAX(value), 0) AS \"Value\" FROM package_version_counter")).ShouldBe(1);
    }

    private static byte[] TarGzOf(string name, string content)
    {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });

        return buffer.ToArray();
    }

    private sealed class ThrowingInspector : IPackageInspector
    {
        public Task InspectAsync(PackageInspectionContext context, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Push_StoresFactsAsJsonb_AndTheVersionEndpointReturnsThem()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, SiteZip(), packageId: "Acme.Web", version: "1.0.0");

        (await ScalarAsync("SELECT COUNT(*) AS \"Value\" FROM builtin_package WHERE facts #>> '{{Values,archive.root}}' = 'my-site'")).ShouldBe(1, "the column is real jsonb, queryable");

        var dto = await Run<IBuiltInPackageQueryService, Squid.Message.Models.BuiltInPackages.BuiltInPackageVersionDto>(q => q.GetVersionAsync(new GetBuiltInPackageVersionRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web", Version = "1.0.0" }, CancellationToken.None));

        dto.Facts.Get(PackageFactKeys.FilesCount).ShouldBe("3");
        (await ScalarAsync("SELECT COUNT(*) AS \"Value\" FROM builtin_package WHERE facts::text LIKE '%index.html%'")).ShouldBe(0, "no file list in the stored jsonb");
        dto.Facts.Is(PackageFactKeys.NodeBuildScript).ShouldBeTrue();
        dto.Facts.Get(PackageFactKeys.SquidRecipe).ShouldBe("Static");

        var listed = await Run<IBuiltInPackageQueryService, ListBuiltInPackageVersionsResponseData>(q => q.ListVersionsAsync(new ListBuiltInPackageVersionsRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web" }, CancellationToken.None));

        listed.Versions.Single().Facts.ShouldBeNull("lists stay light");
    }

    [Fact]
    public async Task Push_WithAnInspectorThatThrows_StillStoresThePackage_AndRecordsTheFailure()
    {
        var setup = await SetUpAsync();
        var bytes = SiteZip();
        var command = new PushBuiltInPackageCommand { SpaceId = setup.SpaceId, FileName = "Acme.Web.1.0.0.zip", PackageId = "Acme.Web", Version = "1.0.0", Content = new MemoryStream(bytes), DeclaredSizeBytes = bytes.Length };

        var result = await Run<IBuiltInPackagePushService, PushBuiltInPackageResponseData>(push => push.PushAsync(command, CancellationToken.None), b => b.RegisterType<ThrowingInspector>().As<IPackageInspector>());

        (await RowsAsync()).Count.ShouldBe(1);
        BlobFiles().Length.ShouldBe(1);
        result.Package.Facts.Get(PackageFactKeys.InspectorFailedPrefix + nameof(ThrowingInspector)).ShouldBe("boom");
        result.Package.Facts.Is(PackageFactKeys.MarkerSquidYaml).ShouldBeTrue("the other inspectors still ran");
    }

    [Fact]
    public async Task Rows_WithNullFacts_ReadFine_AndAreReportedAsNotInspected()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, Zip("old"), packageId: "Acme.Web", version: "1.0.0");
        await Run<IRepository>(r => r.Database.ExecuteSqlRawAsync("UPDATE builtin_package SET facts = NULL"));

        var dto = await Run<IBuiltInPackageQueryService, Squid.Message.Models.BuiltInPackages.BuiltInPackageVersionDto>(q => q.GetVersionAsync(new GetBuiltInPackageVersionRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web", Version = "1.0.0" }, CancellationToken.None));
        var recommendations = await Run<IRecipeRecommendationService, RecipeRecommendationsDto>(s => s.GetAsync(new GetRecipeRecommendationsRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web", Version = "1.0.0" }, CancellationToken.None));

        dto.Facts.ShouldBeNull();
        recommendations.Inspected.ShouldBeFalse();
        recommendations.Facts.ShouldBeNull();
        (await Run<IBuiltInPackageQueryService, ListBuiltInPackageVersionsResponseData>(q => q.ListVersionsAsync(new ListBuiltInPackageVersionsRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web" }, CancellationToken.None))).Versions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_OfAnArchiveWhoseMarkersComeAfterThousandsOfFiles_StillStoresTheMarkers()
    {
        var setup = await SetUpAsync();
        var bytes = ZipOf(Enumerable.Range(0, 5100).Select(i => ($"node_modules/m{i}/index.js", "x")).Concat([("Dockerfile", "FROM x"), ("squid.yaml", "recipe: Docker")]));

        await PushAsync(setup.SpaceId, bytes, packageId: "Acme.Web", version: "1.0.0");

        var dto = await Run<IBuiltInPackageQueryService, Squid.Message.Models.BuiltInPackages.BuiltInPackageVersionDto>(q => q.GetVersionAsync(new GetBuiltInPackageVersionRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web", Version = "1.0.0" }, CancellationToken.None));

        dto.Facts.Is(PackageFactKeys.MarkerDockerfile).ShouldBeTrue();
        dto.Facts.Get(PackageFactKeys.SquidRecipe).ShouldBe("Docker");
        dto.Facts.Get(PackageFactKeys.FilesCount).ShouldBe("5102");
        (await ScalarAsync("SELECT LENGTH(facts::text) AS \"Value\" FROM builtin_package")).ShouldBeLessThan(2048);
    }

    private static byte[] ZipOf(IEnumerable<(string Name, string Content)> entries)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));

                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Push_Replacing_RewritesTheFactsInTheRow()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, Zip("v1"), packageId: "Acme.Web", version: "1.0.0");
        await PushAsync(setup.SpaceId, SiteZip(), packageId: "Acme.Web", version: "1.0.0", replace: true);

        var dto = await Run<IBuiltInPackageQueryService, Squid.Message.Models.BuiltInPackages.BuiltInPackageVersionDto>(q => q.GetVersionAsync(new GetBuiltInPackageVersionRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web", Version = "1.0.0" }, CancellationToken.None));

        dto.Facts.Get(PackageFactKeys.ArchiveRoot).ShouldBe("my-site");
    }

    [Fact]
    public async Task Recommendations_ForAnInspectedVersion_CarryTheFacts_AndNoRecipesYet()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, SiteZip(), packageId: "Acme.Web", version: "1.0.0");

        var result = await Run<IRecipeRecommendationService, RecipeRecommendationsDto>(s => s.GetAsync(new GetRecipeRecommendationsRequest { SpaceId = setup.SpaceId, PackageId = "acme.web", Version = "1.0.0" }, CancellationToken.None));

        result.Inspected.ShouldBeTrue();
        result.Facts.Get(PackageFactKeys.SquidRecipe).ShouldBe("Static");
        result.Selection.UnknownRecipe.ShouldBe("Static", "no recipe is registered in this build, so the name squid.yaml gives matches none");
        result.Selection.Candidates.ShouldBeEmpty();

        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Run<IRecipeRecommendationService, RecipeRecommendationsDto>(s => s.GetAsync(new GetRecipeRecommendationsRequest { SpaceId = setup.SpaceId, PackageId = "Acme.Web", Version = "9.9.9" }, CancellationToken.None)));
    }

    [Fact]
    public async Task Push_AnExplicitNewVersionWithTheLatestsContent_IsStored_ThroughTheRealIndex()
    {
        var setup = await SetUpAsync();

        await PushAsync(setup.SpaceId, Zip("same"), version: "1.0.0");

        var second = await PushAsync(setup.SpaceId, Zip("same"), version: "2.0.0");

        second.Deduplicated.ShouldBeFalse();
        (await RowsAsync()).Select(r => r.Version).ShouldBe(["1.0.0", "2.0.0"]);
    }

    // The release lands between the early check and the row update, i.e. while the file write is going on. The seam is a decorator of the index that creates the
    // release just before the real update runs; nothing sleeps.
    [Fact]
    public async Task Replace_WhenAReleaseSelectsTheVersionAfterTheEarlyCheck_IsRefusedByTheAtomicUpdate_AndChangesNothing()
    {
        var setup = await SetUpAsync();
        var original = Zip("original");

        await PushAsync(setup.SpaceId, original, version: "1.0.0");

        var command = new PushBuiltInPackageCommand { SpaceId = setup.SpaceId, FileName = "Acme.Web.1.0.0.zip", Version = "1.0.0", Replace = true, Content = new MemoryStream(Zip("replacement")) };

        await Should.ThrowAsync<PackageVersionInUseException>(() => Run<IBuiltInPackagePushService, PushBuiltInPackageResponseData>(push => push.PushAsync(command, CancellationToken.None), builder =>
            builder.Register(c => new ReleaseAppearsBeforeReplace(new BuiltInPackageDataProvider(c.Resolve<IRepository>(), c.Resolve<IUnitOfWork>()), c.Resolve<IRepository>(), c.Resolve<IUnitOfWork>(), setup.FeedId)).As<IBuiltInPackageDataProvider>().InstancePerLifetimeScope()));

        (await RowsAsync()).Single().Sha256.ShouldBe(Sha(original));
    }

    private sealed class ReleaseAppearsBeforeReplace(BuiltInPackageDataProvider inner, IRepository repository, IUnitOfWork unitOfWork, int feedId) : IBuiltInPackageDataProvider
    {
        public Task<string> ResolveCanonicalIdAsync(int f, string p, CancellationToken ct) => inner.ResolveCanonicalIdAsync(f, p, ct);
        public Task<BuiltInPackage> GetAsync(int f, string p, string v, CancellationToken ct) => inner.GetAsync(f, p, v, ct);
        public Task<List<BuiltInPackage>> ListVersionsAsync(int f, string p, CancellationToken ct) => inner.ListVersionsAsync(f, p, ct);
        public Task<List<Squid.Message.Models.BuiltInPackages.BuiltInPackageDto>> ListPackagesAsync(int f, string filter, int take, CancellationToken ct) => inner.ListPackagesAsync(f, filter, take, ct);
        public Task InsertAsync(BuiltInPackage package, CancellationToken ct) => inner.InsertAsync(package, ct);

        public async Task ReplaceContentAsync(int id, BuiltInPackage content, CancellationToken ct)
        {
            await repository.InsertAsync(new ReleaseSelectedPackage { ReleaseId = 1, FeedId = feedId, ActionName = "Deploy", PackageReferenceName = "ACME.WEB", Version = "1.0.0" }, ct);
            await unitOfWork.SaveChangesAsync(ct);

            await inner.ReplaceContentAsync(id, content, ct);
        }
    }

    [Fact]
    public async Task Push_WhenTheStoredBlobIsTruncatedOnDisk_APushOfTheSameFileRepairsIt()
    {
        var setup = await SetUpAsync();
        var bytes = Zip("heal me");

        await PushAsync(setup.SpaceId, bytes, version: "1.0.0");

        var blob = BlobFiles().Single();

        await File.WriteAllBytesAsync(blob, bytes[..(bytes.Length / 2)]);
        await PushAsync(setup.SpaceId, bytes, version: "2.0.0");

        (await File.ReadAllBytesAsync(blob)).ShouldBe(bytes);
    }

    [Fact]
    public async Task Ids_AreMatchedIgnoringCase_UnderTheTurkishCulture()
    {
        var setup = await SetUpAsync();
        var previous = System.Globalization.CultureInfo.CurrentCulture;

        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");

        try
        {
            await PushAsync(setup.SpaceId, Zip("one"), packageId: "TITLE.INDEX", version: "1.0.0");
            await InsertSelectionAsync(setup.FeedId, "title.index", "1.0.0");

            (await Run<IBuiltInPackageUsage, bool>(u => u.IsVersionSelectedByReleaseAsync(setup.FeedId, "Title.Index", "1.0.0", CancellationToken.None))).ShouldBeTrue();

            var found = await Run<IBuiltInPackageQueryService, Squid.Message.Models.BuiltInPackages.BuiltInPackageVersionDto>(q => q.GetVersionAsync(new GetBuiltInPackageVersionRequest { SpaceId = setup.SpaceId, PackageId = "title.index", Version = "1.0.0" }, CancellationToken.None));
            var second = await PushAsync(setup.SpaceId, Zip("two"), packageId: "title.index", version: "1.0.1");
            var listed = await Run<IBuiltInPackageQueryService, ListBuiltInPackagesResponseData>(q => q.ListPackagesAsync(new ListBuiltInPackagesRequest { SpaceId = setup.SpaceId, Filter = "TITLE" }, CancellationToken.None));

            found.PackageId.ShouldBe("TITLE.INDEX");
            second.Package.PackageId.ShouldBe("TITLE.INDEX");
            listed.Packages.Single().VersionCount.ShouldBe(2);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task OctopusImport_NeverSeesTheBuiltInFeedAsADestinationFeed()
    {
        var setup = await SetUpAsync();

        await Run<IExternalFeedService>(s => s.CreateExternalFeedAsync(new CreateExternalFeedCommand { FeedType = "NuGet", FeedUri = "https://x", Name = "Built-in feed", Slug = "builtin2", SpaceId = setup.SpaceId }, CancellationToken.None));

        var resources = await Run<IOctopusImportDestinationDataProvider, IReadOnlyList<OctopusImportDestinationResource>>(p => p.GetResourcesAsync(setup.SpaceId, CancellationToken.None));
        var feeds = resources.Where(r => r.Kind == Squid.Core.Services.OctopusImport.Octopus.OctopusResourceKind.Feed).ToList();

        feeds.Count.ShouldBe(1);
        feeds.Single().Id.ShouldNotBe(setup.FeedId);
    }

    [Fact]
    public async Task ALegacyBuiltInTypedFeedWithAUri_IsNeverAdopted_ByTheProvisionerOrTheSeeder()
    {
        var space = await CreateSpaceRowAsync("legacy");

        await Run<IRepository>(r => r.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO external_feed (feed_type, feed_uri, name, slug, space_id, last_modified_date) VALUES ('BuiltIn', 'https://old.example', 'old', 'old', {space}, now())"));

        var ex = await Should.ThrowAsync<BuiltInFeedConflictException>(() => Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(space, CancellationToken.None)));

        ex.Message.ShouldContain("https://old.example");
        await Should.ThrowAsync<BuiltInFeedConflictException>(() => new BuiltInFeedSeeder().SeedAsync(CurrentScope));
    }
}
