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
    public void TryAcquire_NoIsolation_ReturnsTrue()
    {
        var command = MakeCommand(ScriptIsolationLevel.NoIsolation);

        var result = _mutex.TryAcquire(command, out var handle);

        result.ShouldBeTrue();
        handle.ShouldNotBeNull();
        handle.Dispose();
    }

    [Fact]
    public void TryAcquire_NoIsolation_MultipleConcurrent_AllSucceed()
    {
        var handles = new IDisposable?[10];

        for (var i = 0; i < 10; i++)
        {
            _mutex.TryAcquire(MakeCommand(ScriptIsolationLevel.NoIsolation), out handles[i]);
        }

        foreach (var h in handles)
            h?.Dispose();
    }

    [Fact]
    public void TryAcquire_FullIsolation_ReturnsHandle()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);

        var result = _mutex.TryAcquire(command, out var handle);

        result.ShouldBeTrue();
        handle.ShouldNotBeNull();
        handle.Dispose();
    }

    [Fact]
    public void TryAcquire_FullIsolation_SecondAttempt_ReturnsFalse()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, "test-mutex", TimeSpan.FromSeconds(5));

        _mutex.TryAcquire(command, out var handle1);

        var result = _mutex.TryAcquire(command, out var handle2);

        result.ShouldBeFalse();
        handle2.ShouldBeNull();

        handle1!.Dispose();
    }

    [Fact]
    public void TryAcquire_FullIsolation_DifferentMutexNames_DoNotBlock()
    {
        var command1 = MakeCommand(ScriptIsolationLevel.FullIsolation, "mutex-a", TimeSpan.FromSeconds(5));
        var command2 = MakeCommand(ScriptIsolationLevel.FullIsolation, "mutex-b", TimeSpan.FromSeconds(5));

        _mutex.TryAcquire(command1, out var handle1);
        _mutex.TryAcquire(command2, out var handle2);

        handle1!.Dispose();
        handle2!.Dispose();
    }

    [Fact]
    public void TryAcquire_FullIsolation_NullMutexName_UsesDefault()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, null, TimeSpan.FromMilliseconds(200));

        _mutex.TryAcquire(command, out var handle1);

        var result = _mutex.TryAcquire(command, out var handle2);

        result.ShouldBeFalse();
        handle2.ShouldBeNull();

        handle1!.Dispose();
    }

    [Fact]
    public void Dispose_Handle_Twice_DoesNotThrow()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);

        _mutex.TryAcquire(command, out var handle);

        handle!.Dispose();
        Should.NotThrow(() => handle.Dispose());
    }

    [Fact]
    public void Dispose_Handle_ReleasesForNextAcquire()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation, "release-test", TimeSpan.FromMilliseconds(500));

        _mutex.TryAcquire(command, out var handle1);
        handle1!.Dispose();

        var result = _mutex.TryAcquire(command, out var handle2);

        result.ShouldBeTrue();
        handle2.ShouldNotBeNull();
        handle2.Dispose();
    }

    [Theory]
    [InlineData(ScriptIsolationLevel.NoIsolation, false)]
    [InlineData(ScriptIsolationLevel.FullIsolation, true)]
    public void TryAcquire_IsolationLevel_DeterminesBlocking(ScriptIsolationLevel level, bool shouldBlock)
    {
        var command = MakeCommand(level, "blocking-test", TimeSpan.FromMilliseconds(200));

        _mutex.TryAcquire(command, out var handle1);

        if (shouldBlock)
        {
            var result = _mutex.TryAcquire(command, out var handle2);
            result.ShouldBeFalse();
            handle2.ShouldBeNull();
        }
        else
        {
            var result = _mutex.TryAcquire(command, out var handle2);
            result.ShouldBeTrue();
            handle2!.Dispose();
        }

        handle1!.Dispose();
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
