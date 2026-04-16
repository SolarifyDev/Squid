using System.IO;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Tests.Platform;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void WriteAllText_CreatesFileWithContent()
    {
        var path = Path.Combine(_tempDir, "test.json");

        AtomicFileWriter.WriteAllText(path, "{\"key\":\"value\"}");

        File.Exists(path).ShouldBeTrue();
        File.ReadAllText(path).ShouldBe("{\"key\":\"value\"}");
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir, "test.json");
        File.WriteAllText(path, "old");

        AtomicFileWriter.WriteAllText(path, "new");

        File.ReadAllText(path).ShouldBe("new");
    }

    [Fact]
    public void WriteAllText_CreatesParentDirectories()
    {
        var path = Path.Combine(_tempDir, "nested", "deep", "file.json");

        AtomicFileWriter.WriteAllText(path, "ok");

        File.ReadAllText(path).ShouldBe("ok");
    }

    [Fact]
    public void WriteAllText_NoTempFileLeftBehind()
    {
        var path = Path.Combine(_tempDir, "clean.json");

        AtomicFileWriter.WriteAllText(path, "data");

        var filesInDir = Directory.GetFiles(_tempDir);
        filesInDir.Length.ShouldBe(1, "Only the target file should exist — no .tmp leftovers");
        filesInDir[0].ShouldEndWith("clean.json");
    }

    [Fact]
    public void WriteAllTextRestricted_SetsRestrictedPermissionsOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_tempDir, "restricted.json");

        AtomicFileWriter.WriteAllTextRestricted(path, "secret");

        var mode = File.GetUnixFileMode(path);
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
