using Serilog;
using Squid.Tentacle.ScriptExecution.State;

namespace Squid.Tentacle.SelfHeal;

/// <summary>
/// Enumerates completed-script workspace directories under a root and classifies
/// each one for the disk-pressure heal sweep: maps the persisted
/// <see cref="ScriptState"/> to a <see cref="WorkspaceStatus"/>, measures the
/// directory's size + last-write time, and ignores the root itself and any
/// directory that does not follow the <c>squid-tentacle-{ticketId}</c>
/// convention. It is pure of pressure/eviction logic — the
/// <see cref="IWorkspaceCleanupPolicy"/> decides what to evict; this only reports
/// what exists, so the heal action and the policy stay independently testable.
/// </summary>
public static class WorkspaceProbe
{
    // Matches LocalScriptService.ResolveWorkDir: {tempRoot}/squid-tentacle-{ticketId}.
    internal const string WorkspacePrefix = "squid-tentacle-";

    public static IReadOnlyList<WorkspaceCandidate> Probe(string root, IScriptStateStoreFactory stateStoreFactory)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<WorkspaceCandidate>();

        var candidates = new List<WorkspaceCandidate>();

        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || !name.StartsWith(WorkspacePrefix, StringComparison.Ordinal))
                continue;

            try
            {
                var status = ClassifyStatus(dir, stateStoreFactory);
                var lastModified = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
                var size = ComputeSizeBytes(dir);

                candidates.Add(new WorkspaceCandidate(dir, lastModified, size, status));
            }
            catch (Exception ex)
            {
                // A workspace that vanished mid-probe or is momentarily locked must
                // not abort the whole sweep — skip it; the next tick re-evaluates.
                Log.Debug(ex, "[SelfHeal] Skipping unreadable workspace {Path}", dir);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Maps the persisted state to a heal status. A workspace with no readable
    /// state file is <see cref="WorkspaceStatus.Unknown"/> (treated as evictable
    /// under pressure but kept out of the per-status retention windows); a
    /// started-but-not-complete script is <see cref="WorkspaceStatus.Active"/> so
    /// the policy never evicts it (the running-script-reporter veto in
    /// <see cref="DiskPressureHealAction"/> is the authoritative second guard).
    /// </summary>
    private static WorkspaceStatus ClassifyStatus(string workDir, IScriptStateStoreFactory stateStoreFactory)
    {
        var store = stateStoreFactory.Create(workDir);

        if (!store.Exists())
            return WorkspaceStatus.Unknown;

        ScriptState state;
        try
        {
            state = store.Load();
        }
        catch (Exception ex)
        {
            // State file is present but unreadable (corrupt primary with no usable
            // backup — e.g. a partial write when the disk filled mid-Save). Classify
            // Unknown rather than letting the throw bubble to Probe's catch-and-skip,
            // which would drop the workspace from the candidate list forever and make
            // a dead-but-corrupt workspace permanently un-reclaimable — exactly when
            // disk pressure most needs the space. A still-live script is separately
            // protected by the running-script-reporter veto + the fresh-grace floor.
            Log.Debug(ex, "[SelfHeal] Unreadable script state at {Path}; classifying Unknown", workDir);
            return WorkspaceStatus.Unknown;
        }

        if (!state.IsComplete())
            return state.HasStarted() ? WorkspaceStatus.Active : WorkspaceStatus.Unknown;

        return state.ExitCode == 0 ? WorkspaceStatus.Succeeded : WorkspaceStatus.Failed;
    }

    private static long ComputeSizeBytes(string dir)
    {
        long total = 0;

        foreach (var file in SafeEnumerateFiles(dir))
        {
            try { total += new FileInfo(file).Length; }
            catch { /* file removed mid-walk — ignore */ }
        }

        return total;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SelfHeal] Could not enumerate workspace root {Root}", root);
            return Array.Empty<string>();
        }
    }

    // Recurse subdirectories but SKIP reparse points (directory symlinks /
    // junctions): the default SearchOption.AllDirectories walk follows them, so a
    // deployment package that extracted a symlink-to-parent (or a runtime `ln -s`)
    // makes the walk loop until it exhausts the path-length limit — burning CPU/IO
    // on the exact low-disk tick the heal exists to relieve, and mis-attributing a
    // symlink target's size to the workspace. AttributesToSkip overrides the default
    // (Hidden|System) so hidden/system files still count toward disk usage.
    private static readonly EnumerationOptions SizeWalkOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true
    };

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SizeWalkOptions); }
        catch { return Array.Empty<string>(); }
    }
}
