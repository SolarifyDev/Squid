using System.IO;
using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.SessionFiles;

namespace Squid.UnitTests.Services.OctopusImport.SessionFiles;

public sealed class OctopusImportLocalCacheSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-cache-sweep-{Guid.NewGuid():N}");
    private readonly InMemoryOctopusImportSessionFileStore _files = new();

    // Another server cleaned the session up: this server's copy (an export, possibly with secrets) goes too. A session still open keeps
    // its copy; a directory of a session this database does not know (another Squid on the same temporary root) or not named by a
    // session id is never touched.
    [Fact]
    public async Task Sweep_RemovesOnlyDirectoriesOfCleanedUpSessions()
    {
        var cleaned = SessionDirectory(Guid.NewGuid());
        var open = SessionDirectory(Guid.NewGuid());
        var unknown = SessionDirectory(Guid.NewGuid());
        var notASession = Directory.CreateDirectory(Path.Combine(_root, "keep-me")).FullName;

        _files.Purge(Guid.ParseExact(Path.GetFileName(cleaned), "N"));
        await _files.WriteAsync(Guid.ParseExact(Path.GetFileName(open), "N"), "export.zip", new MemoryStream([1]), Squid.Message.Enums.OctopusImport.OctopusImportSessionFileEncoding.Identity);

        var removed = await Sweep().SweepAsync();

        removed.ShouldBe(1);
        Directory.Exists(cleaned).ShouldBeFalse();
        new[] { open, unknown, notASession }.ShouldAllBe(d => Directory.Exists(d));
    }

    [Fact]
    public async Task Sweep_NoRootYet_RemovesNothing()
        => (await new OctopusImportLocalCacheSweep(Settings(Path.Combine(_root, "missing")), Uploads(Path.Combine(_root, "missing")), _files).SweepAsync()).ShouldBe(0);

    [Fact]
    public void Interval_Pinned() => OctopusImportLocalCacheSweeper.Interval.ShouldBe(TimeSpan.FromMinutes(15));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private OctopusImportLocalCacheSweep Sweep() => new(Settings(_root), Uploads(_root), _files);

    private OctopusImportTemporaryUploadStore Uploads(string root) => new(Settings(root), _files);

    private string SessionDirectory(Guid sessionId)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, sessionId.ToString("N"))).FullName;

        File.WriteAllText(Path.Combine(directory, "export.zip"), "cached");

        return directory;
    }

    private static IOctopusImportTemporaryUploadSettings Settings(string root)
    {
        var settings = new Mock<IOctopusImportTemporaryUploadSettings>();

        settings.SetupGet(s => s.RootPath).Returns(root);
        settings.SetupGet(s => s.SecureDeleteBufferBytes).Returns(4096);

        return settings.Object;
    }
}
