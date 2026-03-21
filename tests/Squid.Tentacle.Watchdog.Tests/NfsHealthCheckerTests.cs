using Squid.Tentacle.Watchdog;

namespace Squid.Tentacle.Watchdog.Tests;

public class NfsHealthCheckerTests : IDisposable
{
    private readonly string _tempDir;

    public NfsHealthCheckerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-watchdog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CheckFilesystem_ValidDirectory_ReturnsTrue()
    {
        NfsHealthChecker.CheckFilesystem(_tempDir).ShouldBeTrue();
    }

    [Fact]
    public void CheckFilesystem_NonexistentDirectory_ReturnsTrue()
    {
        // Non-NFS errors are ignored (aligned with Octopus behavior)
        var nonExistent = Path.Combine(_tempDir, "nonexistent-subdir", "deep");

        NfsHealthChecker.CheckFilesystem(nonExistent).ShouldBeTrue();
    }

    [Fact]
    public void IsCorruptedMount_StaleHandle_ReturnsTrue()
    {
        // ESTALE = 116
        var ex = CreateIOExceptionWithErrno(116);

        NfsHealthChecker.IsCorruptedMount(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsCorruptedMount_IoError_ReturnsTrue()
    {
        // EIO = 5
        var ex = CreateIOExceptionWithErrno(5);

        NfsHealthChecker.IsCorruptedMount(ex).ShouldBeTrue();
    }

    [Fact]
    public void IsCorruptedMount_NormalException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("not an NFS error");

        NfsHealthChecker.IsCorruptedMount(ex).ShouldBeFalse();
    }

    private static IOException CreateIOExceptionWithErrno(int errno)
    {
        // On Linux, .NET encodes errno as 0x80070000 | errno
        var ex = new IOException("simulated NFS error");
        ex.HResult = unchecked((int)0x80070000) | errno;
        return ex;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
