using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Squid.Tentacle.Core;
using Serilog;
using Serilog.Events;

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
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        standardErrorFromLevel: LogEventLevel.Verbose,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

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
    if (TentacleEntry.ShouldRunUnderScm(args, OperatingSystem.IsWindows(), WindowsServiceHelpers.IsWindowsService))
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
    Log.Information("SCM-launched mode detected — building host with WindowsServiceLifetime");

    var builder = Host.CreateApplicationBuilder(args);

    // Register WindowsServiceLifetime — IHostLifetime that:
    //   1. Calls StartServiceCtrlDispatcher on host startup
    //   2. Transitions SCM state machine: START_PENDING → RUNNING
    //   3. Listens for SCM Stop signal → cancels the host's CT
    //   4. Transitions SCM state machine: STOP_PENDING → STOPPED
    builder.Services.AddWindowsService(options =>
    {
        // Service name is set by SCM at install time (via sc.exe create
        // --servicename). The Description here is for the host's diagnostic
        // output only — SCM uses what's in the registry.
        options.ServiceName = "Squid.Tentacle";
    });

    // The hosted service that runs the command pipeline under the SCM
    // lifetime's CT. Captures the exit code on completion.
    var hostedService = new TentacleScmHostedService(args, null!);  // Lifetime injected post-build
    builder.Services.AddSingleton(hostedService);
    builder.Services.AddHostedService<TentacleScmHostedService>(sp =>
    {
        // Re-construct with the resolved IHostApplicationLifetime —
        // we couldn't resolve at AddSingleton time because the host
        // is still being built.
        var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
        var configured = new TentacleScmHostedService(args, lifetime);
        return configured;
    });

    using var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);

    // Resolve the hosted service to read its captured exit code. Default
    // 0 if the command pipeline finished cleanly via Stop signal.
    var registeredService = host.Services.GetServices<IHostedService>()
        .OfType<TentacleScmHostedService>()
        .FirstOrDefault();
    return registeredService?.LastExitCode ?? 0;
}
