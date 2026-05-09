using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Instance;
using Serilog;

namespace Squid.Tentacle.Core;

/// <summary>
/// The shared command-dispatch entry-point used by BOTH the console
/// (interactive CLI) launch path AND the Windows SCM-launched service
/// path. Extracted from <c>Program.cs</c> so SCM mode can run the same
/// command pipeline under a host-managed cancellation token (so SCM's
/// Stop signal cleanly cancels the run).
///
/// <para><b>Why a separate entry method (vs leaving the body in
/// Program.cs)</b>: SCM mode wraps the run in
/// <see cref="Microsoft.Extensions.Hosting.IHost"/> with
/// <see cref="Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime"/>
/// — that lifetime owns the cancellation token and triggers it on SCM
/// Stop. The host-controlled CT is what we thread into command execution
/// so a service-stop request actually drains in-flight work cleanly.
/// Leaving the body inline in Program.cs would force every SCM-aware
/// branch to duplicate it.</para>
///
/// <para><b>Behavior is byte-identical between paths</b>: the same
/// CommandResolver, the same config-loading order, the same exception
/// handling. The only difference is which CT cancellation source
/// (Console.CancelKeyPress vs SCM-Stop) drives the token.</para>
/// </summary>
public static class TentacleEntry
{
    /// <summary>
    /// The build-in command set the binary supports. Kept as a static
    /// property (vs constructed in Program.cs) so the SCM-detection seam
    /// can resolve the route without re-instantiating the command list.
    /// </summary>
    public static ITentacleCommand[] BuildCommands() =>
    [
        new RunCommand(),
        new ShowThumbprintCommand(),
        new ShowConfigCommand(),
        new CheckServicesCommand(),
        new NewCertificateCommand(),
        new RegisterCommand(),
        new ServiceCommand(),
        new CreateInstanceCommand(),
        new ListInstancesCommand(),
        new DeleteInstanceCommand(),
        new VersionCommand()
    ];

    /// <summary>
    /// The shared run-the-command-pipeline body. Identical to what
    /// Program.cs previously did inline — extracted so SCM mode can call
    /// it with the host-managed CT.
    /// </summary>
    /// <param name="args">CLI args as received from Main / Host.</param>
    /// <param name="ct">Cancellation token (from Console.CancelKeyPress
    /// in console mode; from <see cref="Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime"/>
    /// in SCM mode).</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var commands = BuildCommands();

