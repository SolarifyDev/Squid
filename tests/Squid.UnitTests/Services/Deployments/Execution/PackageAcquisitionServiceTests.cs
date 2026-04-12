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
    private static readonly string ExpectedMd5 = ComputeMd5(SampleBytes);

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

    private static string ComputeMd5(byte[] bytes)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(bytes));
    }

    // === AcquireAsync Success ===

    [Fact]
    public async Task AcquireAsync_Succeeds_ReturnsResultWithLocalPath()
    {
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "nginx", "1.21.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), SampleBytes));

        var result = await _sut.AcquireAsync(_feed, "nginx", "1.21.0", 123, CancellationToken.None);

        result.PackageId.ShouldBe("nginx");
        result.Version.ShouldBe("1.21.0");
        result.LocalPath.ShouldContain("nginx.1.21.0.nupkg");
        result.SizeBytes.ShouldBe(SampleBytes.Length);
        result.Hash.ShouldBe(ExpectedMd5);
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

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    public async Task AcquireAsync_Succeeds_ComputesCorrectMd5Hash(byte[] bytes)
    {
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "hashed", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), bytes));
        _fetcherMock.Setup(f => f.FetchAsync(_feed, "hashed2", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), bytes));

        var result = await _sut.AcquireAsync(_feed, "hashed", "1.0.0", 222, CancellationToken.None);

        result.Hash.ShouldNotBeNullOrEmpty();
        result.Hash.Length.ShouldBe(32);
        result.Hash.ShouldMatch("(?i)^[a-f0-9]{32}$");

        // Same bytes produce the same hash
        var result2 = await _sut.AcquireAsync(_feed, "hashed2", "1.0.0", 223, CancellationToken.None);
        result2.Hash.ShouldBe(result.Hash);
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
