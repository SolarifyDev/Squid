using Shouldly;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution.State;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class ScriptStateStoreTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-state-test-{Guid.NewGuid():N}");

    public ScriptStateStoreTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Exists_EmptyWorkspace_ReturnsFalse()
    {
        var store = new ScriptStateStore(_workspace);

        store.Exists().ShouldBeFalse();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFaithfully()
    {
        var store = new ScriptStateStore(_workspace);
        var state = new ScriptState
        {
            TicketId = "ticket-abc",
            Progress = ScriptProgress.Running,
            NextLogSequence = 42,
            CreatedAt = DateTimeOffset.Parse("2026-04-17T12:00:00Z"),
            StartedAt = DateTimeOffset.Parse("2026-04-17T12:00:05Z"),
            ProcessId = 9876,
            ProcessOwnerSignature = "agent-instance-1"
        };

        store.Save(state);
        var loaded = store.Load();

        loaded.TicketId.ShouldBe("ticket-abc");
        loaded.Progress.ShouldBe(ScriptProgress.Running);
        loaded.NextLogSequence.ShouldBe(42);
        loaded.CreatedAt.ShouldBe(DateTimeOffset.Parse("2026-04-17T12:00:00Z"));
        loaded.StartedAt.ShouldBe(DateTimeOffset.Parse("2026-04-17T12:00:05Z"));
        loaded.ProcessId.ShouldBe(9876);
        loaded.ProcessOwnerSignature.ShouldBe("agent-instance-1");
    }

    [Fact]
    public void Save_SecondWrite_UpdatesState_AndCreatesBackup()
    {
        var store = new ScriptStateStore(_workspace);

        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Pending });
        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Running, NextLogSequence = 7 });

        var loaded = store.Load();
        loaded.Progress.ShouldBe(ScriptProgress.Running);
        loaded.NextLogSequence.ShouldBe(7);

        File.Exists(Path.Combine(_workspace, "scriptstate.json.bak")).ShouldBeTrue();
    }

    [Fact]
    public void Load_PrimaryCorrupted_RecoversFromBackup()
    {
        var store = new ScriptStateStore(_workspace);
        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Pending });
        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Running, NextLogSequence = 99 });

        // Simulate mid-write corruption by truncating the primary file to garbage
        File.WriteAllText(Path.Combine(_workspace, "scriptstate.json"), "{ this is no longer valid json");

        var loaded = store.Load();

        // Backup still holds the last-good state (Pending, NextLogSequence=0)
        loaded.Progress.ShouldBe(ScriptProgress.Pending);
        loaded.NextLogSequence.ShouldBe(0);
    }

    [Fact]
    public void Load_NoState_Throws()
    {
        var store = new ScriptStateStore(_workspace);

        Should.Throw<InvalidOperationException>(() => store.Load());
    }

    [Fact]
    public void Delete_RemovesAllStateFiles()
    {
        var store = new ScriptStateStore(_workspace);
        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Running });
        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Complete });

        store.Delete();

        store.Exists().ShouldBeFalse();
        File.Exists(Path.Combine(_workspace, "scriptstate.json")).ShouldBeFalse();
        File.Exists(Path.Combine(_workspace, "scriptstate.json.bak")).ShouldBeFalse();
    }

    [Fact]
    public void Save_CrashMidWrite_PrimaryOrBackupRemainsValid()
    {
        // Simulate: write completes to tmp, then the atomic replace is interrupted.
        // We do this by manually placing a half-written tmp file + existing primary,
        // then verifying Load() still returns the last-good primary.
        var store = new ScriptStateStore(_workspace);
        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Running, NextLogSequence = 10 });

        File.WriteAllText(Path.Combine(_workspace, "scriptstate.json.tmp"), "{broken partial");

        var loaded = store.Load();

        loaded.Progress.ShouldBe(ScriptProgress.Running);
        loaded.NextLogSequence.ShouldBe(10);
    }

    [Fact]
    public void Save_ConcurrentWrites_NoCorruption()
    {
        var store = new ScriptStateStore(_workspace);
        store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Pending });

        Parallel.For(0, 50, i =>
        {
            store.Save(new ScriptState { TicketId = "t1", Progress = ScriptProgress.Running, NextLogSequence = i });
        });

        var loaded = store.Load();
        loaded.Progress.ShouldBe(ScriptProgress.Running);
        loaded.NextLogSequence.ShouldBeInRange(0, 49);
    }

    [Fact]
    public void HasStarted_ReflectsProgressCorrectly()
    {
        new ScriptState { Progress = ScriptProgress.Pending }.HasStarted().ShouldBeFalse();
        new ScriptState { Progress = ScriptProgress.Starting }.HasStarted().ShouldBeTrue();
        new ScriptState { Progress = ScriptProgress.Running }.HasStarted().ShouldBeTrue();
        new ScriptState { Progress = ScriptProgress.Complete }.HasStarted().ShouldBeTrue();
    }
}
