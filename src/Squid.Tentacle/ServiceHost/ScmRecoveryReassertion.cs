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

    /// <summary>True while the upgrade process that wrote the marker is still running (same PID AND same start time, so a recycled PID does not count).</summary>
    public bool IsOwnerAlive(Func<int, DateTime?> startTimeUtcOf)
    {
        var start = startTimeUtcOf(OwnerPid);

        if (start == null) return false;

        return Math.Abs(start.Value.Ticks - OwnerStartUtcTicks) <= StartTimeTolerance.Ticks;
    }
}

/// <summary>
/// Self-heals the SCM recovery settings after a killed upgrade. The upgrade turns the failure-actions flag off for
/// the swap window and only its <c>finally</c> turns it back on; a hard kill skips that and a fatal agent error then
/// never auto-restarts again. At startup the agent looks for markers whose owning upgrade is no longer running and
/// puts the flag back to the value the upgrade recorded (NOT blindly to 1, so an operator's deliberate 0 survives).
/// An upgrade that is still running is left alone: it is deliberately holding the flag off.
/// </summary>
public static class ScmRecoveryReassertion
{
    /// <summary>Restores every orphaned marker in <paramref name="directory"/>; returns how many flags were restored. Never throws.</summary>
    public static int Run(string directory, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag)
    {
        try
        {
            if (!Directory.Exists(directory)) return 0;

            return Directory.GetFiles(directory, FailureFlagMarker.FilePrefix + "*" + FailureFlagMarker.FileExtension).Count(path => RestoreIfOrphaned(path, startTimeUtcOf, setFailureFlag));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SCM recovery re-assert skipped (best-effort)");
            return 0;
        }
    }

    /// <summary>Production wiring: the upgrade state directory, real process lookup, real <c>sc failureflag</c>.</summary>
    public static int RunForThisHost()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        var directory = Path.Combine(PlatformPaths.GetSystemConfigDir(), FailureFlagMarker.UpgradeSubDirectory);

        return Run(directory, StartTimeUtcOfProcess, WindowsServiceHost.SetFailureFlag);
    }

    private static bool RestoreIfOrphaned(string path, Func<int, DateTime?> startTimeUtcOf, Func<string, bool, int> setFailureFlag)
    {
        if (!TryReadMarker(path, out var marker)) return false;
        if (marker.IsOwnerAlive(startTimeUtcOf)) return false;

        var exit = setFailureFlag(marker.Service, marker.OriginalFlag == 1);

        if (exit != 0)
        {
            Log.Warning("Could not restore SCM failure-actions flag for {Service} (sc.exe exit {Exit}); will retry at next start", marker.Service, exit);
            return false;
        }

        File.Delete(path);
        Log.Information("Restored SCM failure-actions flag for {Service} to {Flag} after an interrupted upgrade", marker.Service, marker.OriginalFlag);
        return true;
    }

    private static bool TryReadMarker(string path, out FailureFlagMarker marker)
    {
        marker = null;

        try
        {
            return FailureFlagMarker.TryParse(File.ReadAllText(path), out marker);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static DateTime? StartTimeUtcOfProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
