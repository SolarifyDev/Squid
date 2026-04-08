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
}
