using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>A built-in package reaches the deployment as a stream copied to its file: never held in memory, verified against what was recorded, and the HTTP feeds are untouched.</summary>
public class BuiltInStreamingDeployTests
{
    private const long Size = 100L * 1024 * 1024;

    private readonly FakeBuiltInPackageDataProvider _packages = new();
    private readonly ExternalFeed _feed = new() { Id = 5, SpaceId = 1, FeedType = BuiltInFeed.FeedType };

    private static string ShaOfZeros(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[81920];

        for (long done = 0; done < length; done += chunk.Length)
            hash.AppendData(chunk, 0, (int)Math.Min(chunk.Length, length - done));

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class GeneratedStore(long length, long flipAt = -1) : IPackageBlobStore
    {
        public string Kind => "Generated";

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(new Generated(length, flipAt));

        public Task PutAsync(string key, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(true);
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ListKeysAsync(string prefix, CancellationToken ct) => throw new NotSupportedException();
    }

    // Zeros, readable in chunks and never materialised; one byte can be flipped to play a store that altered the file.
    private sealed class Generated(long length, long flipAt) : Stream
    {
        private long _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _served; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _served);

            Array.Clear(buffer, offset, n);

            if (flipAt >= _served && flipAt < _served + n) buffer[offset + (int)(flipAt - _served)] = 1;

            _served += n;

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StoreRegistry(IPackageBlobStore store) : IPackageBlobStoreRegistry
    {
        public Task<IPackageBlobStore> ResolveAsync(int storeId, CancellationToken ct) => Task.FromResult(store);
        public Task<IPackageBlobStore> ResolveDefaultAsync(CancellationToken ct) => Task.FromResult(store);
    }

    private PackageAcquisitionService Acquisition(IPackageBlobStore store, Mock<IPackageContentFetcher> fetcher)
    {
        return new PackageAcquisitionService(fetcher.Object, [new BuiltInPackageFileStreamSource(_packages, new StoreRegistry(store))]);
    }

    private void Seed(long length, string sha)
    {
        var row = _packages.Seed(_feed.Id, "Big.App", "1.0.0", sha);

        row.SizeBytes = length;
        row.Sha256 = sha;
        row.FileExtension = ".tar.gz";
    }

    [Fact]
    public async Task Acquire_ABigBuiltInPackage_IsCopiedToItsFileWithoutBeingHeldInMemory()
    {
        var sha = ShaOfZeros(Size);
        var fetcher = new Mock<IPackageContentFetcher>(MockBehavior.Strict);

        Seed(Size, sha);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await Acquisition(new GeneratedStore(Size), fetcher).AcquireAsync(_feed, "Big.App", "1.0.0", 7771, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        try
        {
            new FileInfo(result.LocalPath).Length.ShouldBe(Size);
            result.LocalPath.ShouldEndWith("Big.App.1.0.0.tar.gz");
            result.Hash.ShouldBe(sha);
            result.SizeBytes.ShouldBe(Size);
            allocated.ShouldBeLessThan(Size / 8, $"a {Size} byte package must not be buffered; {allocated} bytes were allocated while acquiring it");
        }
        finally
        {
            Directory.Delete(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(7771), recursive: true);
        }

        fetcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Acquire_AFileTheStoreAltered_FailsAndLeavesNoFileBehind()
    {
        const long small = 5L * 1024 * 1024;

        Seed(small, ShaOfZeros(small));

        var ex = await Should.ThrowAsync<BuiltInPackageIntegrityException>(() => Acquisition(new GeneratedStore(small, flipAt: 1234), new Mock<IPackageContentFetcher>()).AcquireAsync(_feed, "Big.App", "1.0.0", 7772, CancellationToken.None));

        ex.Message.ShouldContain("SHA-256");

        var dir = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(7772);

        try
        {
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Acquire_AFileTheStoreCutShort_Fails()
    {
        const long small = 5L * 1024 * 1024;

        Seed(small, ShaOfZeros(small));

        await Should.ThrowAsync<BuiltInPackageIntegrityException>(() => Acquisition(new GeneratedStore(small - 10), new Mock<IPackageContentFetcher>()).AcquireAsync(_feed, "Big.App", "1.0.0", 7773, CancellationToken.None));

        Directory.Delete(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(7773), recursive: true);
    }

    [Fact]
    public async Task Acquire_ABuiltInPackageThatIsNotInTheIndex_Fails()
    {
        await Should.ThrowAsync<BuiltInPackageNotFoundException>(() => Acquisition(new GeneratedStore(10), new Mock<IPackageContentFetcher>()).AcquireAsync(_feed, "Nope", "1.0.0", 7774, CancellationToken.None));
    }

    [Fact]
    public async Task Acquire_OtherFeedTypes_StillGoThroughTheFetcher_AndTheByteArrayChecks()
    {
        var fetcher = new Mock<IPackageContentFetcher>();
        var feed = new ExternalFeed { Id = 1, FeedType = "NuGet", FeedUri = "https://x" };
        var bytes = TestArchives.Zip(("a.txt", "x"));

        fetcher.Setup(f => f.FetchAsync(feed, "p", "1.0.0", It.IsAny<CancellationToken>())).ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), [], bytes));

        var acquisition = new PackageAcquisitionService(fetcher.Object, [new BuiltInPackageFileStreamSource(_packages, new StoreRegistry(new GeneratedStore(1)))]);
        var result = await acquisition.AcquireAsync(feed, "p", "1.0.0", 7775, CancellationToken.None);

        try
        {
            File.ReadAllBytes(result.LocalPath).ShouldBe(bytes);
            fetcher.Verify(f => f.FetchAsync(feed, "p", "1.0.0", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(7775), recursive: true);
        }
    }

    [Fact]
    public async Task Fetch_ReadsOnlyTheKeptFiles_AndLeavesOutOversizedOnesWithAWarning()
    {
        var big = new string('x', (int)BuiltInArchiveFileExtractor.MaxFileBytes + 1);
        var tar = TestArchives.TarGz(("pkg/values.yaml", "a: 1"), ("pkg/huge.yaml", big), ("pkg/bin/app.dll", new string('d', 100_000)), ("pkg/sub/b.json", "{}"));
        var row = _packages.Seed(_feed.Id, "Chart", "1.0.0", TestArchives.Sha256Of(tar));
        var blobs = new InMemoryBlobStore();

        row.SizeBytes = tar.Length;
        row.FileExtension = ".tar.gz";
        blobs.Blobs[row.BlobKey] = tar;

        var result = await new BuiltInFeedPackageSource(_packages, new FakeBlobStoreRegistry(blobs)).FetchAsync(_feed, "Chart", "1.0.0", CancellationToken.None);

        result.Files.Keys.OrderBy(k => k).ShouldBe(["sub/b.json", "values.yaml"]);
        result.Warnings.ShouldContain(w => w.Contains("huge.yaml"));
        result.RawBytes.ShouldBe(tar);
    }
}
