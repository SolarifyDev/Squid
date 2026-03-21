using System;
using System.IO;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class ScriptStateFileTests : IDisposable
{
    private readonly string _tempDir;

    public ScriptStateFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-state-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void WriteAndRead_RoundTrip()
    {
        var state = new ScriptStateFile
        {
            TicketId = "abc123",
            PodName = "squid-script-abc123",
            EosMarkerToken = "marker-token-xyz",
            Isolation = "FullIsolation",
            IsolationMutexName = "deploy-mutex",
            CreatedAt = new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero)
        };

        ScriptStateFile.Write(_tempDir, state);
        var loaded = ScriptStateFile.TryRead(_tempDir);

        loaded.ShouldNotBeNull();
        loaded!.TicketId.ShouldBe("abc123");
        loaded.PodName.ShouldBe("squid-script-abc123");
        loaded.EosMarkerToken.ShouldBe("marker-token-xyz");
        loaded.Isolation.ShouldBe("FullIsolation");
        loaded.IsolationMutexName.ShouldBe("deploy-mutex");
        loaded.CreatedAt.ShouldBe(new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        var result = ScriptStateFile.TryRead(_tempDir);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryRead_CorruptJson_ReturnsNull()
    {
        var path = ScriptStateFile.GetPath(_tempDir);
        File.WriteAllText(path, "{ this is not valid json }}}");

        var result = ScriptStateFile.TryRead(_tempDir);

        result.ShouldBeNull();
    }

    [Fact]
    public void TryRead_NonExistentDirectory_ReturnsNull()
    {
        var result = ScriptStateFile.TryRead(Path.Combine(_tempDir, "nonexistent"));

        result.ShouldBeNull();
    }

    [Fact]
    public void WriteAndRead_NullMutexName_RoundTrips()
    {
        var state = new ScriptStateFile
        {
            TicketId = "ticket1",
            PodName = "pod1",
            EosMarkerToken = "token1",
            Isolation = "NoIsolation",
            IsolationMutexName = null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        ScriptStateFile.Write(_tempDir, state);
        var loaded = ScriptStateFile.TryRead(_tempDir);

        loaded.ShouldNotBeNull();
        loaded!.IsolationMutexName.ShouldBeNull();
    }

    [Fact]
    public void GetPath_ReturnsCorrectFileName()
    {
        var path = ScriptStateFile.GetPath("/some/workspace/ticket123");

        path.ShouldBe("/some/workspace/ticket123/.squid-state.json");
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
