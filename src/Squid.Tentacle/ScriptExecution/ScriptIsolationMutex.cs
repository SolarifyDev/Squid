using System.Collections.Concurrent;
using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public class ScriptIsolationMutex
{
    private readonly ConcurrentDictionary<string, ReaderWriterLockState> _mutexes = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(ScriptIsolationLevel isolation, string? isolationMutexName, out IDisposable? handle)
    {
        var mutexName = isolationMutexName ?? "default";
        var state = _mutexes.GetOrAdd(mutexName, _ => new ReaderWriterLockState());

        if (isolation == ScriptIsolationLevel.FullIsolation)
            return state.TryAcquireWriter(mutexName, out handle);

        return state.TryAcquireReader(mutexName, out handle);
    }

    public bool TryAcquire(StartScriptCommand command, out IDisposable? handle)
        => TryAcquire(command.Isolation, command.IsolationMutexName, out handle);

    public async Task<IDisposable?> AcquireAsync(ScriptIsolationLevel isolation, string? isolationMutexName, TimeSpan timeout, CancellationToken ct = default)
    {
        var mutexName = isolationMutexName ?? "default";
        var state = _mutexes.GetOrAdd(mutexName, _ => new ReaderWriterLockState());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var pollInterval = TimeSpan.FromMilliseconds(100);

        while (!cts.Token.IsCancellationRequested)
        {
            IDisposable? handle;
            var acquired = isolation == ScriptIsolationLevel.FullIsolation
                ? state.TryAcquireWriter(mutexName, out handle)
                : state.TryAcquireReader(mutexName, out handle);

            if (acquired)
                return handle;

            try
            {
                await Task.Delay(pollInterval, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout reached (not external cancellation)
                Log.Warning("Isolation mutex {MutexName} acquire timeout ({Timeout}s) for {Level}",
                    mutexName, timeout.TotalSeconds, isolation);
                return null;
            }

            pollInterval = TimeSpan.FromMilliseconds(Math.Min(pollInterval.TotalMilliseconds * 1.5, 1000));
        }

        ct.ThrowIfCancellationRequested();
        return null;
    }

    public Task<IDisposable?> AcquireAsync(StartScriptCommand command, CancellationToken ct = default)
        => AcquireAsync(command.Isolation, command.IsolationMutexName, command.ScriptIsolationMutexTimeout, ct);

    /// <summary>
    /// P1-Phase11.2 (audit ARCH.9 F1.1) — pure-sync acquire-with-polling.
    ///
    /// <para><b>Why this exists</b>: pre-Phase-11.2,
    /// <see cref="LocalScriptService.StartScript"/> wrapped
    /// <see cref="AcquireAsync(StartScriptCommand, CancellationToken)"/>
    /// in <c>.GetAwaiter().GetResult()</c>. That sync-over-async pattern
    /// burns a Halibut RPC thread on a Task.Delay-based async polling loop,
    /// adding allocation churn and threadpool starvation pressure under
    /// mutex contention. Since the wire contract is sync (V1 design,
    /// no real CT to thread), we drop the async abstraction entirely
    /// here — pure sync polling using <see cref="TryAcquire(ScriptIsolationLevel, string?, out IDisposable?)"/>
    /// + <see cref="Thread.Sleep"/>. No Task allocation, no async state
    /// machine, no threadpool churn.</para>
    ///
    /// <para>The <paramref name="cancellationToken"/> parameter accepts the
    /// per-ticket soft-cancel token from
    /// <see cref="ScriptCancellationRegistry"/> so a CancelScript RPC
    /// arriving mid-acquire can short-circuit the polling loop early
    /// without waiting for the configured isolation timeout.</para>
    /// </summary>
    public IDisposable? TryAcquireBlocking(
        ScriptIsolationLevel isolation,
        string? isolationMutexName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var mutexName = isolationMutexName ?? "default";
        var deadline = DateTimeOffset.UtcNow + timeout;
        var pollMs = 100;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IDisposable? handle;
            var acquired = isolation == ScriptIsolationLevel.FullIsolation
                ? _mutexes.GetOrAdd(mutexName, _ => new ReaderWriterLockState()).TryAcquireWriter(mutexName, out handle)
                : _mutexes.GetOrAdd(mutexName, _ => new ReaderWriterLockState()).TryAcquireReader(mutexName, out handle);

            if (acquired) return handle;

            // Poll backoff: same shape as AcquireAsync (100ms → 1s ceiling).
            // Keeps spin pressure low under sustained contention.
            var sleepMs = (int)Math.Min(pollMs, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
            if (sleepMs <= 0) break;

            try
            {
                if (cancellationToken.WaitHandle.WaitOne(sleepMs))
                {
                    // CT signalled → short-circuit
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (ObjectDisposedException)
            {
                // CTS disposed mid-poll (Cleanup raced); treat as "no cancel"
                // and let the deadline check decide.
            }

            pollMs = Math.Min((int)(pollMs * 1.5), 1000);
        }

        Log.Warning("Isolation mutex {MutexName} acquire timeout ({Timeout}s) for {Level}",
            mutexName, timeout.TotalSeconds, isolation);
        return null;
    }

    /// <summary>Sync overload mirroring <see cref="AcquireAsync(StartScriptCommand, CancellationToken)"/>.</summary>
    public IDisposable? TryAcquireBlocking(StartScriptCommand command, CancellationToken cancellationToken = default)
        => TryAcquireBlocking(command.Isolation, command.IsolationMutexName, command.ScriptIsolationMutexTimeout, cancellationToken);

    private sealed class ReaderWriterLockState
    {
        private readonly object _gate = new();
        private int _activeReaders;
        private bool _writerActive;

        public bool TryAcquireReader(string mutexName, out IDisposable? handle)
        {
            lock (_gate)
            {
                if (_writerActive)
                {
                    handle = null;
                    return false;
                }

                _activeReaders++;

                Log.Information("Acquired reader lock on isolation mutex {MutexName} (readers: {Count})", mutexName, _activeReaders);

                handle = new LockRelease(this, mutexName, isWriter: false);
                return true;
            }
        }

        public bool TryAcquireWriter(string mutexName, out IDisposable? handle)
        {
            lock (_gate)
            {
                if (_writerActive || _activeReaders > 0)
                {
                    handle = null;
                    return false;
                }

                _writerActive = true;

                Log.Information("Acquired writer lock on isolation mutex {MutexName}", mutexName);

                handle = new LockRelease(this, mutexName, isWriter: true);
                return true;
            }
        }

        public void ReleaseReader(string mutexName)
        {
            lock (_gate)
            {
                _activeReaders--;

                Log.Information("Released reader lock on isolation mutex {MutexName} (readers: {Count})", mutexName, _activeReaders);
            }
        }

        public void ReleaseWriter(string mutexName)
        {
            lock (_gate)
            {
                _writerActive = false;

                Log.Information("Released writer lock on isolation mutex {MutexName}", mutexName);
            }
        }
    }

    private sealed class LockRelease : IDisposable
    {
        private readonly ReaderWriterLockState _state;
        private readonly string _mutexName;
        private readonly bool _isWriter;
        private int _disposed;

        public LockRelease(ReaderWriterLockState state, string mutexName, bool isWriter)
        {
            _state = state;
            _mutexName = mutexName;
            _isWriter = isWriter;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (_isWriter)
                    _state.ReleaseWriter(_mutexName);
                else
                    _state.ReleaseReader(_mutexName);
            }
        }
    }
}
