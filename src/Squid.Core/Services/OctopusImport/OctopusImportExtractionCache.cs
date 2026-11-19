using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The parsed source of an import session, shared by the requests of its flow (extract, project picker, preview, every validate,
/// confirm). A server session's history bundle can hold gigabytes; parsing it again on each request costs minutes of CPU and
/// gigabytes of allocation, and overlapping requests multiply that. An entry is keyed by the file's identity (path, length, last
/// write), so a rewritten file is read again; a failed read is never kept.
/// </summary>
public interface IOctopusImportExtractionCache : ISingletonDependency
{
    /// <param name="read">Reads the file when it is not cached; it runs to completion even when the caller that started it gives up, so the next caller gets the result.</param>
    Task<OctopusInputExtractionResult> GetOrReadAsync(string path, Func<Task<OctopusInputExtractionResult>> read, CancellationToken ct = default);
}

public sealed class OctopusImportExtractionCache : IOctopusImportExtractionCache, IDisposable
{
    /// <summary>How many sources are held at once; the least recently used one goes first. A session works on one source at a time.</summary>
    public const int Capacity = 2;

    /// <summary>How long an unused source is held before its memory is given back.</summary>
    public static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private static readonly Lazy<OctopusImportExtractionCache> SharedInstance = new(() => new OctopusImportExtractionCache());

    /// <summary>The process-wide cache for callers built without the container's singleton.</summary>
    public static OctopusImportExtractionCache Shared => SharedInstance.Value;

    private readonly object _gate = new();
    private readonly Dictionary<SourceIdentity, Entry> _entries = new();
    private readonly TimeProvider _time;
    private readonly ITimer _sweep;

    public OctopusImportExtractionCache() : this(TimeProvider.System)
    {
    }

    public OctopusImportExtractionCache(TimeProvider time)
    {
        _time = time;
        _sweep = time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public async Task<OctopusInputExtractionResult> GetOrReadAsync(string path, Func<Task<OctopusInputExtractionResult>> read, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);

        var key = SourceIdentity.Of(path);
        var entry = GetOrAdd(key, read);

        try
        {
            return await entry.Extraction.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!entry.Extraction.Value.IsCompleted)
        {
            throw;
        }
        catch
        {
            Remove(key, entry);
            throw;
        }
    }

    /// <summary>Drops the sources unused for <see cref="IdleExpiry"/>; runs on a timer.</summary>
    public void Sweep()
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            foreach (var key in _entries.Where(pair => now - pair.Value.LastUsed >= IdleExpiry).Select(pair => pair.Key).ToList())
                _entries.Remove(key);
        }
    }

    public void Dispose() => _sweep.Dispose();

    private Entry GetOrAdd(SourceIdentity key, Func<Task<OctopusInputExtractionResult>> read)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.LastUsed = _time.GetUtcNow();
                return entry;
            }

            entry = new Entry(new Lazy<Task<OctopusInputExtractionResult>>(() => Task.Run(read), LazyThreadSafetyMode.ExecutionAndPublication)) { LastUsed = _time.GetUtcNow() };
            _entries[key] = entry;
            EvictBeyondCapacity();

            return entry;
        }
    }

    private void EvictBeyondCapacity()
    {
        while (_entries.Count > Capacity)
            _entries.Remove(_entries.MinBy(pair => pair.Value.LastUsed).Key);
    }

    private void Remove(SourceIdentity key, Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
        }
    }

    private sealed class Entry(Lazy<Task<OctopusInputExtractionResult>> extraction)
    {
        public Lazy<Task<OctopusInputExtractionResult>> Extraction { get; } = extraction;
        public DateTimeOffset LastUsed { get; set; }
    }

    private sealed record SourceIdentity(string FullPath, long Length, DateTime LastWriteTimeUtc)
    {
        public static SourceIdentity Of(string path)
        {
            var file = new FileInfo(path);

            return new SourceIdentity(Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc);
        }
    }
}
