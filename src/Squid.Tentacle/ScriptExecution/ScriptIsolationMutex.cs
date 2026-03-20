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
                    Log.Information("Isolation mutex {MutexName} has active writer, deferring reader", mutexName);
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
                    Log.Information("Isolation mutex {MutexName} is held (writer: {Writer}, readers: {Readers}), deferring writer", mutexName, _writerActive, _activeReaders);
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
