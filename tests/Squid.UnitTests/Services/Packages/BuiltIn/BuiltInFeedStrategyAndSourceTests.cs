using System.IO;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.GitHub;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Helm;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.NuGet;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Http;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>The built-in feed's three strategies and package source over the fake index and store: what they return, and that nothing but the built-in type reaches them.</summary>
public class BuiltInFeedStrategyAndSourceTests
{
    private static readonly byte[] Zip = TestArchives.Zip(("app/values.yaml", "a: 1"), ("app/bin/app.dll", "dll"));

    private readonly FakeBuiltInPackageDataProvider _packages = new();
    private readonly InMemoryBlobStore _blobs = new();
    private readonly ExternalFeed _feed = new() { Id = 5, SpaceId = 1, FeedType = BuiltInFeed.FeedType };

    private BuiltInFeedPackageSource Source() => new(_packages, new FakeBlobStoreRegistry(_blobs));

    private void SeedBlob(string packageId, string version, byte[] bytes, string extension = ".zip", string recordedSha = null)
    {
        var sha = TestArchives.Sha256Of(bytes);
        var row = _packages.Seed(_feed.Id, packageId, version, recordedSha ?? sha);

        row.FileExtension = extension;
        row.SizeBytes = bytes.Length;
        row.BlobKey = BuiltInBlobKey.For(sha, extension);

        _blobs.Blobs[row.BlobKey] = bytes;
    }

    // === exact matching ===

