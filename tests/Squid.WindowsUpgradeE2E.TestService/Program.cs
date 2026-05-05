using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Squid.WindowsUpgradeE2E.TestService;

/// <summary>
/// entry point for the test Windows service. Two
/// modes via argv:
/// <list type="bullet">
///   <item><c>--service</c> (default when launched by SCM): runs as a
///         ServiceBase, never returns — SCM controls Start/Stop.</item>
///   <item><c>--probe-version</c>: prints the version that would be
///         reported on Start (reads <c>version.txt</c> next to the exe)
///         and exits. Lets tests verify the on-disk version-file shape
///         WITHOUT actually starting the service.</item>
/// </list>
///
/// <para><b>Why a probe mode</b>: the upgrade pipeline test needs to
/// distinguish "service was started AND read v1" from "service was
/// started AND read v2". The marker file written at Start time is
/// the proof. But the test might also want to verify that the
/// version.txt swap happened correctly without restarting the
/// service. Probe mode lets the test do that without SCM involvement.</para>
///
/// <para><b>Why one binary not two</b>: keeping the production-mode
/// (ServiceBase) and probe-mode in the same exe means the test only
/// needs to manage ONE binary path. The "upgrade" in A-2 swaps the
/// version.txt sibling file — the .exe stays the same. This keeps
/// the test focused on the Phase B file-system mechanics, not on
/// binary-swap mechanics that would be a separate concern.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe-version")
        {
            Console.WriteLine(ReadCurrentVersion());
            return 0;
        }

        // Default: SCM-launched service mode. ServiceBase.Run blocks until
        // the SCM tells the service to stop. Returns int 0 on clean stop.
        ServiceBase.Run(new TestUpgradeService());
        return 0;
    }

    /// <summary>
    /// Reads <c>version.txt</c> from the directory containing this exe.
    /// Public for the service's OnStart handler. Returns
    /// <c>"unknown"</c> if the file is missing or unreadable — the
    /// service still starts, but the marker file records the absence,
    /// which surfaces as a test failure with a clear cause.
    /// </summary>
    public static string ReadCurrentVersion()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var versionFile = Path.Combine(exeDir, "version.txt");
            if (!File.Exists(versionFile)) return "unknown";
            return File.ReadAllText(versionFile).Trim();
        }
        catch
        {
            return "unknown";
        }
    }
}

/// <summary>
/// The actual Windows service. Minimal: writes its version into a marker
/// file on Start; deletes the marker on Stop. Tests verify the marker
/// presence + content as proof of "the service is running, reading
/// version V". The marker location is derived from the exe path so a
/// fixture installing two parallel test services (each at its own
/// install dir) doesn't collide.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TestUpgradeService : ServiceBase
{
    /// <summary>
    /// Pinned per Rule 8: the fixture polls for THIS filename in the
    /// service's exe directory to determine "is the service running and
    /// has it written its version yet?".
    /// </summary>
    public const string MarkerFileName = "service-running.marker";

    public TestUpgradeService()
    {
        ServiceName = "SquidUpgradeE2ETestService";  // overridden by sc.exe at install time
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var markerPath = Path.Combine(exeDir, MarkerFileName);
            File.WriteAllText(markerPath, Program.ReadCurrentVersion());
        }
        catch
        {
            // Swallow — service must not throw from OnStart or SCM rejects it.
            // Marker absence will surface as test failure with clear cause.
        }
    }

    protected override void OnStop()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var markerPath = Path.Combine(exeDir, MarkerFileName);
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }
        catch
        {
            // Best-effort cleanup; SCM doesn't care if Stop has side effects.
        }
    }
}
