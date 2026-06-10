using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Squid.Tentacle.Core;
using Serilog;

// Direct ALL Serilog output to stderr (Unix convention: diagnostic
// log lines on stderr, command output on stdout). This keeps stdout
// clean for shell pipelines like:
//
//   THUMBPRINT=$(squid-tentacle show-thumbprint)
//   squid-tentacle list-instances | grep MyInstance
//   squid-tentacle show-config | awk '/Roles:/ {print $2}'
//
// Without this, Serilog's INF lines from TentacleCertificateManager.
// LoadOrCreateCertificate (and other config-load paths) bleed into
// stdout, polluting the captured value. Caught by Linux D1h E2E first
// runner — the test had to defensively regex-extract a 40-char hex
// from stdout to work around the leak. With this fix, stdout for
// read-only diagnostic commands is exactly the value (clean for
// `$()` capture); operators who want the diagnostic context can
// still see it via `2>&1` redirect.
//
// Affects: every command. Stdout-only commands (`version`,
// `show-thumbprint`, `show-config`, `list-instances`,
// `new-certificate`) gain clean pipeline UX. State-mutating commands
// (`register`, `service install`) still emit their summary output
// (MachineId, etc.) to stdout via Console.WriteLine — log noise just
// moves to stderr where it belongs.
// Resolve the launch mode ONCE: a Windows-SCM-launched service has its console
// attached to NUL, so it gets a persistent rolling FILE sink (otherwise its
// entire runtime log is lost). Short-lived CLI commands stay console-only.
// Reused below to drive the SCM lifetime branch.
var runsUnderScm = TentacleEntry.ShouldRunUnderScm(args, OperatingSystem.IsWindows(), WindowsServiceHelpers.IsWindowsService);

Log.Logger = TentacleLogging.BuildLoggerConfiguration(addPersistentFileSink: runsUnderScm).CreateLogger();

