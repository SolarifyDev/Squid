using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Serilog;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.ServiceHost;

/// <summary>
/// What the Windows upgrade script records before it suspends the SCM failure-actions flag
/// (<c>sc failureflag &lt;svc&gt; 0</c>) for the binary swap, so the flag can be put back even when the script
/// is killed (host reboot, terminated process) before its <c>finally</c> block runs.
/// </summary>
/// <remarks>
/// Producer: <c>upgrade-windows-tentacle.ps1</c> (<c>Write-FailureFlagMarker</c>). The file name prefix and the JSON
/// property names are a wire contract pinned by a drift test against that script.
/// </remarks>
public sealed class FailureFlagMarker
{
    public const string FilePrefix = "failureflag-suspended-";

    public const string FileExtension = ".json";

    /// <summary>Sub-directory of the system config dir that holds the upgrade state (shared with the upgrade status files).</summary>
    public const string UpgradeSubDirectory = "upgrade";

    /// <summary>Tolerance when matching the owner's recorded start time against the live process (clock rounding between PowerShell and .NET).</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public string Service { get; set; } = string.Empty;

    /// <summary>The flag value to restore: 1 = failure actions apply to non-crash failures, 0 = operator had it off.</summary>
    public int OriginalFlag { get; set; } = 1;

    public int OwnerPid { get; set; }

    public long OwnerStartUtcTicks { get; set; }

    /// <summary>Same upgrade process (PID + start time) as <paramref name="other"/>: a marker re-read mid-restore is only "ours" while this holds.</summary>
    public bool HasSameOwnerAs(FailureFlagMarker other) => OwnerPid == other.OwnerPid && OwnerStartUtcTicks == other.OwnerStartUtcTicks;

    public static bool TryParse(string json, out FailureFlagMarker marker)
    {
        marker = null;

        try
        {
            var parsed = JsonSerializer.Deserialize<FailureFlagMarker>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Service) || parsed.OriginalFlag is not (0 or 1) || parsed.OwnerPid <= 0) return false;

            marker = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the upgrade process that wrote the marker is still running (same PID AND same start time, so a recycled
    /// PID does not count). A lookup that throws <see cref="UnauthorizedAccessException"/> / <see cref="Win32Exception"/>
    /// (Access Denied reading another account's process) is <see cref="OwnerLiveness.Unreadable"/>, never "dead".
    /// </summary>
    public OwnerLiveness GetOwnerLiveness(Func<int, DateTime?> startTimeUtcOf)
    {
        DateTime? start;

        try
        {
            start = startTimeUtcOf(OwnerPid);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception)
        {
            return OwnerLiveness.Unreadable;
        }

        if (start == null) return OwnerLiveness.Dead;

        return Math.Abs(start.Value.Ticks - OwnerStartUtcTicks) <= StartTimeTolerance.Ticks ? OwnerLiveness.Alive : OwnerLiveness.Dead;
    }

    /// <summary>True unless the owner is provably gone: an unreadable owner counts as running so a live upgrade is never undermined.</summary>
    public bool IsOwnerAlive(Func<int, DateTime?> startTimeUtcOf) => GetOwnerLiveness(startTimeUtcOf) != OwnerLiveness.Dead;
}

/// <summary>The probes a re-assert pass runs against: the owner-start lookup, the <c>sc failureflag</c> call and the wait between watcher polls.</summary>
public sealed record ScmReassertionOps(Func<int, DateTime?> StartTimeUtcOf, Func<string, bool, int> SetFailureFlag, Func<TimeSpan, CancellationToken, Task> Delay);

public enum OwnerLiveness
{
    Dead,
    Alive,
    Unreadable
}

/// <summary>
/// Self-heals the SCM recovery settings after a killed upgrade. The upgrade turns the failure-actions flag off for
/// the swap window and only its <c>finally</c> turns it back on; a hard kill skips that and a fatal agent error then
/// never auto-restarts again. At startup the agent looks for markers whose owning upgrade is no longer running and
/// puts the flag back to the value the upgrade recorded (NOT blindly to 1, so an operator's deliberate 0 survives).
/// An upgrade that is still running is left alone (it is deliberately holding the flag off) but is WATCHED: the new
/// agent is started by the live upgrade script, so if that script is killed afterwards nothing else would ever
/// re-check; the watcher re-evaluates the marker when the owner exits.
/// </summary>
public static class ScmRecoveryReassertion
{
    /// <summary>How often a live owner is re-probed. One process lookup; bounded by the owner's lifetime and the agent's.</summary>
    public static readonly TimeSpan OwnerPollInterval = TimeSpan.FromSeconds(5);

    private enum MarkerOutcome
    {
        Restored,
        Settled,
        OwnerRunning
    }

    private sealed record Pass(int Restored, List<string> Pending);

    /// <summary>One synchronous pass: restores every orphaned marker in <paramref name="directory"/>; returns how many flags were restored. Never throws.</summary>
    public static int Run(string directory, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag) => RunPass(directory, startTimeUtcOf, setFailureFlag).Restored;

