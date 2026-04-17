using Shouldly;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class ScriptStateFileAtomicityTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-state-file-{Guid.NewGuid():N}");

    public ScriptStateFileAtomicityTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var state = new ScriptStateFile
        {
            TicketId = "t1",
            PodName = "squid-script-t1",
            EosMarkerToken = "marker-abc",
            Isolation = "FullIsolation",
            IsolationMutexName = "mutex-a",
            Namespace = "prod",
            CreatedAt = DateTimeOffset.Parse("2026-04-17T08:00:00Z")
        };

        ScriptStateFile.Write(_workspace, state);
        var loaded = ScriptStateFile.TryRead(_workspace);

        loaded.ShouldNotBeNull();
        loaded!.TicketId.ShouldBe("t1");
        loaded.PodName.ShouldBe("squid-script-t1");
        loaded.EosMarkerToken.ShouldBe("marker-abc");
        loaded.Namespace.ShouldBe("prod");
    }

    [Fact]
    public void SecondWrite_CreatesBackupOfPriorVersion()
    {
        ScriptStateFile.Write(_workspace, new ScriptStateFile { TicketId = "t1", PodName = "pod-v1" });
        ScriptStateFile.Write(_workspace, new ScriptStateFile { TicketId = "t1", PodName = "pod-v2" });

        var primaryPath = ScriptStateFile.GetPath(_workspace);
        File.Exists(primaryPath + ".bak").ShouldBeTrue("atomic replace must keep a .bak of the prior version");
    }

    [Fact]
    public void PrimaryCorrupted_RecoversFromBackup()
    {
        ScriptStateFile.Write(_workspace, new ScriptStateFile { TicketId = "t1", PodName = "pod-v1" });
        ScriptStateFile.Write(_workspace, new ScriptStateFile { TicketId = "t1", PodName = "pod-v2" });

        // Simulate corruption of the primary by truncating to garbage.
        var primaryPath = ScriptStateFile.GetPath(_workspace);
        File.WriteAllText(primaryPath, "{ not valid json");

        var loaded = ScriptStateFile.TryRead(_workspace);

        loaded.ShouldNotBeNull();
        // Backup holds the first version (before pod-v2 replaced it).
        loaded!.PodName.ShouldBe("pod-v1");
    }

    [Fact]
    public void MidWriteCrash_TempFileDoesNotCorruptState()
    {
        // A crash that left behind {path}.tmp must not prevent normal reads.
        ScriptStateFile.Write(_workspace, new ScriptStateFile { TicketId = "t1", PodName = "good" });
        File.WriteAllText(ScriptStateFile.GetPath(_workspace) + ".tmp", "partial-write-garbage");

        var loaded = ScriptStateFile.TryRead(_workspace);

        loaded.ShouldNotBeNull();
        loaded!.PodName.ShouldBe("good");
    }

    [Fact]
    public void NoFile_ReturnsNull()
    {
        var loaded = ScriptStateFile.TryRead(_workspace);

        loaded.ShouldBeNull();
    }

    [Fact]
    public void ConcurrentWrites_NoCorruption()
    {
        ScriptStateFile.Write(_workspace, new ScriptStateFile { TicketId = "t1", PodName = "initial" });

        // 200 parallel writers to the same target path — heavy enough to reliably
        // surface the Linux File.Replace race that earlier iterations of this test
        // caught on CI.
        Parallel.For(0, 200, i =>
        {
            ScriptStateFile.Write(_workspace, new ScriptStateFile
            {
                TicketId = "t1",
                PodName = $"pod-{i}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        });

        var loaded = ScriptStateFile.TryRead(_workspace);
        loaded.ShouldNotBeNull();
        loaded!.TicketId.ShouldBe("t1");
        loaded.PodName.ShouldStartWith("pod-");
    }
}
