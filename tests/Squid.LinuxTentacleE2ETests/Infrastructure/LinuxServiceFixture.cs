using System.Diagnostics;
using System.Text;

namespace Squid.LinuxTentacleE2ETests.Infrastructure;

/// <summary>
/// Phase 12.L.E.3 — Linux systemd analog of <c>WindowsServiceFixture</c>.
/// Wraps <c>systemctl</c> + <c>sudo</c> for install + start + stop +
/// uninstall of a transient systemd service, used by lifecycle E2E tests
/// that need a real running service to drive the upgrade .sh's Phase B
/// (Stop → swap → Start) against.
///
/// <para><b>Why an explicit fixture (vs systemd-run --scope ad-hoc)</b>:
/// upgrade-linux-tentacle.sh's Phase B does <c>systemctl restart $SERVICE_NAME</c>,
/// which only works against a properly-installed unit (not an
/// ad-hoc transient run). Tests need to install a real .service file +
/// daemon-reload + start, which is what this fixture does.</para>
///
/// <para><b>Sudo handling</b>: GHA <c>ubuntu-latest</c> runners have
/// passwordless sudo. The fixture invokes <c>sudo systemctl ...</c> +
/// <c>sudo cp ...</c> directly. Local Linux dev hosts that don't have
/// passwordless sudo configured will see <see cref="IsAvailable"/>
/// return false (probed via a no-op sudo call at fixture-construction
/// time).</para>
///
/// <para><b>Cleanup discipline (Rule 12.3)</b>: <see cref="Dispose"/>
/// is best-effort and idempotent — <c>systemctl stop</c> + <c>disable</c>
/// + <c>rm</c> all swallow errors so a partial install or a Stop on a
/// not-running service doesn't throw. Even on test-failure paths every
/// staged artefact is reaped: unit file deleted, install dir removed,
/// systemd reloaded.</para>
///
/// <para><b>Skip-on-non-Linux</b>: <see cref="IsAvailable"/> returns false
/// on macOS/Windows dev hosts (no systemd). Tests using this fixture
/// skip-guard via the existing <c>OperatingSystem.IsLinux()</c> pattern.</para>
/// </summary>
public sealed class LinuxServiceFixture : IDisposable
{
    /// <summary>
    /// True only on Linux WITH systemd reachable via systemctl AND
    /// passwordless sudo configured. Probed once at fixture
    /// construction (not at static initialisation — env can change).
    /// Tests guard with this; non-Linux dev hosts no-op-skip.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return false;

