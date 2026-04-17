using Shouldly;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution.State;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class WorkspaceLockTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-lock-test-{Guid.NewGuid():N}");

    public WorkspaceLockTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void TryAcquire_EmptyWorkspace_Succeeds()
    {
        using var @lock = WorkspaceLock.TryAcquire(_workspace);

        @lock.ShouldNotBeNull();
    }

    [Fact]
    public void TryAcquire_TwoTimes_SecondFails()
    {
        using var first = WorkspaceLock.TryAcquire(_workspace);
        var second = WorkspaceLock.TryAcquire(_workspace);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public void TryAcquire_AfterDispose_Succeeds()
    {
        var first = WorkspaceLock.TryAcquire(_workspace);
        first!.Dispose();

        using var second = WorkspaceLock.TryAcquire(_workspace);

        second.ShouldNotBeNull();
    }

    [Fact]
    public void Acquire_WithTimeout_ReleasedMidWait_Succeeds()
    {
        var first = WorkspaceLock.TryAcquire(_workspace);
        first.ShouldNotBeNull();

        var releaseAfter = TimeSpan.FromMilliseconds(150);
        Task.Run(async () =>
        {
            await Task.Delay(releaseAfter);
            first!.Dispose();
        });

        using var second = WorkspaceLock.Acquire(_workspace, TimeSpan.FromSeconds(2));

        second.ShouldNotBeNull();
    }

    [Fact]
    public void Acquire_PermanentlyHeld_ThrowsTimeout()
    {
        using var first = WorkspaceLock.TryAcquire(_workspace);
        first.ShouldNotBeNull();

        Should.Throw<TimeoutException>(() =>
            WorkspaceLock.Acquire(_workspace, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void TryAcquire_WorkspaceDoesNotExist_CreatesAndLocks()
    {
        var nested = Path.Combine(_workspace, "nested", "child");

        using var @lock = WorkspaceLock.TryAcquire(nested);

        @lock.ShouldNotBeNull();
        Directory.Exists(nested).ShouldBeTrue();
    }
}
