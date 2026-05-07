using System.Diagnostics;
using System.Reflection;

namespace Squid.LinuxTentacleE2ETests.Infrastructure;

/// <summary>
/// Phase 12.M.L.A.1+ — fixture for the production
/// <c>deploy/scripts/install-tentacle.sh</c> bootstrap installer. Drives
/// the real script against a real <see cref="LocalReleaseMirror"/> and
/// real bash + curl + (where applicable) sudo, with cleanup of every
/// host artefact the script could stage.
///
/// <para>Pollution surface is minimised by the recommended overrides:
/// <c>INSTALL_DIR</c>=test-private temp dir, <c>NO_PKG_INSTALL=1</c>
/// (skips the "optimistic APT/RPM" probe-then-install path), and
/// <c>CREATE_USER=no</c> (skips <c>useradd squid-tentacle</c>). Tests
/// that DO need the full pollution matrix (happy-path with systemd
/// unit + sudoers) inherit <see cref="Dispose"/>'s best-effort
/// cleanup of every well-known production path the script writes to.</para>
///
/// <para><b>Skip-on-non-Linux</b>: composes
/// <see cref="LinuxServiceFixture.IsAvailable"/> (Linux + passwordless
/// sudo, since the .sh's runtime-deps install + write to /etc/* needs
/// sudo even with INSTALL_DIR override). Tests using this context call
/// <see cref="IsAvailable"/> first and no-op-skip on non-Linux dev hosts.</para>
/// </summary>
public sealed class LinuxInstallScriptContext : IDisposable
{
    private bool _clean;

    public static bool IsAvailable => LinuxServiceFixture.IsAvailable;

    public LocalReleaseMirror Mirror { get; }

    /// <summary>Path to the production install-tentacle.sh under deploy/scripts.</summary>
    public string InstallScriptPath { get; }

    /// <summary>Per-test isolated install dir (under /tmp). The .sh's `mkdir -p` only fires after successful download, so failed runs leave this absent.</summary>
    public string InstallDir { get; }

    public LinuxInstallScriptContext()
    {
        var unique = Guid.NewGuid().ToString("N");

        InstallDir = Path.Combine(Path.GetTempPath(), $"squid-install-test-{unique}");
        Mirror = LocalReleaseMirror.Start();
        InstallScriptPath = LocateInstallScript();
    }

    /// <summary>
    /// Runs <c>install-tentacle.sh</c> via <c>sudo bash</c> with the
    /// given version + the recommended-minimum-pollution env overrides.
    /// Returns (exitCode, combined stdout+stderr).
    ///
    /// <para>Why <c>sudo bash</c>: the .sh's <c>install_runtime_deps</c>
    /// invokes <c>apt-get install libicu</c> and the post-extract phase
    /// writes to <c>/etc/squid-tentacle</c>, <c>/var/lib/squid-tentacle</c>,
    /// <c>/usr/local/bin</c> — all of which require root. Even with a
    /// per-test INSTALL_DIR override, root is still needed for those.
    /// On the GHA ubuntu-latest runner sudo is passwordless; the
    /// <see cref="IsAvailable"/> guard skips on hosts without it.</para>
    ///
    /// <para>Recommended overrides applied:
    /// <list type="bullet">
    ///   <item><c>INSTALL_DIR</c> → test-private <see cref="InstallDir"/></item>
    ///   <item><c>DOWNLOAD_BASE</c> → <see cref="Mirror"/>'s base URL (so 404s + tarball serves are deterministic + offline)</item>
    ///   <item><c>NO_PKG_INSTALL=1</c> (skip APT/RPM repo probe path; goes direct to tarball)</item>
    ///   <item><c>CREATE_USER=no</c> (skip useradd)</item>
    ///   <item><c>SQUID_BASE_URL=http://localhost:1</c> defensive: even if NO_PKG_INSTALL is forgotten, bind to a non-resolvable host so the probe fails fast instead of hitting prod squid.solarifyai.com)</item>
    /// </list></para>
    /// </summary>
    public (int exitCode, string output) RunInstallScript(string version, Dictionary<string, string> extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("--preserve-env=INSTALL_DIR,DOWNLOAD_BASE,NO_PKG_INSTALL,CREATE_USER,SQUID_BASE_URL,TENTACLE_VERSION");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add(InstallScriptPath);
        psi.ArgumentList.Add("--version");
        psi.ArgumentList.Add(version);
        psi.ArgumentList.Add("--install-dir");
        psi.ArgumentList.Add(InstallDir);

        psi.Environment["INSTALL_DIR"] = InstallDir;
        psi.Environment["DOWNLOAD_BASE"] = Mirror.BaseUri.ToString().TrimEnd('/');
        psi.Environment["NO_PKG_INSTALL"] = "1";
        psi.Environment["CREATE_USER"] = "no";
        // Defensive: even if NO_PKG_INSTALL is dropped on a future polish,
        // the APT probe must fail-fast against a non-resolvable host
        // instead of hitting the real squid.solarifyai.com mirror.
        psi.Environment["SQUID_BASE_URL"] = "http://localhost:1";

        if (extraEnv != null)
        {
            foreach (var kvp in extraEnv)
                psi.Environment[kvp.Key] = kvp.Value;
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch sudo bash for install-tentacle.sh execution");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // Generous timeout. Real installer takes ~30s in production
        // (apt-get update + tarball download + extract + service setup);
        // failures should surface much faster.
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException(
                "install-tentacle.sh did not complete within 3min. " +
                $"Stuck — check apt-get / curl progress. Mirror base: {Mirror.BaseUri}");
        }

        return (proc.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
    }

    public void MarkClean()
    {
        _clean = true;
    }

    public void Dispose()
    {
        // Diagnostic: surface non-clean exits in CI logs.
        if (!_clean)
            Console.WriteLine($"[LinuxInstallScriptContext] Dispose called without MarkClean — install test for {InstallDir} failed before its happy-path conclusion.");

        // Best-effort cleanup of every well-known production path the
        // .sh writes to. Each step is `|| true`-style — missing paths
        // (typical for failed-install tests where most paths were never
        // created) are a no-op. Order matters: stop+disable systemd unit
        // BEFORE removing its file; otherwise systemctl reports the unit
        // as still-loaded and a subsequent test's daemon-reload may not
        // pick up the new unit cleanly.
        TrySudoRm(InstallDir);

        // System paths the .sh creates on happy-path. None are touched
        // by failure-path tests, but the rm is idempotent so we always
        // run it (defensive against partial-state on assertion-failure
        // mid-test).
        TrySudoRm("/etc/squid-tentacle");
        TrySudoRm("/var/lib/squid-tentacle");
        TrySudoRm("/usr/local/bin/squid-tentacle");
        TrySudoRm("/etc/apt/sources.list.d/squid.list");
        TrySudoRm("/etc/apt/keyrings/squid.gpg");
        TrySudoRm("/etc/apt/apt.conf.d/99-squid-direct.conf");
        TrySudoRm("/etc/sudoers.d/squid-tentacle-upgrade");
        TrySudoRm("/etc/systemd/system/squid-tentacle.service");

        try { Mirror.Dispose(); } catch { /* best-effort */ }
    }

    private static void TrySudoRm(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("rm");
            psi.ArgumentList.Add("-rf");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch
        {
            // Best-effort. Cleanup leak on failure is preferable to
            // failing the test in the dispose path.
        }
    }

    private static string LocateInstallScript()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "deploy", "scripts", "install-tentacle.sh");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate deploy/scripts/install-tentacle.sh. Expected at the repo root's deploy/scripts/.");
    }
}
