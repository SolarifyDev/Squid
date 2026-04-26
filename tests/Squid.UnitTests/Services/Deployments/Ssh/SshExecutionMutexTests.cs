using System.Linq;
using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshExecutionMutexTests
{
    private readonly SshExecutionMutex _mutex = new();

    [Fact]
    public async Task AcquireAsync_FirstCall_ReturnsImmediately()
    {
        using var lockHandle = await _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        lockHandle.ShouldNotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_SameEndpoint_BlocksUntilReleased()
    {
        var lock1 = await _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        var lock2Task = _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(2), CancellationToken.None);

        // Should not complete while lock1 is held
        await Task.Delay(100);
        lock2Task.IsCompleted.ShouldBeFalse();

        lock1.Dispose();

        // Should now complete
        var lock2 = await lock2Task;
        lock2.ShouldNotBeNull();
        lock2.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_DifferentEndpoints_DoNotBlock()
    {
        using var lock1 = await _mutex.AcquireAsync("host1", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var lock2 = await _mutex.AcquireAsync("host2", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        lock1.ShouldNotBeNull();
        lock2.ShouldNotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_DifferentPorts_DoNotBlock()
    {
        using var lock1 = await _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var lock2 = await _mutex.AcquireAsync("host", 2222, TimeSpan.FromSeconds(5), CancellationToken.None);

        lock1.ShouldNotBeNull();
        lock2.ShouldNotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_Timeout_ThrowsTimeoutException()
    {
        using var lock1 = await _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Should.ThrowAsync<TimeoutException>(() =>
            _mutex.AcquireAsync("host", 22, TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    [Fact]
    public async Task AcquireAsync_Cancellation_ThrowsOperationCanceled()
    {
        using var lock1 = await _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        using var cts = new CancellationTokenSource(50);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task AcquireAsync_CaseInsensitive_SameEndpoint()
    {
        var lock1 = await _mutex.AcquireAsync("Host.Example.COM", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        var lock2Task = _mutex.AcquireAsync("host.example.com", 22, TimeSpan.FromSeconds(1), CancellationToken.None);

        await Task.Delay(100);
        lock2Task.IsCompleted.ShouldBeFalse();

        lock1.Dispose();

        var lock2 = await lock2Task;
        lock2.ShouldNotBeNull();
        lock2.Dispose();
    }

    [Fact]
    public async Task Dispose_MultipleTimesIsSafe()
    {
        var lockHandle = await _mutex.AcquireAsync("host", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        lockHandle.Dispose();
        lockHandle.Dispose(); // should not throw or double-release
    }

    // ── P1-N3 (Phase-7): refcount-driven eviction ─────────────────────────────
    //
    // Pre-fix: every unique host:port added a SemaphoreSlim that lived for the
    // singleton service's lifetime. On a long-running server with churning
    // ephemeral SSH targets (k8s pods, ephemeral VMs), this leaked one
    // SemaphoreSlim per ever-seen endpoint. Slow but unbounded — visible by
    // week 2-3 of uptime via metrics.
    //
    // Fix: refcount each entry; remove + dispose when the last caller releases.
    // Pinned by these tests on the LockCount internal accessor.

    [Fact]
    public async Task LockCount_AfterAcquireAndDispose_ReturnsToZero()
    {
        var mutex = new SshExecutionMutex();
        mutex.LockCount.ShouldBe(0);

        var handle = await mutex.AcquireAsync("host-a", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        mutex.LockCount.ShouldBe(1, customMessage: "while held, the entry must remain in the dict.");

        handle.Dispose();
        mutex.LockCount.ShouldBe(0,
            customMessage: "after release with no waiters, the entry must be evicted — pre-fix it leaked forever.");
    }

    [Fact]
    public async Task LockCount_ManyDistinctEndpoints_AllEvicted()
    {
        var mutex = new SshExecutionMutex();

        // Simulate a churning ephemeral-target environment: 100 unique hosts.
        var handles = new List<IDisposable>();
        for (var i = 0; i < 100; i++)
            handles.Add(await mutex.AcquireAsync($"host-{i}", 22, TimeSpan.FromSeconds(5), CancellationToken.None));

        mutex.LockCount.ShouldBe(100);

        foreach (var h in handles) h.Dispose();

        mutex.LockCount.ShouldBe(0,
            customMessage: "100 unique endpoints fully released → 0 entries left. Pre-fix this would stay at 100 forever.");
    }

    [Fact]
    public async Task LockCount_WhileWaiterIsQueued_StaysAtOne_ThenZeroAfterAllRelease()
    {
        var mutex = new SshExecutionMutex();

        var holder = await mutex.AcquireAsync("contended", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        var waiterTask = mutex.AcquireAsync("contended", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Task.Delay(50);
        mutex.LockCount.ShouldBe(1, customMessage: "one entry while one holder + one waiter — entry is shared.");

        holder.Dispose();
        var waiter = await waiterTask;

        // Waiter now holds; entry must STILL be present.
        mutex.LockCount.ShouldBe(1, customMessage: "waiter took ownership; entry still alive.");

        waiter.Dispose();
        mutex.LockCount.ShouldBe(0, customMessage: "all released → entry evicted.");
    }

    [Fact]
    public async Task LockCount_TimeoutRelease_StillEvicts()
    {
        // A waiter that times out must drop its ref so the entry can be
        // evicted when the holder eventually releases.
        var mutex = new SshExecutionMutex();

        var holder = await mutex.AcquireAsync("contended", 22, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await mutex.AcquireAsync("contended", 22, TimeSpan.FromMilliseconds(100), CancellationToken.None));

        // Holder still holds; one entry.
        mutex.LockCount.ShouldBe(1);

        holder.Dispose();

        mutex.LockCount.ShouldBe(0,
            customMessage: "timeout-then-release path must still evict — both refs were dropped.");
    }

    [Fact]
    public async Task LockCount_ConcurrentAcquireRelease_NoDictionaryLeak()
    {
        // Hammer the eviction race window — many threads concurrently
        // acquiring + releasing different endpoints. Final state: zero.
        var mutex = new SshExecutionMutex();

        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            for (var j = 0; j < 20; j++)
            {
                using var h = await mutex.AcquireAsync($"host-{i}", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
                await Task.Yield();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        mutex.LockCount.ShouldBe(0,
            customMessage: "concurrent acquire+release across 50 endpoints × 20 iters must leave dict empty.");
    }
}
