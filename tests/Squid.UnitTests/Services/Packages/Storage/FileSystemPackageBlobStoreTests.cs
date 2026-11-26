using System.IO;
using System.Linq;
using System.Text;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class FileSystemPackageBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-blobs-{Guid.NewGuid():N}");
    private readonly FileSystemPackageBlobStore _store;

    public FileSystemPackageBlobStoreTests()
    {
        _store = new FileSystemPackageBlobStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Put_ThenOpenRead_ReturnsTheSameBytes_ForANestedKey()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();

        await _store.PutAsync("acme/web/1.0.0.zip", new MemoryStream(bytes), CancellationToken.None);

        (await ReadAllAsync("acme/web/1.0.0.zip")).ShouldBe(bytes);
        File.Exists(Path.Combine(_root, "acme", "web", "1.0.0.zip")).ShouldBeTrue("the key spells the path on disk");
    }

    [Fact]
    public async Task Put_OverwritesAnExistingBlob()
    {
        await PutTextAsync("a.zip", "first version, longer content");
        await PutTextAsync("a.zip", "second");

        Encoding.UTF8.GetString(await ReadAllAsync("a.zip")).ShouldBe("second");
    }

    [Fact]
    public async Task Put_WithAStreamThatFails_LeavesNoFileForANewKey_AndNoStagingLeftover()
    {
        await Should.ThrowAsync<IOException>(() => _store.PutAsync("new/blob.zip", new FailingStream(failAfterBytes: 1000), CancellationToken.None));

        (await _store.ExistsAsync("new/blob.zip", CancellationToken.None)).ShouldBeFalse("a partial upload must never be visible as a blob");
        StagingFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Put_WithAStreamThatFails_KeepsTheEarlierBlobIntact()
    {
        await PutTextAsync("keep.zip", "original content");

        await Should.ThrowAsync<IOException>(() => _store.PutAsync("keep.zip", new FailingStream(failAfterBytes: 1000), CancellationToken.None));

        Encoding.UTF8.GetString(await ReadAllAsync("keep.zip")).ShouldBe("original content");
        StagingFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Put_WhenCancelledMidWrite_LeavesNothingBehind()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => _store.PutAsync("c.zip", new MemoryStream(new byte[10]), cts.Token));

        (await _store.ExistsAsync("c.zip", CancellationToken.None)).ShouldBeFalse();
        StagingFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenRead_OfAMissingKey_ThrowsBlobNotFound()
    {
        var ex = await Should.ThrowAsync<PackageBlobNotFoundException>(() => _store.OpenReadAsync("nope/missing.zip", CancellationToken.None));

        ex.Key.ShouldBe("nope/missing.zip");
    }

    [Fact]
    public async Task Exists_ReflectsPutAndDelete()
    {
        (await _store.ExistsAsync("x.zip", CancellationToken.None)).ShouldBeFalse();

        await PutTextAsync("x.zip", "x");
        (await _store.ExistsAsync("x.zip", CancellationToken.None)).ShouldBeTrue();

        await _store.DeleteAsync("x.zip", CancellationToken.None);
        (await _store.ExistsAsync("x.zip", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_OfAMissingBlob_IsANoOp_EvenWhenTheFolderIsMissing()
    {
        await Should.NotThrowAsync(() => _store.DeleteAsync("never/existed.zip", CancellationToken.None));

        await PutTextAsync("sibling.zip", "stays");
        await Should.NotThrowAsync(() => _store.DeleteAsync("other.zip", CancellationToken.None));
        (await _store.ExistsAsync("sibling.zip", CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task ListKeys_ReturnsSlashSeparatedKeys_FilteredByPrefix()
    {
        foreach (var key in new[] { "acme/web/1.0.zip", "acme/web/2.0.zip", "acme/api/1.0.zip", "other/x.zip", "root.zip" })
            await PutTextAsync(key, key);

        (await ListAsync("")).ShouldBe(["acme/api/1.0.zip", "acme/web/1.0.zip", "acme/web/2.0.zip", "other/x.zip", "root.zip"]);
        (await ListAsync("acme/")).ShouldBe(["acme/api/1.0.zip", "acme/web/1.0.zip", "acme/web/2.0.zip"]);
        (await ListAsync("acme/web/")).ShouldBe(["acme/web/1.0.zip", "acme/web/2.0.zip"]);
        (await ListAsync("acme/web/2")).ShouldBe(["acme/web/2.0.zip"]);
        (await ListAsync("ac")).ShouldBe(["acme/api/1.0.zip", "acme/web/1.0.zip", "acme/web/2.0.zip"]);
        (await ListAsync("nothing/")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListKeys_OnAStoreWhoseRootDoesNotExistYet_IsEmpty()
    {
        (await ListAsync("")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListKeys_NeverShowsStagingFiles()
    {
        await PutTextAsync("a.zip", "a");
        Directory.CreateDirectory(Path.Combine(_root, FileSystemBlobKey.StagingFolderName));
        await File.WriteAllTextAsync(Path.Combine(_root, FileSystemBlobKey.StagingFolderName, "half-written"), "partial");

        (await ListAsync("")).ShouldBe(["a.zip"]);
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("/etc/escape.zip")]
    [InlineData("a/../../escape.zip")]
    public async Task UnsafeKeys_AreRefusedByEveryOperation_AndNothingIsWrittenOutside(string key)
    {
        await Should.ThrowAsync<InvalidPackageBlobKeyException>(() => _store.PutAsync(key, new MemoryStream(new byte[3]), CancellationToken.None));
        await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.OpenReadAsync(key, CancellationToken.None));
        await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.ExistsAsync(key, CancellationToken.None));
        await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.DeleteAsync(key, CancellationToken.None));

        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.zip")).ShouldBeFalse();
    }

    [Fact]
    public async Task ListKeys_WithAnUnsafePrefix_IsRefused()
    {
        await Should.ThrowAsync<InvalidPackageBlobKeyException>(() => ListAsync("../"));
    }

    [Fact]
    public async Task Put_SweepsStagingFilesOlderThanTheMaxAge_AndKeepsRecentOnes()
    {
        var old = PlantStagingFile("crashed-write", age: FileSystemPackageBlobStore.StagingMaxAge + TimeSpan.FromHours(1));
        var recent = PlantStagingFile("in-flight-write", age: TimeSpan.FromHours(1));

        await PutTextAsync("a.zip", "a");

        File.Exists(old).ShouldBeFalse("a staging file left by a crash must not stay forever");
        File.Exists(recent).ShouldBeTrue("a recent staging file may belong to a write that is still running");
        (await ListAsync("")).ShouldBe(["a.zip"]);
    }

    [Fact]
    public async Task StagingSweep_RunsAtMostOncePerInterval_PerStore()
    {
        var offset = TimeSpan.Zero;
        var store = new FileSystemPackageBlobStore(_root, () => DateTime.UtcNow + offset);
        var firstOld = PlantStagingFile("old-1", FileSystemPackageBlobStore.StagingMaxAge + TimeSpan.FromHours(1));

        await store.PutAsync("a.zip", new MemoryStream(new byte[] { 1 }), CancellationToken.None);

        File.Exists(firstOld).ShouldBeFalse();

        var secondOld = PlantStagingFile("old-2", FileSystemPackageBlobStore.StagingMaxAge + TimeSpan.FromHours(1));

        await store.PutAsync("b.zip", new MemoryStream(new byte[] { 1 }), CancellationToken.None);

        File.Exists(secondOld).ShouldBeTrue("the sweep ran less than an interval ago, so this write does not repeat it");

        offset = FileSystemPackageBlobStore.StagingSweepInterval + TimeSpan.FromMinutes(1);

        await store.PutAsync("c.zip", new MemoryStream(new byte[] { 1 }), CancellationToken.None);

        File.Exists(secondOld).ShouldBeFalse("an interval later the next write sweeps again");
    }

    [Fact]
    public async Task StagingSweep_NeverTouchesBlobs_EvenOldOnes()
    {
        await PutTextAsync("keep/old.zip", "x");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "keep", "old.zip"), DateTime.UtcNow.AddDays(-400));
        PlantStagingFile("old", TimeSpan.FromDays(3));

        _store.SweepStaging();

        (await _store.ExistsAsync("keep/old.zip", CancellationToken.None)).ShouldBeTrue();
        StagingFiles().ShouldBeEmpty();
    }

    [Fact]
    public void TryFlushDirectory_NeverThrows_ForAFolderOrAMissingOne()
    {
        Directory.CreateDirectory(_root);

        Should.NotThrow(() => FileSystemPackageBlobStore.TryFlushDirectory(_root));
        Should.NotThrow(() => FileSystemPackageBlobStore.TryFlushDirectory(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task ListKeys_HidesTheProbeFolder_InAnyCase_ButNotSimilarlyNamedFolders()
    {
        await PutTextAsync(".squid-probe/abc", "p");
        await PutTextAsync("app/.squid-probe/inner.zip", "x");
        await PutTextAsync(".squid-probex/real.zip", "x");

        (await ListAsync("")).ShouldBe(new[] { "app/.squid-probe/inner.zip", ".squid-probex/real.zip" }.OrderBy(k => k, StringComparer.Ordinal).ToList());
        (await ListAsync(".squid-probe/")).ShouldBeEmpty();
        (await _store.ExistsAsync(".squid-probe/abc", CancellationToken.None)).ShouldBeTrue("hidden from listings, not from direct access: the store test needs it");
    }

    [Fact]
    public async Task OpenRead_OfAKeyThatNamesAFolder_IsBlobNotFound()
    {
        await PutTextAsync("acme/web/1.0.zip", "x");

        await Should.ThrowAsync<PackageBlobNotFoundException>(async () => await _store.OpenReadAsync("acme/web", CancellationToken.None));
        (await _store.ExistsAsync("acme", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task ASymlinkedFolderUnderTheRoot_IsNotFollowed_ByPutOpenExistsOrDelete()
    {
        if (OperatingSystem.IsWindows()) return;

        var outside = Path.Combine(Path.GetTempPath(), $"squid-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.zip"), "outside data");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);

        try
        {
            await Should.ThrowAsync<InvalidPackageBlobKeyException>(() => _store.PutAsync("link/new.zip", new MemoryStream(new byte[] { 1 }), CancellationToken.None));
            await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.OpenReadAsync("link/secret.zip", CancellationToken.None));
            await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.ExistsAsync("link/secret.zip", CancellationToken.None));
            await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.DeleteAsync("link/secret.zip", CancellationToken.None));

            File.Exists(Path.Combine(outside, "new.zip")).ShouldBeFalse("nothing may be written through the link");
            File.ReadAllText(Path.Combine(outside, "secret.zip")).ShouldBe("outside data", "nothing may be deleted through the link");
            (await ListAsync("")).ShouldBeEmpty("a listing does not follow the link either");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ASymlinkedFile_AndADanglingSymlink_UnderTheRoot_AreNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return;

        var outside = Path.Combine(Path.GetTempPath(), $"squid-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "outside data");
        Directory.CreateDirectory(_root);
        File.CreateSymbolicLink(Path.Combine(_root, "file.zip"), outside);
        File.CreateSymbolicLink(Path.Combine(_root, "dangling.zip"), Path.Combine(Path.GetTempPath(), $"squid-missing-{Guid.NewGuid():N}"));

        try
        {
            await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.OpenReadAsync("file.zip", CancellationToken.None));
            await Should.ThrowAsync<InvalidPackageBlobKeyException>(async () => await _store.DeleteAsync("file.zip", CancellationToken.None));
            await Should.ThrowAsync<InvalidPackageBlobKeyException>(() => _store.PutAsync("dangling.zip", new MemoryStream(new byte[] { 1 }), CancellationToken.None));

            File.ReadAllText(outside).ShouldBe("outside data");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task TheRootItselfMayBeASymlink_OnlyWhatIsBeneathItIsChecked()
    {
        if (OperatingSystem.IsWindows()) return;

        var real = Path.Combine(Path.GetTempPath(), $"squid-real-root-{Guid.NewGuid():N}");
        var linkedRoot = Path.Combine(Path.GetTempPath(), $"squid-linked-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(linkedRoot, real);

        try
        {
            var store = new FileSystemPackageBlobStore(linkedRoot);

            await store.PutAsync("a/b.zip", new MemoryStream(new byte[] { 7 }), CancellationToken.None);

            File.Exists(Path.Combine(real, "a", "b.zip")).ShouldBeTrue();
        }
        finally
        {
            File.Delete(linkedRoot);
            Directory.Delete(real, recursive: true);
        }
    }

    private string PlantStagingFile(string name, TimeSpan age)
    {
        var staging = Path.Combine(_root, FileSystemBlobKey.StagingFolderName);
        Directory.CreateDirectory(staging);

        var path = Path.Combine(staging, name);
        File.WriteAllText(path, "partial");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

        return path;
    }

    private async Task PutTextAsync(string key, string text) => await _store.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    private async Task<byte[]> ReadAllAsync(string key)
    {
        await using var stream = await _store.OpenReadAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer);

        return buffer.ToArray();
    }

    private async Task<List<string>> ListAsync(string prefix)
    {
        var keys = new List<string>();

        await foreach (var key in _store.ListKeysAsync(prefix, CancellationToken.None))
            keys.Add(key);

        keys.Sort(StringComparer.Ordinal);

        return keys;
    }

    private string[] StagingFiles()
    {
        var staging = Path.Combine(_root, FileSystemBlobKey.StagingFolderName);

        return Directory.Exists(staging) ? Directory.GetFiles(staging) : [];
    }

    private sealed class FailingStream(int failAfterBytes) : Stream
    {
        private int _served;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served >= failAfterBytes) throw new IOException("the source stream broke");

            var count = Math.Min(buffer.Length, failAfterBytes - _served);
            buffer.Span[..count].Fill(7);
            _served += count;

            return ValueTask.FromResult(count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
