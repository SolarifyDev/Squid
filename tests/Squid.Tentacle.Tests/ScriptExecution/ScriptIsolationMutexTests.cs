using System;
using System.Threading;
using System.Threading.Tasks;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class ScriptIsolationMutexTests
{
    private readonly ScriptIsolationMutex _mutex = new();

    [Fact]
    public void Acquire_NoIsolation_ReturnsImmediately()
    {
        var command = MakeCommand(ScriptIsolationLevel.NoIsolation);

        var handle = _mutex.Acquire(command);

        handle.ShouldNotBeNull();
        handle.Dispose();
    }

    [Fact]
    public void Acquire_NoIsolation_MultipleConcurrent_AllSucceed()
    {
        var handles = new IDisposable[10];

        for (var i = 0; i < 10; i++)
            handles[i] = _mutex.Acquire(MakeCommand(ScriptIsolationLevel.NoIsolation));

        foreach (var h in handles)
            h.Dispose();
    }

    [Fact]
    public void Acquire_FullIsolation_ReturnsHandle()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);

        var handle = _mutex.Acquire(command);

        handle.ShouldNotBeNull();
        handle.Dispose();
    }

    [Fact]
    public void Acquire_FullIsolation_SecondAcquire_BlocksUntilFirstReleased()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, "test-mutex", TimeSpan.FromSeconds(5));

        var handle1 = _mutex.Acquire(command);
        var acquired = false;

        var task = Task.Run(() =>
        {
            var handle2 = _mutex.Acquire(command);
            acquired = true;
            handle2.Dispose();
        });

        Thread.Sleep(200);
        acquired.ShouldBeFalse();

        handle1.Dispose();
        task.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        acquired.ShouldBeTrue();
    }

    [Fact]
    public void Acquire_FullIsolation_Timeout_ThrowsTimeoutException()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, "timeout-mutex", TimeSpan.FromMilliseconds(200));

        var handle1 = _mutex.Acquire(command);

        Should.Throw<TimeoutException>(() => _mutex.Acquire(command));

        handle1.Dispose();
    }

    [Fact]
    public void Acquire_FullIsolation_DifferentMutexNames_DoNotBlock()
    {
        var command1 = MakeCommand(ScriptIsolationLevel.FullIsolation, "mutex-a", TimeSpan.FromSeconds(5));
        var command2 = MakeCommand(ScriptIsolationLevel.FullIsolation, "mutex-b", TimeSpan.FromSeconds(5));

        var handle1 = _mutex.Acquire(command1);
        var handle2 = _mutex.Acquire(command2);

        handle1.Dispose();
        handle2.Dispose();
    }

    [Fact]
    public void Acquire_FullIsolation_NullMutexName_UsesDefault()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, null, TimeSpan.FromMilliseconds(200));

        var handle1 = _mutex.Acquire(command);

        Should.Throw<TimeoutException>(() => _mutex.Acquire(command));

        handle1.Dispose();
    }

    [Fact]
    public void Acquire_FullIsolation_ZeroTimeout_UsesDefaultTimeout()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, "zero-timeout", TimeSpan.Zero);

        var handle = _mutex.Acquire(command);

        handle.ShouldNotBeNull();
        handle.Dispose();
    }

    [Fact]
    public void Dispose_Handle_Twice_DoesNotThrow()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);

        var handle = _mutex.Acquire(command);

        handle.Dispose();
        Should.NotThrow(() => handle.Dispose());
    }

    [Fact]
    public void Dispose_Handle_ReleasesForNextAcquire()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, "release-test", TimeSpan.FromMilliseconds(500));

        var handle1 = _mutex.Acquire(command);
        handle1.Dispose();

        var handle2 = _mutex.Acquire(command);
        handle2.ShouldNotBeNull();
        handle2.Dispose();
    }

    [Theory]
    [InlineData(ScriptIsolationLevel.NoIsolation, false)]
    [InlineData(ScriptIsolationLevel.FullIsolation, true)]
    public void Acquire_IsolationLevel_DeterminesBlocking(ScriptIsolationLevel level, bool shouldBlock)
    {
        var command = MakeCommand(level, "blocking-test", TimeSpan.FromMilliseconds(200));

        var handle1 = _mutex.Acquire(command);

        if (shouldBlock)
        {
            Should.Throw<TimeoutException>(() => _mutex.Acquire(command));
        }
        else
        {
            var handle2 = _mutex.Acquire(command);
            handle2.Dispose();
        }

        handle1.Dispose();
    }

    private static StartScriptCommand MakeCommand(ScriptIsolationLevel isolation, string? mutexName = null, TimeSpan? timeout = null)
    {
        return new StartScriptCommand(
            "echo test",
            isolation,
            timeout ?? TimeSpan.FromMinutes(5),
            mutexName,
            Array.Empty<string>(),
            null);
    }
}