            // Probe via `sudo -n true` — `-n` makes sudo fail immediately
            // if a password would be required (vs hanging on the prompt).
            // GHA runners + properly-configured CI hosts return 0; local
            // dev hosts without passwordless sudo return non-zero.
            try
            {
                var psi = new ProcessStartInfo("sudo", "-n true")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(2_000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private readonly string _serviceName;
    private readonly string _installDir;
    private bool _installed;

    /// <summary>The service name registered with systemd (`systemctl status <name>`).</summary>
    public string ServiceName => _serviceName;

    /// <summary>The directory containing the test service script + version.txt + marker.</summary>
    public string InstallDir => _installDir;

    /// <summary>The full path to the test service script inside <see cref="InstallDir"/>.</summary>
    public string ServiceScriptPath => Path.Combine(_installDir, "squid-linux-test-service.sh");

    /// <summary>The path to the version.txt file the service reads on Start.</summary>
    public string VersionFilePath => Path.Combine(_installDir, "version.txt");

    /// <summary>The path to the marker file the service writes on Start (containing the version it read).</summary>
    public string MarkerFilePath => Path.Combine(_installDir, "service-running.marker");

    /// <summary>Path to the systemd unit file under <c>/etc/systemd/system/</c>.</summary>
    public string UnitFilePath => $"/etc/systemd/system/{_serviceName}.service";

    /// <summary>
    /// Construct against a UNIQUE service name + UNIQUE install dir.
    /// Caller responsible for uniqueness (recommended: GUID-suffix).
    /// </summary>
    public LinuxServiceFixture(string serviceName, string installDir)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("serviceName is required", nameof(serviceName));
        if (string.IsNullOrWhiteSpace(installDir))
            throw new ArgumentException("installDir is required", nameof(installDir));

        _serviceName = serviceName;
        _installDir = installDir;
    }

    /// <summary>
    /// Stage the test service script at <see cref="InstallDir"/> with the
    /// supplied initial version, write a systemd unit file referencing
    /// it, daemon-reload + start, polling until the service reaches
    /// active(running) state OR the timeout expires.
    /// </summary>
    public void InstallAndStart(string testServiceScriptSourcePath, string initialVersion, TimeSpan startTimeout)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("LinuxServiceFixture only runs on Linux with passwordless sudo");

        if (!File.Exists(testServiceScriptSourcePath))
            throw new FileNotFoundException($"test service script not found at: {testServiceScriptSourcePath}");

        // Idempotent cleanup of any stale prior install of the same name.
        TryUninstall();

        // Stage install dir + script + initial version.txt.
        Directory.CreateDirectory(_installDir);
        File.Copy(testServiceScriptSourcePath, ServiceScriptPath, overwrite: true);
        File.WriteAllText(VersionFilePath, initialVersion);
        RunSudo("chmod +x " + Quote(ServiceScriptPath));

        // Write systemd unit file. Type=simple = main process is the
        // ExecStart command (the bash script itself, which sleeps forever).
        // KillMode=mixed sends SIGTERM to main process + SIGKILL to children
        // so our trap-handler runs cleanly on stop.
        var unitContent = new StringBuilder()
            .AppendLine("[Unit]")
            .AppendLine($"Description=Squid Linux Tentacle E2E Test Service — {_serviceName}")
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=simple")
            .AppendLine($"Environment=INSTALL_DIR={_installDir}")
            .AppendLine($"ExecStart=/usr/bin/env bash {ServiceScriptPath}")
            .AppendLine("KillMode=mixed")
            .AppendLine("Restart=no")
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=multi-user.target")
            .ToString();

        var tempUnit = Path.Combine(Path.GetTempPath(), $"{_serviceName}.unit-staging");
        File.WriteAllText(tempUnit, unitContent);
        try
        {
            RunSudo($"cp {Quote(tempUnit)} {Quote(UnitFilePath)}");
            RunSudo($"chmod 644 {Quote(UnitFilePath)}");
        }
        finally
        {
            try { File.Delete(tempUnit); } catch { /* best-effort */ }
        }

        RunSudo("systemctl daemon-reload");
        RunSudo($"systemctl start {Quote(_serviceName)}");

        _installed = true;

        // Poll for active(running) state.
        var deadline = DateTime.UtcNow + startTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsActive()) return;
            Thread.Sleep(200);
        }

        // Capture diagnostic state for the test failure message.
        var status = RunSudoCapture($"systemctl status {Quote(_serviceName)} --no-pager") ?? "(systemctl status unavailable)";
        throw new TimeoutException(
            $"systemd service '{_serviceName}' did not reach active(running) within {startTimeout}. " +
            $"systemctl status:\n{status}");
    }

    /// <summary>
    /// Stop the service (waits up to <paramref name="timeout"/> for it to
    /// reach inactive). Best-effort — silently swallows errors if the
    /// service is already stopped.
    /// </summary>
    public void Stop(TimeSpan timeout)
    {
        if (!_installed) return;

        RunSudo($"systemctl stop {Quote(_serviceName)}");

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsActive()) return;
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// True if `systemctl is-active <name>` returns "active". Consults
    /// systemd directly (vs reading the marker file) so the fixture can
    /// answer "running" even when the marker isn't present yet (start
    /// race window).
    /// </summary>
    public bool IsActive()
    {
        var output = RunSudoCapture($"systemctl is-active {Quote(_serviceName)}");
        return output?.Trim() == "active";
    }

    public void Dispose()
    {
        TryUninstall();
    }

    /// <summary>
    /// Best-effort cleanup. Stops + disables + removes the unit file
    /// + reloads daemon + removes the install dir. Each step swallows
    /// errors — a partial install (e.g. unit file written but daemon-
    /// reload failed) still reaps cleanly.
    /// </summary>
    private void TryUninstall()
    {
        // Order: stop → disable → rm unit → daemon-reload → rm install dir.
        // disable is best-effort even if not enabled; rm unit + reload
        // are idempotent.
        try { RunSudo($"systemctl stop {Quote(_serviceName)}"); } catch { /* not running, fine */ }
        try { RunSudo($"systemctl disable {Quote(_serviceName)}"); } catch { /* not enabled, fine */ }
        try { RunSudo($"rm -f {Quote(UnitFilePath)}"); } catch { /* best-effort */ }
        try { RunSudo("systemctl daemon-reload"); } catch { /* best-effort */ }
        try { RunSudo($"rm -rf {Quote(_installDir)}"); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Runs <c>sudo &lt;command&gt;</c> via a shell, throws on non-zero
    /// exit code with stderr captured. Uses bash -c so the command can
    /// be a multi-token expression (e.g. systemctl args).
    /// </summary>
    private static void RunSudo(string command)
    {
        var psi = new ProcessStartInfo("sudo", "-n bash -c " + Quote(command))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to launch sudo for: {command}");

        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"sudo command failed (exit {proc.ExitCode}): {command}\nstderr:\n{stderr}");
        }
    }

    /// <summary>
    /// Variant of <see cref="RunSudo"/> that captures stdout and returns
    /// it (or null on failure). Used for status probes that should NOT
    /// throw on non-zero exit (e.g. `systemctl is-active` returns 3 if
    /// inactive — that's expected, not an error).
    /// </summary>
    private static string RunSudoCapture(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("sudo", "-n bash -c " + Quote(command))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);
            return stdout;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Single-quote a string for safe inclusion in a bash -c command line.
    /// Escapes embedded single quotes via the standard <c>'\''</c> idiom.
    /// </summary>
    private static string Quote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";
}