    /// <summary>One pass, then waits for every live owner to exit and re-checks its marker. Returns the total flags restored; ends when no marker is pending, or throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.</summary>
    public static Task<int> RunAndWatchAsync(string directory, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
        => RunAndWatchAsync(directory, new ScmReassertionOps(startTimeUtcOf, setFailureFlag, delay), () => { }, ct);

    /// <summary>As above, and invokes <paramref name="firstPassDone"/> once the first synchronous pass has settled every orphaned marker (before it starts watching live owners).</summary>
    public static async Task<int> RunAndWatchAsync(string directory, ScmReassertionOps ops, Action firstPassDone, CancellationToken ct)
    {
        var pass = RunPass(directory, ops.StartTimeUtcOf, ops.SetFailureFlag);

        firstPassDone();

        var watched = await Task.WhenAll(pass.Pending.Select(path => WatchOwnerAsync(path, ops.StartTimeUtcOf, ops.SetFailureFlag, ops.Delay, ct))).ConfigureAwait(false);

        return pass.Restored + watched.Sum();
    }

    /// <summary>Production wiring: the upgrade state directory, real process lookup, real <c>sc failureflag</c>.</summary>
    public static Task<int> RunForThisHostAsync(Action firstPassDone, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            firstPassDone();
            return Task.FromResult(0);
        }

        var directory = Path.Combine(PlatformPaths.GetSystemConfigDir(), FailureFlagMarker.UpgradeSubDirectory);

        return RunAndWatchAsync(directory, new ScmReassertionOps(StartTimeUtcOfProcess, WindowsServiceHost.SetFailureFlag, Task.Delay), firstPassDone, ct);
    }

    private static Pass RunPass(string directory, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag)
    {
        var pending = new List<string>();
        var restored = 0;

        try
        {
            if (!Directory.Exists(directory)) return new Pass(0, pending);

            foreach (var path in Directory.GetFiles(directory, FailureFlagMarker.FilePrefix + "*" + FailureFlagMarker.FileExtension))
            {
                var outcome = EvaluateMarker(path, startTimeUtcOf, setFailureFlag);

                if (outcome == MarkerOutcome.Restored) restored++;
                if (outcome == MarkerOutcome.OwnerRunning) pending.Add(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SCM recovery re-assert skipped (best-effort)");
        }

        return new Pass(restored, pending);
    }

    private static async Task<int> WatchOwnerAsync(string path, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        Log.Information("An upgrade is holding the SCM failure-actions flag off; watching it so the flag is restored if it dies ({Marker})", path);

        while (true)
        {
            await delay(OwnerPollInterval, ct).ConfigureAwait(false);

            var outcome = EvaluateMarker(path, startTimeUtcOf, setFailureFlag);

            if (outcome != MarkerOutcome.OwnerRunning) return outcome == MarkerOutcome.Restored ? 1 : 0;
        }
    }

    /// <summary>Isolated per marker: nothing a single marker does may stop the others from being evaluated.</summary>
    private static MarkerOutcome EvaluateMarker(string path, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag)
    {
        try
        {
            return RestoreIfOrphaned(path, startTimeUtcOf, setFailureFlag);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SCM recovery re-assert failed for marker {Marker}; will retry at next start", path);
            return MarkerOutcome.Settled;
        }
    }

    private static MarkerOutcome RestoreIfOrphaned(string path, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag)
    {
        if (!TryReadMarker(path, out var marker)) return MarkerOutcome.Settled;
        if (marker.IsOwnerAlive(startTimeUtcOf)) return MarkerOutcome.OwnerRunning;

        // A newer upgrade may have replaced the marker while the owner lookup ran; its swap window is not ours to undo.
        if (!StillHolds(path, marker, out var current)) return current == null ? MarkerOutcome.Settled : MarkerOutcome.OwnerRunning;

        var exit = setFailureFlag(marker.Service, marker.OriginalFlag == 1);

        if (exit != 0)
        {
            Log.Warning("Could not restore SCM failure-actions flag for {Service} (sc.exe exit {Exit}); will retry at next start", marker.Service, exit);
            return MarkerOutcome.Settled;
        }

        Log.Information("Restored SCM failure-actions flag for {Service} to {Flag} after an interrupted upgrade", marker.Service, marker.OriginalFlag);

        return SettleAfterRestore(path, marker, startTimeUtcOf, setFailureFlag);
    }

    /// <summary>
    /// Deletes the marker only while it still names the owner this restore was for. If a newer upgrade replaced it during the
    /// restore, that upgrade's marker is kept and its suspension re-asserted (the restore may have landed after its
    /// <c>failureflag 0</c>), and the marker is watched like any live owner's.
    /// </summary>
    private static MarkerOutcome SettleAfterRestore(string path, FailureFlagMarker restoredFor, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag)
    {
        if (StillHolds(path, restoredFor, out var current))
        {
            TryDeleteMarker(path);
            return MarkerOutcome.Restored;
        }

        if (current == null) return MarkerOutcome.Restored;

        Log.Information("A newer upgrade took over the SCM failure-actions marker for {Service} during the restore; keeping its marker and its suspension", current.Service);

        if (current.IsOwnerAlive(startTimeUtcOf)) setFailureFlag(current.Service, false);

        return MarkerOutcome.OwnerRunning;
    }

    /// <summary>True while the file still holds <paramref name="expected"/>'s owner; <paramref name="current"/> is what is there now (null when gone or unreadable).</summary>
    private static bool StillHolds(string path, FailureFlagMarker expected, out FailureFlagMarker current)
    {
        if (!TryReadMarker(path, out current)) return false;

        return current.HasSameOwnerAs(expected);
    }

    private static void TryDeleteMarker(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Restored the SCM flag but could not remove marker {Marker}; the next start re-applies the same value", path);
        }
    }

    private static bool TryReadMarker(string path, out FailureFlagMarker marker)
    {
        marker = null;

        try
        {
            return FailureFlagMarker.TryParse(File.ReadAllText(path), out marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>null = no such process (gone). Access Denied (<see cref="Win32Exception"/>) is deliberately NOT caught: the marker reads it as "unreadable", i.e. possibly alive.</summary>
    internal static DateTime? StartTimeUtcOfProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