try
{
    // SCM-detection branch — when launched by Windows SCM (`sc start`),
    // hand off to the host pipeline so SCM's StartServiceCtrlDispatcher
    // contract is honored. Without this, SCM-launched start times out
    // at ERROR_SERVICE_REQUEST_TIMEOUT after 30s because the binary
    // never registers a service control handler. See
    // TentacleEntry.ShouldRunUnderScm + TentacleScmHostedService for
    // the full SCM lifetime story.
    //
    // Console mode (interactive CLI, register, service install, etc.)
    // — including SSH-launched `run` for systemd's ExecStart on Linux
    // — falls through to the existing Console-CT path below.
    if (runsUnderScm)
        return await RunUnderScmLifetimeAsync(args).ConfigureAwait(false);

    // Console mode — existing flow. Console.CancelKeyPress + ProcessExit
    // drive the CT so Ctrl-C on an interactive run + systemd stop on
    // Linux both trigger graceful drain.
    //
    // IMPORTANT: do NOT wrap consoleCts in `using var`. PR #274's first-
    // runner CI surfaced that a `using var` here breaks every short-lived
    // CLI invocation (`version`, `show-thumbprint`, etc.) with exit 134:
    //   1. `using var` disposes the CTS when the try block returns
    //   2. ProcessExit handler then fires during runtime shutdown
    //   3. Handler's `consoleCts.Cancel()` throws ObjectDisposedException
    //   4. Unhandled exception → process aborts with 134 instead of 0
    // The original (pre-#274) Program.cs used a non-using CTS that lived
    // until process exit — restored here. Belt-and-suspenders: handlers
    // also catch ObjectDisposedException as defence against any future
    // code that disposes the CTS earlier.
    var consoleCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        try { consoleCts.Cancel(); }
        catch (ObjectDisposedException) { /* race vs shutdown — handler fired post-Dispose */ }
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        try { consoleCts.Cancel(); }
        catch (ObjectDisposedException) { /* race vs shutdown */ }
    };

    return await TentacleEntry.RunAsync(args, consoleCts.Token).ConfigureAwait(false);
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static async Task<int> RunUnderScmLifetimeAsync(string[] args)
{
    // SCM-launched binaries have no console to write to; Serilog.Console
    // sink writes go nowhere. For diagnostic visibility into where SCM
    // start hangs, ALSO write progress events to a known file path that
    // operators / CI can grab on failure.
    ScmDiagnosticLog.WriteLine($"=== SCM lifetime entry @ {DateTimeOffset.UtcNow:O} args=[{string.Join(", ", args)}] ===");
    Log.Information("SCM-launched mode detected — building host with WindowsServiceLifetime");

    try
    {
        // Switched from `Host.CreateApplicationBuilder` (.NET 8+ minimal)
        // to `Host.CreateDefaultBuilder().UseWindowsService()` (legacy)
        // for SCM compatibility. Empirical first-runner CI evidence (PR
        // #276 closed) showed that with `Host.CreateApplicationBuilder` +
        // `services.AddWindowsService()`, SCM `sc start` consistently
        // times out at 30s with `[SC] StartService FAILED 1053:` —
        // service never transitions to RUNNING.
        //
        // The legacy builder's `.UseWindowsService()` extension does
        // `services.RemoveAll<IHostLifetime>()` BEFORE registering
        // WindowsServiceLifetime, ensuring no ConsoleLifetime
        // initialization can interfere on a process with no console.
        // The new `services.AddWindowsService()` does NOT remove the
        // pre-registered ConsoleLifetime, so ConsoleLifetime's
        // initialization paths run too — likely the source of the
        // hang on a no-console SCM-launched binary.
        ScmDiagnosticLog.WriteLine("Building host via Host.CreateDefaultBuilder().UseWindowsService() ...");

        // Capture the BackgroundService instance via factory closure —
        // can't read it from host.Services AFTER host.RunAsync because
        // the host's IServiceProvider is disposed by then (round-1
        // diagnostic harvest caught this — Program.cs:144 threw
        // ObjectDisposedException reading host.Services post-RunAsync).
        TentacleScmHostedService? capturedService = null;

        using var host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                // Service name is set by SCM at install time (sc.exe
                // create --servicename). The Description here is for
                // host's diagnostic output only.
                options.ServiceName = "Squid.Tentacle";
            })
            .ConfigureServices((_, services) =>
            {
                // BackgroundService that runs TentacleEntry.RunAsync
                // under the SCM lifetime's CT. Factory captures the
                // resolved IHostApplicationLifetime + the original
                // args from Main, AND assigns to capturedService so
                // we can read LastExitCode AFTER host disposal.
                services.AddHostedService(sp =>
                {
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    capturedService = new TentacleScmHostedService(args, lifetime);
                    return capturedService;
                });
            })
            .Build();
        ScmDiagnosticLog.WriteLine("Host built. Calling host.RunAsync — WindowsServiceLifetime now drives SCM state machine.");

        await host.RunAsync().ConfigureAwait(false);
        ScmDiagnosticLog.WriteLine("host.RunAsync returned (SCM Stop received + drained).");

        // Read captured exit code from the closure-captured instance —
        // safe to read even after host disposal because the field outlives
        // the host (only the host's IServiceProvider is disposed; the
        // BackgroundService instance itself is still in memory).
        var exitCode = capturedService?.LastExitCode ?? 0;
        ScmDiagnosticLog.WriteLine($"Captured exit code: {exitCode}");
        return exitCode;
    }
    catch (Exception ex)
    {
        ScmDiagnosticLog.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        Log.Fatal(ex, "RunUnderScmLifetimeAsync crashed");
        return 1;
    }
}

/// <summary>
/// Append-only diagnostic log writer for the SCM-launched code path.
/// SCM-launched binaries have no console; Serilog Console sink writes
/// go to NUL. This writer pipes critical-path events to a known file
/// (<c>%ProgramData%\Squid\Tentacle\scm-diagnostic.log</c>) so
/// operators / CI can grab the file on failure to see where SCM start
/// hung.
///
/// <para>Writes are best-effort and never throw. If the path can't be
/// created (e.g. permission denied), writes silently no-op so the SCM
/// lifetime path itself is never disrupted by the logger.</para>
/// </summary>
static class ScmDiagnosticLog
{
    private static readonly string LogPath = ResolvePath();
    private static readonly object Lock = new();

    public static string Path => LogPath;

    public static void WriteLine(string line)
    {
        try
        {
            lock (Lock)
            {
                // Single-generation rotation so this append-only diagnostic can't
                // grow unbounded across thousands of service restarts.
                TentacleLogging.RotateIfOversized(LogPath, TentacleLogging.ScmDiagnosticMaxBytes);

                System.IO.File.AppendAllText(
                    LogPath,
                    $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort — never throw from diagnostic logging.
        }
    }

    private static string ResolvePath()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(programData))
                programData = System.IO.Path.GetTempPath();

            var dir = System.IO.Path.Combine(programData, "Squid", "Tentacle");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "scm-diagnostic.log");
        }
        catch
        {
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "squid-tentacle-scm-diagnostic.log");
        }
    }
}
