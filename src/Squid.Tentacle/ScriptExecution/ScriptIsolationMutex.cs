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
