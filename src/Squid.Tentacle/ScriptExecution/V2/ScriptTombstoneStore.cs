using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Squid.Message.Contracts.Tentacle.V2;

namespace Squid.Tentacle.ScriptExecution.V2;

/// <summary>What the agent remembers about a ticket after its workspace was released.</summary>
public sealed record ScriptTombstone(string TicketId, ScriptStateV2 State, int ExitCode, DateTimeOffset CompletedAt);

/// <summary>
/// Durable, bounded memory of tickets that <c>CompleteScript</c> already released, so a repeated V2
/// Complete answers <c>AlreadyCompleted</c> instead of looking like an unknown ticket.
///
/// <para><b>Storage</b>: one tiny JSON file (~150 bytes) per ticket in a dedicated directory next to the
/// workspaces it supplements (the temp root for the local backend, the workspace volume for the pod backend),
/// so it survives an agent restart exactly as long as the workspace state it replaces would have. The
/// directory name deliberately does not match the <c>squid-tentacle-*</c> orphan-workspace glob, and the pod
/// monitor skips it by name.</para>
///
/// <para><b>Bounds</b>: entries expire after <see cref="DefaultRetention"/> and the directory never holds
/// more than <see cref="DefaultMaxEntries"/> files (oldest evicted first), so worst case is about 1.5 MB.
/// Pruning runs on writes at most every <see cref="PruneInterval"/>, never on the hot read path.</para>
/// </summary>
public sealed class ScriptTombstoneStore
{
    public const string LocalDirectoryName = "squid-script-tombstones";
    public const string WorkspaceDirectoryName = ".squid-script-tombstones";

    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    public const int DefaultMaxEntries = 10_000;
    public static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);

    private static readonly Regex TicketIdWhitelist = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _directory;
    private readonly TimeSpan _retention;
    private readonly int _maxEntries;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _pruneLock = new();
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public ScriptTombstoneStore(string directory, TimeSpan? retention = null, int? maxEntries = null, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _retention = retention is { } r && r > TimeSpan.Zero ? r : DefaultRetention;
        _maxEntries = maxEntries is { } m && m > 0 ? m : DefaultMaxEntries;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static ScriptTombstoneStore ForTempRoot() => new(Path.Combine(Path.GetTempPath(), LocalDirectoryName));

    public static ScriptTombstoneStore ForWorkspace(string workspacePath) => new(Path.Combine(workspacePath, WorkspaceDirectoryName));

    public void Write(ScriptTombstone tombstone) => TryWrite(tombstone);

    /// <summary>
    /// Writes the tombstone and reports whether it is durably on disk. Callers that are about to release the
    /// workspace state it replaces must keep that state when this returns false, otherwise a crash leaves nothing
    /// that records the result and a resume re-runs the script.
    /// </summary>
    public bool TryWrite(ScriptTombstone tombstone)
    {
        if (!TicketIdWhitelist.IsMatch(tombstone.TicketId)) return false;

        try
        {
            Directory.CreateDirectory(_directory);
            WriteAtomically(tombstone);
            PruneIfDue();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write completion tombstone for ticket {TicketId}; keeping its recorded state so the result is not lost", tombstone.TicketId);

            return false;
        }
    }

    public ScriptTombstone? TryRead(string ticketId)
    {
        if (!TicketIdWhitelist.IsMatch(ticketId)) return null;

        var path = PathFor(ticketId);

        try
        {
            if (!File.Exists(path)) return null;

            var tombstone = JsonSerializer.Deserialize<ScriptTombstone>(File.ReadAllText(path), JsonOptions);

            return tombstone != null && !IsExpired(tombstone) ? tombstone : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Unreadable completion tombstone for ticket {TicketId}; treating as absent", ticketId);
            return null;
        }
    }

    /// <summary>Removes expired entries, then the oldest beyond the cap. Returns how many files were deleted.</summary>
    public int Prune()
    {
        if (!Directory.Exists(_directory)) return 0;

        var files = new DirectoryInfo(_directory).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var cutoff = _clock() - _retention;
        var doomed = files.Where(f => f.LastWriteTimeUtc < cutoff.UtcDateTime).ToList();
        var survivors = files.Except(doomed).ToList();

        doomed.AddRange(survivors.Take(Math.Max(0, survivors.Count - _maxEntries)));

        return doomed.Count(TryDelete);
    }

    private void WriteAtomically(ScriptTombstone tombstone)
    {
        var path = PathFor(tombstone.TicketId);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(tombstone, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private void PruneIfDue()
    {
        lock (_pruneLock)
        {
            if (_clock() - _lastPrune < PruneInterval) return;

            _lastPrune = _clock();
        }

        Prune();
    }

    private string PathFor(string ticketId) => Path.Combine(_directory, ticketId + ".json");

    private bool IsExpired(ScriptTombstone tombstone) => _clock() - tombstone.CompletedAt > _retention;

    private static bool TryDelete(FileInfo file)
    {
        try { file.Delete(); return true; }
        catch (IOException) { return false; }
    }
}