    [Theory]
    [InlineData("BuiltIn", true)]
    [InlineData("builtin", true)]
    [InlineData("NuGet", false)]
    [InlineData("Docker", false)]
    [InlineData("GitHub", false)]
    [InlineData("Helm", false)]
    [InlineData("MyBuiltIn", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EveryBuiltInPart_ClaimsExactlyTheBuiltInType(string feedType, bool expected)
    {
        new BuiltInPackageVersionStrategy(_packages).CanHandle(feedType).ShouldBe(expected);
        new BuiltInPackageSearchStrategy(_packages).CanHandle(feedType).ShouldBe(expected);
        new BuiltInPackageNotesStrategy(_packages).CanHandle(feedType).ShouldBe(expected);
        Source().CanHandle(feedType).ShouldBe(expected);
    }

    [Fact]
    public void NoOtherStrategyOrSource_ClaimsTheBuiltInType()
    {
        var http = new Mock<ISquidHttpClientFactory>().Object;
        var downloader = new FeedPackageDownloader(http);

        new IPackageVersionStrategy[] { new NuGetPackageVersionStrategy(http), new DockerPackageVersionStrategy(http), new GitHubPackageVersionStrategy(http), new HelmPackageVersionStrategy(http) }.Any(s => s.CanHandle("BuiltIn")).ShouldBeFalse();
        new IPackageSearchStrategy[] { new NuGetPackageSearchStrategy(http), new DockerPackageSearchStrategy(http), new GitHubPackageSearchStrategy(http), new HelmPackageSearchStrategy(http) }.Any(s => s.CanHandle("BuiltIn")).ShouldBeFalse();
        new IPackageNotesStrategy[] { new NuGetPackageNotesStrategy(http), new DockerPackageNotesStrategy(http), new GitHubPackageNotesStrategy(http), new HelmPackageNotesStrategy(http) }.Any(s => s.CanHandle("BuiltIn")).ShouldBeFalse();
        new IFeedPackageSource[] { new HelmFeedPackageSource(http, downloader), new GitHubFeedPackageSource(downloader), new NuGetFeedPackageSource(downloader) }.Any(s => s.CanHandle("BuiltIn")).ShouldBeFalse();
    }

    // === version strategy ===

    [Fact]
    public async Task Versions_AreTheIndexedVersionsOfThatPackageOnly_IgnoringIdCase()
    {
        _packages.Seed(5, "Acme.Web", "1.0.1", "a");
        _packages.Seed(5, "Acme.Web", "1.0.2", "b");
        _packages.Seed(5, "Other", "9.9.9", "c");
        _packages.Seed(6, "Acme.Web", "7.7.7", "d");

        var versions = await new BuiltInPackageVersionStrategy(_packages).ListVersionsAsync(_feed, "acme.web", CancellationToken.None);

        versions.OrderBy(v => v).ShouldBe(["1.0.1", "1.0.2"]);
    }

    [Fact]
    public async Task Versions_BlankPackageId_IsEmpty()
    {
        (await new BuiltInPackageVersionStrategy(_packages).ListVersionsAsync(_feed, " ", CancellationToken.None)).ShouldBeEmpty();
    }

    // === search strategy ===

    [Fact]
    public async Task Search_FindsPackageIdsContainingTheQuery_IgnoringCase_AndHonoursTake()
    {
        _packages.Seed(5, "Acme.Web", "1.0.0", "a");
        _packages.Seed(5, "Acme.Api", "1.0.0", "b");
        _packages.Seed(5, "Contoso", "1.0.0", "c");

        var strategy = new BuiltInPackageSearchStrategy(_packages);

        (await strategy.SearchAsync(_feed, "ACME", 10, CancellationToken.None)).ShouldBe(["Acme.Api", "Acme.Web"]);
        (await strategy.SearchAsync(_feed, "acme", 1, CancellationToken.None)).ShouldBe(["Acme.Api"]);
        (await strategy.SearchAsync(_feed, "", 10, CancellationToken.None)).Count.ShouldBe(3);
    }

    // === notes strategy ===

    [Fact]
    public async Task Notes_ForAKnownVersion_CarryThePublishedDate()
    {
        var published = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

        _packages.Seed(5, "Acme.Web", "1.0.0", "a", published);

        var result = await new BuiltInPackageNotesStrategy(_packages).GetNotesAsync(_feed, "Acme.Web", "1.0.0", CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        result.Published.ShouldBe(published);
    }

    [Fact]
    public async Task Notes_ForAnUnknownVersion_Fail()
    {
        var result = await new BuiltInPackageNotesStrategy(_packages).GetNotesAsync(_feed, "Acme.Web", "1.0.0", CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("not in the built-in feed");
    }

    // === package source ===

    [Fact]
    public async Task Source_ReturnsTheStoredBytes_TheExtractedFiles_AndTheRealExtension()
    {
        var tar = TestArchives.TarGz(("app/run.yaml", "k: v"), ("app/run.sh", "echo hi"));

        SeedBlob("Acme.Web", "1.0.0", tar, ".tar.gz");

        var result = await Source().FetchAsync(_feed, "acme.web", "1.0.0", CancellationToken.None);

        result.RawBytes.ShouldBe(tar);
        result.FileExtension.ShouldBe(".tar.gz");
        result.Files.Keys.ShouldBe(["run.yaml"]);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Source_ZipPackage_ExtractsItsFiles()
    {
        SeedBlob("Acme.Web", "1.0.0", Zip);

        var result = await Source().FetchAsync(_feed, "Acme.Web", "1.0.0", CancellationToken.None);

        result.Files.Keys.ShouldBe(["values.yaml"]);
        result.FileExtension.ShouldBe(".zip");
    }

    [Fact]
    public async Task Source_UnknownVersion_ThrowsNotFound_NotAWarning()
    {
        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Source().FetchAsync(_feed, "Acme.Web", "1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task Source_VersionOfAnotherFeed_IsNotServed()
    {
        SeedBlob("Acme.Web", "1.0.0", Zip);

        var other = new ExternalFeed { Id = 99, SpaceId = 2, FeedType = BuiltInFeed.FeedType };

        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Source().FetchAsync(other, "Acme.Web", "1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task Source_AlteredBlob_IsRefusedByItsRecordedHash()
    {
        SeedBlob("Acme.Web", "1.0.0", Zip, recordedSha: new string('0', 64));

        var ex = await Should.ThrowAsync<BuiltInPackageIntegrityException>(() => Source().FetchAsync(_feed, "Acme.Web", "1.0.0", CancellationToken.None));

        ex.Message.ShouldContain("SHA-256");
    }

    [Fact]
    public async Task Source_MissingBlob_Throws()
    {
        var row = _packages.Seed(5, "Acme.Web", "1.0.0", TestArchives.Sha256Of(Zip));

        row.SizeBytes = Zip.Length;

        await Should.ThrowAsync<Squid.Core.Services.Packages.Storage.Exceptions.PackageBlobNotFoundException>(() => Source().FetchAsync(_feed, "Acme.Web", "1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task Source_PackageBiggerThanAnArray_IsKeptButCannotBeDeployed()
    {
        var row = _packages.Seed(5, "Big", "1.0.0", "a");

        row.SizeBytes = (long)Array.MaxLength + 1;

        await Should.ThrowAsync<BuiltInPackageTooLargeToDeployException>(() => Source().FetchAsync(_feed, "Big", "1.0.0", CancellationToken.None));
    }

    // === through the router and the acquisition ===

    [Fact]
    public async Task Router_SendsABuiltInFeedToTheBuiltInSource_NotTheGenericFallback()
    {
        SeedBlob("Acme.Web", "1.0.0", Zip);

        var http = new Mock<ISquidHttpClientFactory>();
        var downloader = new FeedPackageDownloader(http.Object);
        var router = new RoutingPackageContentFetcher([new NuGetFeedPackageSource(downloader), Source()], new Squid.Core.Services.DeploymentExecution.Packages.Sources.Generic.GenericFeedPackageSource(downloader));

        var result = await router.FetchAsync(_feed, "Acme.Web", "1.0.0", CancellationToken.None);

        result.RawBytes.ShouldBe(Zip);
        http.Verify(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task Acquisition_KeepsTheSourcesRealExtension_InsteadOfGuessingZip()
    {
        var tar = TestArchives.TarGz(("run.sh", "echo hi"));

        SeedBlob("Acme.Web", "1.0.0", tar, ".tar.gz");

        var acquisition = new PackageAcquisitionService(new RoutingPackageContentFetcher([Source()], null));
        var result = await acquisition.AcquireAsync(_feed, "Acme.Web", "1.0.0", 4242, CancellationToken.None);

        try
        {
            result.LocalPath.ShouldEndWith("Acme.Web.1.0.0.tar.gz");
            File.ReadAllBytes(result.LocalPath).ShouldBe(tar);
            result.Hash.ShouldBe(TestArchives.Sha256Of(tar));
        }
        finally
        {
            Directory.Delete(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(4242), recursive: true);
        }
    }

    [Fact]
    public async Task Acquisition_OtherFeedTypes_StillGuessTheExtensionAsBefore()
    {
        var fetcher = new Mock<IPackageContentFetcher>();
        var feed = new ExternalFeed { Id = 1, FeedType = "NuGet", FeedUri = "https://x" };

        fetcher.Setup(f => f.FetchAsync(feed, "Acme.Web", "1.0.0", It.IsAny<CancellationToken>())).ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), [], Zip));

        var result = await new PackageAcquisitionService(fetcher.Object).AcquireAsync(feed, "Acme.Web", "1.0.0", 4243, CancellationToken.None);

        try
        {
            result.LocalPath.ShouldEndWith(".nupkg");
        }
        finally
        {
            Directory.Delete(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(4243), recursive: true);
        }
    }
}