        try
        {
            var route = CommandResolver.Resolve(commands, args);

            if (route.HelpRequested)
            {
                PrintHelp(commands);
                return 0;
            }

            if (route.UnknownCommand != null)
            {
                Console.Error.WriteLine($"Unknown command: {route.UnknownCommand}");
                PrintHelp(commands);
                return 1;
            }

            // Extract --instance NAME (the instance-aware commands need to know which config to load
            // and which certs dir to use). Remove it from the arg array before the rest of the pipeline
            // sees it, so ConfigurationBuilder.AddCommandLine doesn't choke on an unknown key.
            var (instanceName, argsAfterInstance) = InstanceSelector.ExtractInstanceArg(route.RemainingArgs);

            var configBuilder = new ConfigurationBuilder();

            // Priority (low → high): per-instance config.json → appsettings.json → env vars → CLI args.
            // This lets `register` persist to the config file and `systemd run` / `sc start` pick it up,
            // while still allowing env vars / CLI args to override for debugging or Docker-style launches.
            var instanceConfigPath = TryGetInstanceConfigPath(instanceName);
            if (instanceConfigPath != null)
                configBuilder.AddJsonFile(instanceConfigPath, optional: true, reloadOnChange: false);

            configBuilder.AddJsonFile("appsettings.json", optional: true);
            configBuilder.AddEnvironmentVariables();
            configBuilder.AddCommandLine(argsAfterInstance);

            var config = configBuilder.Build();

            return await route.Command.ExecuteAsync(route.RemainingArgs, config, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Squid Tentacle shutdown requested");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Squid Tentacle terminated unexpectedly");
            // Also write to the SCM diagnostic file — when SCM-launched
            // (no console), Serilog's Console sink writes go to NUL and
            // operators / CI lose all signal about WHY the run command
            // failed. The diagnostic file path is the same one Program.
            // ScmDiagnosticLog uses; duplicated here as a small static
            // helper because we can't reference internal types from
            // top-level Program.cs.
            TryWriteScmDiagnostic($"TentacleEntry.RunAsync caught {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }

    /// <summary>
    /// Best-effort append to the SCM diagnostic log file used by
    /// <c>Program.ScmDiagnosticLog</c>. Mirrors that helper so this
    /// class can write to it from its catch block. Never throws.
    /// </summary>
    private static void TryWriteScmDiagnostic(string line)
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(programData)) programData = Path.GetTempPath();
            var dir = Path.Combine(programData, "Squid", "Tentacle");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "scm-diagnostic.log");
            File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Pure SCM-detection seam: should we hand off to
    /// <see cref="Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime"/>
    /// before running the command pipeline?
    ///
    /// <para>True when ALL of:
    /// <list type="bullet">
    ///   <item>Running on Windows</item>
    ///   <item>Process was launched by SCM (per
    ///         <c>WindowsServiceHelpers.IsWindowsService()</c> — checks
    ///         the parent process is <c>services.exe</c>)</item>
    ///   <item>The CLI command being invoked is the long-running
    ///         <c>run</c> command (other commands like <c>register</c>,
    ///         <c>service install</c>, <c>show-thumbprint</c> are short-
    ///         lived and don't need SCM lifetime)</item>
    /// </list></para>
    ///
    /// <para><b>Why isolate the check</b>: lets unit tests verify the
    /// detection logic on any platform without needing a real SCM. The
    /// <paramref name="isLaunchedBySCM"/> parameter is the seam — production
    /// passes <c>WindowsServiceHelpers.IsWindowsService</c>; tests pass
    /// a deterministic predicate.</para>
    /// </summary>
    public static bool ShouldRunUnderScm(string[] args, bool isWindows, Func<bool> isLaunchedBySCM)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(isLaunchedBySCM);

        if (!isWindows) return false;

        // Resolve the command without side effects so we can short-circuit
        // for non-`run` invocations (registry queries, sc.exe one-shots
        // like service install, show-config, etc.). Those commands are
        // sub-second; no SCM lifetime needed.
        var commands = BuildCommands();
        var route = CommandResolver.Resolve(commands, args);
        if (route.Command is not RunCommand) return false;

        return isLaunchedBySCM();
    }

    private static string TryGetInstanceConfigPath(string instanceName)
    {
        try
        {
            var record = InstanceSelector.Resolve(instanceName);
            return new TentacleConfigFile(record.ConfigPath).Exists() ? record.ConfigPath : null;
        }
        catch
        {
            // Instance lookup failures are non-fatal at startup — commands that actually need
            // an instance (register, run) will fail with a clear error later.
            return null;
        }
    }

    private static void PrintHelp(ITentacleCommand[] commands)
    {
        Console.WriteLine("Squid Tentacle — Deployment Agent");
        Console.WriteLine();
        Console.WriteLine("Usage: squid-tentacle <command> [--instance NAME] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");

        foreach (var cmd in commands)
            Console.WriteLine($"  {cmd.Name,-20} {cmd.Description}");

        Console.WriteLine($"  {"help",-20} Show this help message");
        Console.WriteLine();
        Console.WriteLine("Instance management (multiple Tentacles on one host):");
        Console.WriteLine("  squid-tentacle create-instance --instance production");
        Console.WriteLine("  squid-tentacle list-instances");
        Console.WriteLine("  squid-tentacle delete-instance --instance production");
        Console.WriteLine();
        Console.WriteLine("Install + register + run (typical flow):");
        Console.WriteLine("  squid-tentacle register --server URL --api-key KEY --role R --environment E");
        Console.WriteLine("  sudo squid-tentacle service install");
        Console.WriteLine();
        Console.WriteLine("Configuration sources (low → high): instance config → appsettings.json → env (Tentacle__Key) → CLI (--Tentacle:Key=V)");
    }
}
