using System.IO;
using System.Text;
using Squid.Core.Services.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportTemporaryUploadStoreTests : IDisposable
{
    private readonly string _rootPath;
    private readonly OctopusImportTemporaryUploadStore _store;

    public OctopusImportTemporaryUploadStoreTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"squid-octopus-import-upload-tests-{Guid.NewGuid():N}");
        _store = new OctopusImportTemporaryUploadStore(new TestTemporaryUploadSettings
        {
            RootPath = _rootPath,
            SecureDeleteBufferBytes = 4096
        });
    }

    [Fact]
    public async Task SaveAsync_WritesUploadInsideSessionDirectory()
    {
        var sessionId = Guid.NewGuid();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("temporary secret export"));

        var upload = await _store.SaveAsync(sessionId, "../export.zip", stream, CancellationToken.None);

        upload.SizeBytes.ShouldBe("temporary secret export".Length);
        File.Exists(upload.Path).ShouldBeTrue();
        Path.GetFullPath(upload.Path).ShouldStartWith(Path.Combine(_rootPath, sessionId.ToString("N")));
        Path.GetFileName(upload.Path).ShouldBe("export.zip");
    }

    [Fact]
    public async Task SecureDeleteAsync_DeletesUploadFileAndSessionDirectory()
    {
        var sessionId = Guid.NewGuid();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("temporary secret export"));
        var upload = await _store.SaveAsync(sessionId, "export.zip", stream, CancellationToken.None);
        var sessionDirectory = Path.GetDirectoryName(upload.Path);

        var result = await _store.SecureDeleteAsync(sessionId, upload.Path, CancellationToken.None);

        result.Deleted.ShouldBeTrue();
        result.FilesDeleted.ShouldBe(1);
        File.Exists(upload.Path).ShouldBeFalse();
        Directory.Exists(sessionDirectory!).ShouldBeFalse();
    }

    [Fact]
    public async Task SecureDeleteAsync_RejectsPathOutsideExpectedSessionDirectory()
    {
        var sessionId = Guid.NewGuid();
        var outsidePath = Path.Combine(_rootPath, "outside.zip");
        Directory.CreateDirectory(_rootPath);
        await File.WriteAllTextAsync(outsidePath, "do not delete", CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _store.SecureDeleteAsync(sessionId, outsidePath, CancellationToken.None));

        File.Exists(outsidePath).ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private sealed class TestTemporaryUploadSettings : IOctopusImportTemporaryUploadSettings
    {
        public string RootPath { get; set; }

        public TimeSpan DefaultRetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        public TimeSpan FailedRetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        public TimeSpan InterruptedImportGracePeriod { get; set; } = TimeSpan.FromHours(6);

        public int CleanupBatchSize { get; set; } = 100;

        public int SecureDeleteBufferBytes { get; set; } = 81920;
    }
}
