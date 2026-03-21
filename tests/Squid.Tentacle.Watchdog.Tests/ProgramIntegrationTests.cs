using Moq;
using Squid.Tentacle.Watchdog;

namespace Squid.Tentacle.Watchdog.Tests;

public class ProgramIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ProgramIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-watchdog-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task MainLoop_HealthyDirectory_NeverTerminates()
    {
        var terminatorMock = new Mock<IPodTerminator>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        _ = Task.Run(async () =>
        {
            await RunWatchdogLoopAsync(_tempDir, terminatorMock.Object, loopSeconds: 0.5, initialBackoff: 0.1, timeout: 1, cts.Token);
        });

        await Task.Delay(1800);

        terminatorMock.Verify(t => t.TerminateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MainLoop_CorruptedMount_TerminatesAfterTimeout()
    {
        var terminatorMock = new Mock<IPodTerminator>();
        terminatorMock.Setup(t => t.TerminateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // We need a check function that simulates NFS corruption (returns false)
        // The actual NfsHealthChecker returns true for non-existent dirs (non-NFS error)
        // So we test the loop logic directly with a failing check
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await RunWatchdogLoopWithCheckAsync(
            () => false, // Simulates corrupted NFS mount
            terminatorMock.Object,
            loopSeconds: 0.5, initialBackoff: 0.1, timeout: 0.5,
            cts.Token);

        terminatorMock.Verify(t => t.TerminateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MainLoop_CancellationStopsGracefully()
    {
        var terminatorMock = new Mock<IPodTerminator>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var task = RunWatchdogLoopAsync(_tempDir, terminatorMock.Object, loopSeconds: 0.2, initialBackoff: 0.1, timeout: 1, cts.Token);

        // Should complete without throwing (OperationCanceledException is caught)
        await Should.NotThrowAsync(async () => await task);

        terminatorMock.Verify(t => t.TerminateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Extracted watchdog loop logic for testability
    private static async Task RunWatchdogLoopAsync(string directory, IPodTerminator terminator, double loopSeconds, double initialBackoff, double timeout, CancellationToken ct)
    {
        await RunWatchdogLoopWithCheckAsync(
            () => NfsHealthChecker.CheckFilesystem(directory),
            terminator, loopSeconds, initialBackoff, timeout, ct);
    }

    private static async Task RunWatchdogLoopWithCheckAsync(Func<bool> check, IPodTerminator terminator, double loopSeconds, double initialBackoff, double timeout, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(loopSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var healthy = await RetryWithBackoffAsync(check, TimeSpan.FromSeconds(initialBackoff), TimeSpan.FromSeconds(timeout), ct).ConfigureAwait(false);

                if (!healthy)
                {
                    await terminator.TerminateAsync(ct).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    private static async Task<bool> RetryWithBackoffAsync(Func<bool> check, TimeSpan initialBackoff, TimeSpan maxElapsed, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxElapsed;
        var backoff = initialBackoff;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (check()) return true;

            var remaining = deadline - DateTime.UtcNow;
            var delay = backoff < remaining ? backoff : remaining;
            if (delay <= TimeSpan.Zero) break;

            await Task.Delay(delay, ct).ConfigureAwait(false);
            backoff = backoff < maxElapsed ? backoff * 2 : maxElapsed;
        }

        return check();
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
