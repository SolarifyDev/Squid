using System.Diagnostics;
using System.Text;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Services.Machines.Upgrade.Methods;

namespace Squid.LinuxTentacleE2ETests.Infrastructure;

/// <summary>
/// Phase 12.L.E.4 — Linux analog of the Windows project's
/// <c>UpgradeLifecycleContext</c>. Wraps every OS resource an
/// upgrade-flow E2E test stages so cleanup is best-effort + idempotent
/// (Rule 12.3): systemd unit, install dir, isolated state dir, mirror,
/// staging dir, .bak. Each test creates its own instance via <c>using</c>.
///
/// <para><b>Test isolation strategy</b>: every artefact is GUID-suffixed
/// so concurrent tests don't collide on the global systemd / sudo /
/// filesystem namespace. State dir is redirected via the
/// <see cref="LinuxTentacleUpgradeStrategy.StateDirEnvVar"/> override
/// (added in 12.L.E.2) so <c>last-upgrade.json</c> /
/// <c>upgrade.lock</c> / <c>upgrade-events.jsonl</c> all land under
/// <c>/tmp/test-isolated-{guid}/</c> instead of the production
/// <c>/var/lib/squid-tentacle/</c>.</para>
///
/// <para><b>Skip-on-non-Linux</b>: composes <see cref="LinuxServiceFixture.IsAvailable"/>
/// (Linux + passwordless sudo). Tests using this context call
/// <see cref="IsAvailable"/> first and no-op-skip on non-Linux dev hosts
/// + Linux-without-sudo.</para>
/// </summary>
public sealed class LinuxLifecycleContext : IDisposable
{
    private bool _clean;

    public static bool IsAvailable => LinuxServiceFixture.IsAvailable;

    public LinuxServiceFixture Fixture { get; }
    public LocalReleaseMirror Mirror { get; }

    /// <summary>Path to the test service script under the test source tree.</summary>
    public string TestServiceScript { get; }

    /// <summary>Test-isolated state-dir. The .ps1's STATUS_DIR / LOCK_FILE / etc. all derive from here via the env override.</summary>
    public string StateDirOverride { get; }

    /// <summary>The <c>last-upgrade.json</c> path the .sh writes (under StateDirOverride).</summary>
    public string StatusFilePath => Path.Combine(StateDirOverride, "last-upgrade.json");

    /// <summary>The <c>upgrade.lock</c> path the .sh writes (under StateDirOverride).</summary>
    public string LockFilePath => Path.Combine(StateDirOverride, "upgrade.lock");

    public LinuxLifecycleContext()
    {
        var unique = Guid.NewGuid().ToString("N");

        var serviceName = $"squid-linux-lifecycle-{unique}";
        var installDir = Path.Combine(Path.GetTempPath(), $"squid-linux-lifecycle-install-{unique}");

        Fixture = new LinuxServiceFixture(serviceName, installDir);
        Mirror = LocalReleaseMirror.Start();
        TestServiceScript = LocateTestServiceScript();

        StateDirOverride = Path.Combine(Path.GetTempPath(), $"squid-linux-lifecycle-state-{unique}");
        // The .sh creates this on demand via `sudo mkdir -p`. We pre-create
        // it here only so test code can write to it directly (e.g. pre-staging
        // a stale lock file for E11.u2 tests later).
        Directory.CreateDirectory(StateDirOverride);
    }

