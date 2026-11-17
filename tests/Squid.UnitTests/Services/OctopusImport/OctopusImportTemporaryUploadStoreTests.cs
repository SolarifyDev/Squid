using System.IO;
using System.Text;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.UnitTests.Services.OctopusImport.SessionFiles;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportTemporaryUploadStoreTests : IDisposable
{
    private readonly string _rootPath;
    private readonly string _otherServerRoot;
    private readonly InMemoryOctopusImportSessionFileStore _files = new();
    private readonly OctopusImportTemporaryUploadStore _store;

    public OctopusImportTemporaryUploadStoreTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"squid-octopus-import-upload-tests-{Guid.NewGuid():N}");
        _otherServerRoot = Path.Combine(Path.GetTempPath(), $"squid-octopus-import-upload-tests-{Guid.NewGuid():N}");
        _store = StoreOn(_rootPath);
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

    [Theory]
    [InlineData("octopus-history.zip", "upload-octopus-history.zip")]
    [InlineData("OCTOPUS-HISTORY.ZIP", "upload-OCTOPUS-HISTORY.ZIP")]
    [InlineData("octopus-history-2024.zip", "octopus-history-2024.zip")]
    public async Task SaveAsync_NeverTakesTheReservedHistoryBundleName(string uploadedName, string storedName)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("export"));

        var upload = await _store.SaveAsync(Guid.NewGuid(), uploadedName, stream, CancellationToken.None);

        Path.GetFileName(upload.Path).ShouldBe(storedName);
    }

    // ".." inside a name is an ordinary file name: the upload is stored with the session and any server reads it back.
    [Fact]
    public async Task SaveAsync_DotsInsideTheName_StoredAndReadBackElsewhere()
    {
        var sessionId = Guid.NewGuid();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("export"));

        var upload = await _store.SaveAsync(sessionId, "Octopus..export.zip", stream, CancellationToken.None);
        var elsewhere = await StoreOn(_otherServerRoot).MaterializeAsync(new OctopusImportSession { SessionId = sessionId, TemporaryUploadPath = upload.Path });

        Path.GetFileName(upload.Path).ShouldBe("Octopus..export.zip");
        (await File.ReadAllTextAsync(elsewhere)).ShouldBe("export");
    }

    // The export holds secrets: when it cannot be stored with the session, this server's copy is not left behind for nothing to remove.
    [Fact]
    public async Task SaveAsync_StoreFails_LeavesNoCopyOnDisk()
    {
        var sessionId = Guid.NewGuid();
        var failing = new Mock<IOctopusImportSessionFileStore>();
        failing.Setup(f => f.WriteAsync(sessionId, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<OctopusImportSessionFileEncoding>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database unavailable"));
        var store = new OctopusImportTemporaryUploadStore(Settings(_rootPath), failing.Object);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("password=secret"));

        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveAsync(sessionId, "export.zip", stream, CancellationToken.None));

        Directory.Exists(store.GetSessionDirectory(sessionId)).ShouldBeFalse();
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
        result.FilesDeleted.ShouldBe(2, "the upload and the hash recorded beside it");
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

    // ── Stored with the session; this server's disk only caches it ─────────

    [Fact]
    public async Task SaveAsync_StoresTheUploadWithTheSession()
    {
        var sessionId = Guid.NewGuid();

        var upload = await SaveAsync(sessionId, "export payload");

        (await _files.ReadAsync(sessionId, "export.zip")).ShouldBe(Encoding.UTF8.GetBytes("export payload"));
        (await _files.GetHeaderAsync(sessionId, "export.zip")).Sha256.ShouldBe(upload.Sha256);
    }

    // The server that took the upload uses its own copy: the hash beside it matches the stored one, so nothing is read back.
    [Fact]
    public async Task Materialize_LocalCopyCurrent_UsedWithoutReadingTheStoredCopy()
    {
        var session = await UploadedSessionAsync("export payload");

        var path = await _store.MaterializeAsync(session);

        path.ShouldBe(session.TemporaryUploadPath);
        _files.Reads.ShouldBe(0);
    }

    // Another server (its own RootPath, never the registered path) writes its copy from the stored one; so does a server whose copy is gone or stale.
    [Theory]
    [InlineData("other-server")]
    [InlineData("deleted")]
    [InlineData("stale")]
    [InlineData("hash-file-lost-and-content-changed")]
    public async Task Materialize_NoCurrentLocalCopy_WrittenFromTheStoredCopy(string situation)
    {
        var session = await UploadedSessionAsync("export payload");
        var store = situation == "other-server" ? StoreOn(_otherServerRoot) : _store;

        if (situation == "deleted") File.Delete(session.TemporaryUploadPath);
        if (situation == "stale")
        {
            await File.WriteAllTextAsync(session.TemporaryUploadPath, "older  payload");
            await File.WriteAllTextAsync(session.TemporaryUploadPath + OctopusImportTemporaryUploadStore.HashSuffix, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("older  payload"))).ToLowerInvariant());
        }
        if (situation == "hash-file-lost-and-content-changed")
        {
            File.Delete(session.TemporaryUploadPath + OctopusImportTemporaryUploadStore.HashSuffix);
            await File.WriteAllTextAsync(session.TemporaryUploadPath, "EXPORT PAYLOAD");
        }

        var path = await store.MaterializeAsync(session);

        path.ShouldBe(store.GetLocalPath(session.SessionId, session.TemporaryUploadPath));
        (await File.ReadAllTextAsync(path)).ShouldBe("export payload");
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldNotContain(f => f.EndsWith(".partial"));
    }

    // A stored copy that cannot be read back intact (another master key, damaged bytes) is no source: null, and nothing half-written is left.
    [Fact]
    public async Task Materialize_StoredCopyUnreadable_NullAndNoPartialFileLeft()
    {
        var session = await UploadedSessionAsync("export payload");
        var broken = new Mock<IOctopusImportSessionFileStore>();
        broken.Setup(f => f.GetHeaderAsync(session.SessionId, "export.zip", It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionFileHeader("export.zip", 14, new string('a', 64), 1, "identity", "aes-gcm-v1"));
        broken.Setup(f => f.CopyToAsync(session.SessionId, "export.zip", It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(async (Guid _, string _, Stream destination, CancellationToken ct) =>
        {
            await destination.WriteAsync(Encoding.UTF8.GetBytes("half"), ct);
            throw new OctopusImportSessionFileUnreadableException();
        });
        var store = new OctopusImportTemporaryUploadStore(Settings(_otherServerRoot), broken.Object);

        (await store.MaterializeAsync(session)).ShouldBeNull();
        Directory.Exists(store.GetSessionDirectory(session.SessionId)).ShouldBeTrue();
        Directory.GetFiles(store.GetSessionDirectory(session.SessionId)).ShouldBeEmpty();
    }

    // A session uploaded before files were stored with sessions has no stored copy: the server that took it still uses its own file.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Materialize_NoStoredCopy_RegisteredLocalPathOrNothing(bool localFileExists)
    {
        var sessionId = Guid.NewGuid();
        var legacyPath = Path.Combine(_rootPath, sessionId.ToString("N"), "legacy.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        if (localFileExists) await File.WriteAllTextAsync(legacyPath, "legacy");

        var path = await _store.MaterializeAsync(new OctopusImportSession { SessionId = sessionId, TemporaryUploadPath = legacyPath });

        path.ShouldBe(localFileExists ? legacyPath : null);
    }

    [Fact]
    public async Task Materialize_SessionWithoutAnUpload_Null()
        => (await _store.MaterializeAsync(new OctopusImportSession { SessionId = Guid.NewGuid() })).ShouldBeNull();

    [Fact]
    public void HashSuffix_Pinned() => OctopusImportTemporaryUploadStore.HashSuffix.ShouldBe(".sha256");

    public void Dispose()
    {
        foreach (var root in new[] { _rootPath, _otherServerRoot }.Where(Directory.Exists)) Directory.Delete(root, recursive: true);
    }

    private OctopusImportTemporaryUploadStore StoreOn(string root) => new(Settings(root), _files);

    private static TestTemporaryUploadSettings Settings(string root) => new() { RootPath = root, SecureDeleteBufferBytes = 4096 };

    private async Task<OctopusImportTemporaryUpload> SaveAsync(Guid sessionId, string content)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        return await _store.SaveAsync(sessionId, "export.zip", stream, CancellationToken.None);
    }

    private async Task<OctopusImportSession> UploadedSessionAsync(string content)
    {
        var sessionId = Guid.NewGuid();
        var upload = await SaveAsync(sessionId, content);

        return new OctopusImportSession { SessionId = sessionId, TemporaryUploadPath = upload.Path };
    }

    private sealed class TestTemporaryUploadSettings : IOctopusImportTemporaryUploadSettings
    {
        public string RootPath { get; set; }

        public TimeSpan DefaultRetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        public TimeSpan FailedRetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        public TimeSpan InterruptedImportGracePeriod { get; set; } = TimeSpan.FromHours(6);

        public TimeSpan MaxSessionAge { get; set; } = TimeSpan.FromHours(168);

        public int CleanupBatchSize { get; set; } = 100;

        public int SecureDeleteBufferBytes { get; set; } = 81920;
    }
}
