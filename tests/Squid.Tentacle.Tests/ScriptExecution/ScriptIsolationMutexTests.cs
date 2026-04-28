using System;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class ScriptIsolationMutexTests
{
    private readonly ScriptIsolationMutex _mutex = new();

    [Fact]
    public void NoIsolation_MultipleReaders_AllSucceed()
    {
        var handles = new IDisposable?[10];

        for (var i = 0; i < 10; i++)
        {
            var result = _mutex.TryAcquire(MakeCommand(ScriptIsolationLevel.NoIsolation), out handles[i]);
            result.ShouldBeTrue();
            handles[i].ShouldNotBeNull();
        }

        foreach (var h in handles)
            h?.Dispose();
    }

    [Fact]
    public void FullIsolation_BlocksSecondWriter()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);

        _mutex.TryAcquire(command, out var handle1);

        var result = _mutex.TryAcquire(command, out var handle2);

        result.ShouldBeFalse();
        handle2.ShouldBeNull();

        handle1!.Dispose();
    }

    [Fact]
    public void FullIsolation_BlocksReader()
    {
        var writer = MakeCommand(ScriptIsolationLevel.FullIsolation);
        var reader = MakeCommand(ScriptIsolationLevel.NoIsolation);

        _mutex.TryAcquire(writer, out var writerHandle);

        var result = _mutex.TryAcquire(reader, out var readerHandle);

        result.ShouldBeFalse();
        readerHandle.ShouldBeNull();

        writerHandle!.Dispose();
    }

    [Fact]
    public void NoIsolation_BlocksWriter()
    {
        var reader = MakeCommand(ScriptIsolationLevel.NoIsolation);
        var writer = MakeCommand(ScriptIsolationLevel.FullIsolation);

        _mutex.TryAcquire(reader, out var readerHandle);

        var result = _mutex.TryAcquire(writer, out var writerHandle);

        result.ShouldBeFalse();
        writerHandle.ShouldBeNull();

        readerHandle!.Dispose();
    }

    [Fact]
    public void Writer_AfterAllReadersRelease_Succeeds()
    {
        var reader = MakeCommand(ScriptIsolationLevel.NoIsolation);
        var writer = MakeCommand(ScriptIsolationLevel.FullIsolation);

        _mutex.TryAcquire(reader, out var r1);
        _mutex.TryAcquire(reader, out var r2);

        _mutex.TryAcquire(writer, out _).ShouldBeFalse();

        r1!.Dispose();

        _mutex.TryAcquire(writer, out _).ShouldBeFalse();

        r2!.Dispose();

        var result = _mutex.TryAcquire(writer, out var writerHandle);

        result.ShouldBeTrue();
        writerHandle.ShouldNotBeNull();
        writerHandle!.Dispose();
    }

    [Fact]
    public void Reader_AfterWriterRelease_Succeeds()
    {
        var writer = MakeCommand(ScriptIsolationLevel.FullIsolation);
        var reader = MakeCommand(ScriptIsolationLevel.NoIsolation);

        _mutex.TryAcquire(writer, out var writerHandle);

        _mutex.TryAcquire(reader, out _).ShouldBeFalse();

        writerHandle!.Dispose();

        var result = _mutex.TryAcquire(reader, out var readerHandle);

        result.ShouldBeTrue();
        readerHandle.ShouldNotBeNull();
        readerHandle!.Dispose();
    }

    [Fact]
    public void DifferentMutexNames_Independent()
    {
        var commandA = MakeCommand(ScriptIsolationLevel.FullIsolation, "mutex-a");
        var commandB = MakeCommand(ScriptIsolationLevel.FullIsolation, "mutex-b");

        _mutex.TryAcquire(commandA, out var handleA).ShouldBeTrue();
        _mutex.TryAcquire(commandB, out var handleB).ShouldBeTrue();

        handleA!.Dispose();
        handleB!.Dispose();
    }

    [Fact]
    public void NullMutexName_UsesDefault()
    {
        var command1 = MakeCommand(ScriptIsolationLevel.FullIsolation, null);
        var command2 = MakeCommand(ScriptIsolationLevel.FullIsolation, null);

        _mutex.TryAcquire(command1, out var handle1);

        var result = _mutex.TryAcquire(command2, out var handle2);

        result.ShouldBeFalse();
        handle2.ShouldBeNull();

        handle1!.Dispose();
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);

        _mutex.TryAcquire(command, out var handle);

        handle!.Dispose();
        Should.NotThrow(() => handle.Dispose());
    }

    [Fact]
    public void Dispose_ReaderHandle_Idempotent()
    {
        var command = MakeCommand(ScriptIsolationLevel.NoIsolation);

        _mutex.TryAcquire(command, out var handle);

        handle!.Dispose();
        Should.NotThrow(() => handle.Dispose());
    }

    // ── P1-Phase11.2 (audit ARCH.9 F1.1) — pure-sync TryAcquireBlocking ─────

    [Fact]
    public void TryAcquireBlocking_FreeMutex_SucceedsImmediately()
    {
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);

        var handle = _mutex.TryAcquireBlocking(command);

        handle.ShouldNotBeNull(customMessage:
            "Acquire on a free mutex must succeed on first try without polling.");
        handle?.Dispose();
    }

    [Fact]
    public void TryAcquireBlocking_ContendedMutex_TimesOutReturnsNull()
    {
        // First writer holds the slot; second TryAcquireBlocking with a tiny
        // timeout must return null when the timeout fires (NOT throw).
        var first = MakeCommand(ScriptIsolationLevel.FullIsolation, mutexName: "shared");
        var second = MakeCommand(ScriptIsolationLevel.FullIsolation, mutexName: "shared", timeout: TimeSpan.FromMilliseconds(200));

        _mutex.TryAcquire(first, out var firstHandle);
        firstHandle.ShouldNotBeNull();

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var contended = _mutex.TryAcquireBlocking(second);
            sw.Stop();

            contended.ShouldBeNull(customMessage:
                "Contended acquire must time out and return null (not throw).");
            sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(150,
                customMessage: "Acquire must actually poll for the timeout window, not return immediately.");
            sw.ElapsedMilliseconds.ShouldBeLessThan(800,
                customMessage: "Acquire must NOT poll well past the timeout.");
        }
        finally
        {
            firstHandle?.Dispose();
        }
    }

    [Fact]
    public void TryAcquireBlocking_CtCancelled_ShortCircuitsBeforeTimeout()
    {
        // Soft-cancel scenario: CancelScript RPC arrives mid-acquire. The
        // CTS flips, the polling loop short-circuits via OperationCanceledException
        // INSTEAD of waiting for the configured isolation timeout.
        var first = MakeCommand(ScriptIsolationLevel.FullIsolation, mutexName: "shared");
        var second = MakeCommand(ScriptIsolationLevel.FullIsolation, mutexName: "shared", timeout: TimeSpan.FromSeconds(30));

        _mutex.TryAcquire(first, out var firstHandle);

        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(150));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Should.Throw<OperationCanceledException>(
                () => _mutex.TryAcquireBlocking(second, cts.Token));
            sw.Stop();

            sw.ElapsedMilliseconds.ShouldBeLessThan(2000, customMessage:
                "CT cancel must short-circuit polling — must NOT wait for the 30s isolation timeout.");
        }
        finally
        {
            firstHandle?.Dispose();
        }
    }

    [Fact]
    public void TryAcquireBlocking_PreCancelledCt_ThrowsImmediately()
    {
        // Out-of-order: ScriptCancellationRegistry returns an already-cancelled
        // token (Cancel arrived before GetOrCreate). TryAcquireBlocking must
        // throw immediately rather than even try one acquire.
        var command = MakeCommand(ScriptIsolationLevel.FullIsolation);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(
            () => _mutex.TryAcquireBlocking(command, cts.Token));
    }

    private static StartScriptCommand MakeCommand(ScriptIsolationLevel isolation, string? mutexName = null, TimeSpan? timeout = null)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "echo test",
            isolation,
            timeout ?? TimeSpan.FromMinutes(5),
            mutexName,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }
}
