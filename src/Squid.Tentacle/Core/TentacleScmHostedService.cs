using Microsoft.Extensions.Hosting;
using Serilog;

namespace Squid.Tentacle.Core;

/// <summary>
/// <see cref="BackgroundService"/> that runs the
/// <see cref="TentacleEntry.RunAsync"/> command pipeline under
/// <see cref="Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime"/>.
/// Used ONLY when SCM launches the binary
/// (<see cref="TentacleEntry.ShouldRunUnderScm"/> returns true).
///
/// <para><b>The lifetime contract</b>: when the service binary is started
/// by SCM:
/// <list type="number">
///   <item><c>WindowsServiceLifetime</c> calls <c>StartServiceCtrlDispatcher</c>
///         + registers the SCM control handler. SCM transitions to
///         <c>START_PENDING</c>.</item>
///   <item>Host build completes; <c>WindowsServiceLifetime.OnStarted</c>
///         fires; SCM transitions to <c>RUNNING</c>.</item>
///   <item>This service's <see cref="ExecuteAsync"/> runs in the
///         background. It calls <c>TentacleEntry.RunAsync</c> with
///         <paramref name="stoppingToken"/> as the CT.</item>
///   <item>When SCM sends Stop (operator runs <c>sc stop</c>, host
///         reboots, etc.), <c>WindowsServiceLifetime</c> cancels the
///         host's CT, which in turn cancels <paramref name="stoppingToken"/>.
///         <c>TentacleEntry.RunAsync</c> sees the cancellation, the
///         agent's polling loop drains in-flight work cleanly, and
///         <see cref="ExecuteAsync"/> returns.</item>
///   <item>Host shutdown completes; SCM transitions to <c>STOPPED</c>.</item>
/// </list></para>
///
/// <para><b>Argument propagation</b>: the args passed to <c>Main</c>
/// (e.g. <c>["run", "--instance", "production"]</c> from the SCM-
/// installed unit's <c>binPath</c>) are captured at construction so
/// <c>ExecuteAsync</c> can pass them verbatim to
/// <c>TentacleEntry.RunAsync</c>.</para>
///
/// <para><b>Exit code propagation</b>: the host doesn't return an exit
/// code from <c>RunAsync</c> directly. We capture the result of
/// <c>TentacleEntry.RunAsync</c> in <see cref="LastExitCode"/> so
/// <c>Program.cs</c>'s SCM branch can return it. Default is 0 — set on
/// successful drain; non-zero only if the command pipeline itself
/// returned non-zero (which would be unusual for <c>run</c> since it's
/// long-running).</para>
/// </summary>
public sealed class TentacleScmHostedService : BackgroundService
{
    private readonly string[] _args;
    private readonly IHostApplicationLifetime _lifetime;

    public TentacleScmHostedService(string[] args, IHostApplicationLifetime lifetime)
    {
        _args = args ?? throw new ArgumentNullException(nameof(args));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    /// <summary>
    /// Captured exit code from the command pipeline. Read by
    /// <c>Program.cs</c>'s SCM branch after the host returns. Default 0
    /// (treated as success on clean shutdown).
    /// </summary>
    public int LastExitCode { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Diagnostic file logging — see Program.ScmDiagnosticLog. Critical
        // for debugging SCM start failures since SCM-launched binaries
        // have no console for Serilog Console sink to write to.
        try { System.IO.File.AppendAllText(ResolveDiagPath(), $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] TentacleScmHostedService.ExecuteAsync entered.{Environment.NewLine}"); } catch { }

        Log.Information("Squid Tentacle launched under SCM lifetime — args: [{Args}]",
            string.Join(", ", _args));

        try
        {
            LastExitCode = await TentacleEntry.RunAsync(_args, stoppingToken).ConfigureAwait(false);
            try { System.IO.File.AppendAllText(ResolveDiagPath(), $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] TentacleEntry.RunAsync returned exitCode={LastExitCode}.{Environment.NewLine}"); } catch { }
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText(ResolveDiagPath(), $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] TentacleEntry.RunAsync threw {ex.GetType().Name}: {ex.Message}{Environment.NewLine}"); } catch { }
            Log.Fatal(ex, "Squid Tentacle SCM-hosted service crashed");
            LastExitCode = 1;
        }
        finally
        {
            // Trigger host shutdown so SCM transitions out of RUNNING
            // even if the command pipeline finished early (e.g. `run`
            // exited cleanly without a Stop signal — uncommon for the
            // long-running run command but possible for malformed
            // configs that fail-fast).
            _lifetime.StopApplication();
        }
    }

    private static string ResolveDiagPath()
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