    /// <summary>
    /// Renders the production .sh with placeholders pointed at our test
    /// fixtures: DOWNLOAD_URL → mirror, INSTALL_DIR / SERVICE_NAME →
    /// fixture, STATE_DIR → test-isolated dir, INSTALL_METHODS → tarball-
    /// only (skips apt + yum probes which would slow tests + introduce
    /// host-config sensitivity).
    /// </summary>
    public string RenderProductionScriptForVersion(string targetVersion)
    {
        // Set env vars for resolver-driven placeholders. Strategy reads
        // SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL + the new
        // SQUID_TARGET_LINUX_TENTACLE_STATE_DIR (12.L.E.2). HEALTHCHECK_URL
        // also resolver-driven; default is fine because the test service
        // doesn't expose HTTP and we want the "warning + proceed" path.
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, Mirror.BaseUri.ToString().TrimEnd('/'));
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, StateDirOverride);

        // Tarball-only method order. Skips apt+yum probes which would
        // otherwise slow tests + introduce host-config sensitivity (apt's
        // dpkg-query / yum's rpm -q on systems where these tools may or
        // may not have squid-tentacle installed).
        var tarballOnly = new ILinuxUpgradeMethod[] { new TarballUpgradeMethod() };

        return LinuxTentacleUpgradeStrategy.BuildScript(targetVersion, tarballOnly);
    }

    /// <summary>
    /// Reads the .sh-written <c>last-upgrade.json</c>. Returns null if
    /// the file doesn't exist OR is unparseable.
    /// </summary>
    public UpgradeStatusPayload ReadLastUpgradeStatus()
    {
        if (!File.Exists(StatusFilePath)) return null;
        var raw = File.ReadAllText(StatusFilePath);
        return UpgradeStatusPayload.TryParse(raw);
    }

    /// <summary>
    /// Runs the rendered .sh via <c>bash</c>. Returns (exitCode, combined
    /// stdout+stderr). Bash on Linux + macOS handles set -uo pipefail +
    /// trap chains the same way; the .sh is Linux-only in production
    /// (uses systemd-run / systemctl) but its Phase A pre-scope flow
    /// (download, SHA verify, extract, ldd check) is bash-only and runs
    /// fine on macOS too.
    /// </summary>
    public (int exitCode, string output) RunUpgradeScript(string script)
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"squid-linux-lifecycle-{Guid.NewGuid():N}.sh");
        File.WriteAllText(tempScript, script);
        try
        {
            // chmod +x + bash invocation. We use `bash $script` (not
            // `./$script`) so the file's executable bit isn't load-
            // bearing — defensive against macOS attribute oddities.
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = tempScript,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch bash for .sh execution");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            // Generous timeout. Phase A download + SHA verify + extract
            // is typically <10s; the 5min wall-clock matches the
            // production strategy's UpgradeScriptTimeout — same upper
            // bound the agent enforces.
            if (!proc.WaitForExit(300_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new TimeoutException(
                    "upgrade-linux-tentacle.sh did not complete within 5min. " +
                    $"Inspect /var/log/squid-tentacle-upgrade.log if it exists, or check the rendered .sh at {tempScript} (left behind for diagnosis).");
            }

            return (proc.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* best-effort */ }
        }
    }

    public void MarkClean()
    {
        _clean = true;
    }

    public void Dispose()
    {
        // Diagnostic: surface non-clean exits in CI logs.
        if (!_clean)
            Console.WriteLine($"[LinuxLifecycleContext] Dispose called without MarkClean — test for service '{Fixture.ServiceName}' failed before its happy-path conclusion.");

        try { Fixture.Dispose(); } catch { /* best-effort */ }

        // .bak directory created by Phase B's mv swap (when Phase B runs).
        try
        {
            var bakDir = Fixture.InstallDir + ".bak";
            if (Directory.Exists(bakDir))
                Directory.Delete(bakDir, recursive: true);
        }
        catch { /* best-effort */ }

        // Test-isolated state dir.
        try
        {
            if (Directory.Exists(StateDirOverride))
                Directory.Delete(StateDirOverride, recursive: true);
        }
        catch { /* best-effort */ }

        try { Mirror.Dispose(); } catch { /* best-effort */ }

        // Reset env vars set during RenderProductionScriptForVersion. NOT
        // strictly needed (test process exits) but clean.
        try { Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, null); } catch { }
        try { Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, null); } catch { }
    }

    private static string LocateTestServiceScript()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "Squid.LinuxTentacleE2E.TestService", "squid-linux-test-service.sh");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate squid-linux-test-service.sh. Expected at tests/Squid.LinuxTentacleE2E.TestService/squid-linux-test-service.sh.");
    }
}
