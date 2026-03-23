using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class PodLogEncryptionKeyManagerTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly byte[] TestMachineKey = System.Text.Encoding.UTF8.GetBytes("test-machine-key-for-unit-tests!");

    public PodLogEncryptionKeyManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-key-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void WriteKey_CreatesFile()
    {
        PodLogEncryptionKeyManager.WriteKeyToWorkspace(_tempDir, TestMachineKey, "ticket-1");

        File.Exists(Path.Combine(_tempDir, ".squid-log-key")).ShouldBeTrue();
    }

    [Fact]
    public void ReadKey_RoundTrips()
    {
        PodLogEncryptionKeyManager.WriteKeyToWorkspace(_tempDir, TestMachineKey, "ticket-1");
        var key = PodLogEncryptionKeyManager.ReadKeyFromWorkspace(_tempDir);

        key.ShouldNotBeNull();
        key.Length.ShouldBe(32);

        var expected = PodLogEncryption.DeriveLogEncryptionKey(TestMachineKey, "ticket-1");
        key.ShouldBe(expected);
    }

    [Fact]
    public void ReadKey_MissingFile_ReturnsNull()
    {
        var key = PodLogEncryptionKeyManager.ReadKeyFromWorkspace(_tempDir);

        key.ShouldBeNull();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }
}
