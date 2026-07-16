using System.IO;
using Moq;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class PackageAcquisitionServiceTests : IDisposable
{
    private static readonly byte[] SampleBytes = [0x01, 0x02, 0x03, 0x04, 0x05];
    private static readonly string ExpectedSha256 = ComputeSha256(SampleBytes);

    private readonly Mock<IPackageContentFetcher> _fetcherMock;
    private readonly PackageAcquisitionService _sut;
    private readonly string _tempDir;
    private readonly ExternalFeed _feed;

    public PackageAcquisitionServiceTests()
    {
        _fetcherMock = new Mock<IPackageContentFetcher>();
        _sut = new PackageAcquisitionService(_fetcherMock.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-test-{Guid.NewGuid()}");
        _feed = new ExternalFeed { Id = 42, FeedType = "Generic", FeedUri = "https://packages.example.com" };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static ExternalFeed CreateFeed(int id = 1) => new() { Id = id, FeedType = "Generic", FeedUri = "https://packages.example.com" };

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    // === AcquireAsync Success ===

    [Fact]
    public async Task AcquireAsync_Succeeds_ReturnsResultWithLocalPath()
    {
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "nginx", "1.21.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), SampleBytes));

        var result = await _sut.AcquireAsync(_feed, "nginx", "1.21.0", 123, CancellationToken.None);

        result.PackageId.ShouldBe("nginx");
        result.Version.ShouldBe("1.21.0");
        result.LocalPath.ShouldContain("nginx.1.21.0.zip");
        result.SizeBytes.ShouldBe(SampleBytes.Length);
        result.Hash.ShouldBe(ExpectedSha256);
    }

    [Fact]
    public async Task AcquireAsync_Succeeds_CreatesStorageDirectory()
    {
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "app", "2.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), SampleBytes));

        await _sut.AcquireAsync(_feed, "app", "2.0.0", 456, CancellationToken.None);

        var expectedPath = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(456);
        Directory.Exists(expectedPath).ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireAsync_Succeeds_StoresFileWithCorrectContent()
    {
        var bytes = "hello world"u8.ToArray();
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "pkg", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), bytes));

        var result = await _sut.AcquireAsync(_feed, "pkg", "1.0.0", 789, CancellationToken.None);

        File.Exists(result.LocalPath).ShouldBeTrue();
        File.ReadAllBytes(result.LocalPath).ShouldBe(bytes);
    }

    [Fact]
    public async Task AcquireAsync_Succeeds_OverwritesExistingFile()
    {
        var bytes1 = "first"u8.ToArray();
        var bytes2 = "second"u8.ToArray();

        _fetcherMock.SetupSequence(f => f.FetchAsync(_feed, "overwrite", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), bytes1))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), bytes2));

        var result1 = await _sut.AcquireAsync(_feed, "overwrite", "1.0.0", 111, CancellationToken.None);
        var result2 = await _sut.AcquireAsync(_feed, "overwrite", "1.0.0", 111, CancellationToken.None);

        result2.Hash.ShouldNotBe(result1.Hash);
        File.ReadAllBytes(result2.LocalPath).ShouldBe(bytes2);
    }

    [Fact]
    public async Task AcquireAsync_Succeeds_ComputesLowercaseSha256Hash()
    {
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "nginx", "1.21.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), SampleBytes));

        var result = await _sut.AcquireAsync(_feed, "nginx", "1.21.0", 123, CancellationToken.None);

        result.Hash.ShouldBe(ExpectedSha256);
        result.Hash.Length.ShouldBe(64);
        result.Hash.ShouldMatch("^[a-f0-9]{64}$");
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    public async Task AcquireAsync_Succeeds_ComputesCorrectSha256Hash(byte[] bytes)
    {
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "hashed", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), bytes));
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "hashed2", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), bytes));

        var result = await _sut.AcquireAsync(_feed, "hashed", "1.0.0", 222, CancellationToken.None);

        result.Hash.ShouldNotBeNullOrEmpty();
        result.Hash.Length.ShouldBe(64);
        result.Hash.ShouldMatch("^[a-f0-9]{64}$");
        result.Hash.ShouldBe(ComputeSha256(bytes));

        var result2 = await _sut.AcquireAsync(_feed, "hashed2", "1.0.0", 223, CancellationToken.None);
        result2.Hash.ShouldBe(result.Hash);
    }

    [Theory]
    [InlineData("", "1.0.0")]
    [InlineData("   ", "1.0.0")]
    [InlineData("pkg", "")]
    [InlineData("pkg", "   ")]
    public async Task AcquireAsync_BlankPackageIdOrVersion_Throws(string packageId, string version)
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AcquireAsync(_feed, packageId, version, 1, CancellationToken.None));
    }

    // === AcquireAsync Failure ===

    [Fact]
    public async Task AcquireAsync_EmptyContent_Throws()
    {
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "empty", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), Array.Empty<byte>()));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AcquireAsync(_feed, "empty", "1.0.0", 333, CancellationToken.None));

        ex.Message.ShouldContain("empty content");
        ex.Message.ShouldContain("empty");
        ex.Message.ShouldContain("1.0.0");
    }

    [Fact]
    public async Task AcquireAsync_FetchFailedWithWarnings_Throws()
    {
        var warnings = new List<string> { "HTTP 404", "Retry count exceeded" };
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "missing", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), warnings, Array.Empty<byte>()));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AcquireAsync(_feed, "missing", "1.0.0", 444, CancellationToken.None));

        ex.Message.ShouldContain("empty content");
    }

    // === Unsupported / supported feed types ===

    [Theory]
    [InlineData("Docker")]
    [InlineData("Docker Container Registry")]
    [InlineData("Helm")]
    [InlineData("Helm Chart Repository")]
    [InlineData("AWS Elastic Container Registry")]
    public async Task AcquireAsync_UnsupportedFeedType_Throws(string feedType)
    {
        var feed = new ExternalFeed { Id = 7, FeedType = feedType, FeedUri = "https://registry.example.com" };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AcquireAsync(feed, "pkg", "1.0.0", 1, CancellationToken.None));

        ex.Message.ShouldContain("cannot be installed by Deploy a Package");
        ex.Message.ShouldContain(feedType);
        _fetcherMock.Verify(
            f => f.FetchAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcquireAsync_GitHubFeed_DoesNotThrowNuGetOnlyGuard()
    {
        var feed = new ExternalFeed { Id = 8, FeedType = "GitHub", FeedUri = "https://api.github.com" };
        _fetcherMock.Setup(f => f.FetchAsync(feed, "owner/repo", "v1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), SampleBytes));

        var result = await _sut.AcquireAsync(feed, "owner/repo", "v1.0.0", 900, CancellationToken.None);

        result.PackageId.ShouldBe("owner/repo");
        result.LocalPath.ShouldNotBeNullOrEmpty();
        File.Exists(result.LocalPath).ShouldBeTrue();
        _fetcherMock.Verify(f => f.FetchAsync(feed, "owner/repo", "v1.0.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("NuGet", ".nupkg")]
    [InlineData("NuGet Feed", ".nupkg")]
    [InlineData("GitHub", ".tar.gz")]
    [InlineData("GitHub Repository Feed", ".tar.gz")]
    [InlineData("Maven", ".zip")]
    [InlineData("Generic", ".zip")]
    public async Task AcquireAsync_UsesArchiveExtensionForFeedType(string feedType, string expectedExtension)
    {
        var feed = new ExternalFeed { Id = 9, FeedType = feedType, FeedUri = "https://packages.example.com" };
        _fetcherMock.Setup(f => f.FetchAsync(feed, "Acme.App", "1.2.3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), SampleBytes));

        var result = await _sut.AcquireAsync(feed, "Acme.App", "1.2.3", 901, CancellationToken.None);

        result.LocalPath.ShouldEndWith($"Acme.App.1.2.3{expectedExtension}");
    }

    [Theory]
    [InlineData("Acme.App.zip", "Generic", "https://packages.example.com/repo", ".zip")]
    [InlineData("owner/repo.tar.gz", "GitHub", "https://api.github.com", ".tar.gz")]
    [InlineData("Acme.App", "NuGet", "https://packages.example.com/artifacts.zip", ".nupkg")]
    [InlineData("Acme.App", "Generic", "https://packages.example.com/artifacts.zip", ".zip")]
    [InlineData("Acme.App", "Generic", "https://packages.example.com/download/app.nupkg", ".nupkg")]
    [InlineData("Acme.App", "Generic", "https://packages.example.com/repo", ".zip")]
    [InlineData("Acme.App", "GitHub", "https://api.github.com", ".tar.gz")]
    public void ResolveArchiveExtension_PrefersPackageIdThenFeedType_NotGenericFeedUri(
        string packageId, string feedType, string feedUri, string expected)
    {
        PackageAcquisitionService.ResolveArchiveExtension(feedType, packageId, feedUri)
            .ShouldBe(expected);
    }

    // === BuildPackageStoragePath ===

    [Theory]
    [InlineData(123)]
    [InlineData(456)]
    public void BuildPackageStoragePath_ReturnsCorrectPath(int deploymentId)
    {
        var expectedSuffix = $"squid-packages{Path.DirectorySeparatorChar}{deploymentId}";
        var path = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(deploymentId);

        path.ShouldEndWith(expectedSuffix);
        path.StartsWith(Path.GetTempPath()).ShouldBeTrue();
    }
}
