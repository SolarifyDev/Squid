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

    /// <summary>Path to the test service script under tests/ (used as the placeholder Squid.Tentacle binary in install tarballs).</summary>
    public string TestServiceScript { get; }

    public LinuxInstallScriptContext()
    {
        var unique = Guid.NewGuid().ToString("N");

        InstallDir = Path.Combine(Path.GetTempPath(), $"squid-install-test-{unique}");
        Mirror = LocalReleaseMirror.Start();
        InstallScriptPath = LocateInstallScript();
        TestServiceScript = LocateTestServiceScript();
    }

    /// <summary>
    /// Builds a single-entry .tar.gz containing a placeholder
    /// <c>Squid.Tentacle</c> binary (a copy of the test service script).
    /// install-tentacle.sh extracts the tarball to <see cref="InstallDir"/>
    /// then chmod +x's the binary + creates the symlinks. The placeholder
    /// is a bash script that handles <c>version</c> + <c>help</c>
    /// subcommands (per the .sh's post-install verification at line 510)
    /// and falls into a sleep loop otherwise (so a systemd start
    /// post-install would idle as expected).
    ///
    /// <para>Tar contents are FLAT (no wrapper directory) — line 251 of
    /// install-tentacle.sh expects this exact shape: <c>tar xzf "$ARCHIVE"
    /// -C "$INSTALL_DIR"</c> with no <c>--strip-components</c>.</para>
    /// </summary>
    public byte[] BuildInstallTarGz(string version)
    {
        var binaryBytes = File.ReadAllBytes(TestServiceScript);

        // Single entry: Squid.Tentacle (the placeholder binary).
        // No version.txt needed — install-tentacle.sh doesn't read it
        // (only the upgrade flow's marker mechanism does). Keeping the
        // tarball minimal matches what the production release pipeline
        // ships for fresh installs.
        var entries = new (string Name, byte[] Content)[]
        {
            ("Squid.Tentacle", binaryBytes)
        };

        return BuildTarGz(entries);
    }

    /// <summary>
    /// Hand-rolled multi-entry POSIX tar + gzip. Mirrors
    /// <see cref="LinuxLifecycleContext.BuildV2BundleTarGz"/>'s helper
    /// so the install tarball format matches the upgrade tarball format
    /// the production release pipeline produces.
    /// </summary>
    private static byte[] BuildTarGz((string Name, byte[] Content)[] entries)
    {
        using var tarMs = new MemoryStream();
        foreach (var entry in entries)
        {
            var header = BuildTarHeader(entry.Name, entry.Content.Length);
            tarMs.Write(header, 0, header.Length);
            tarMs.Write(entry.Content, 0, entry.Content.Length);
            var pad = (512 - (entry.Content.Length % 512)) % 512;
            if (pad > 0) tarMs.Write(new byte[pad], 0, pad);
        }
        tarMs.Write(new byte[1024], 0, 1024);

        using var gzMs = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(gzMs, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var tarBytes = tarMs.ToArray();
            gz.Write(tarBytes, 0, tarBytes.Length);
        }
        return gzMs.ToArray();
    }

    private static byte[] BuildTarHeader(string fileName, long size)
    {
        var header = new byte[512];

        var nameBytes = System.Text.Encoding.ASCII.GetBytes(fileName);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));

        // Mode 0755 — install-tentacle.sh re-applies chmod +x but
        // shipping it executable from the tarball matches production.
        WriteOctal(header, offset: 100, length: 8, value: 0x1ED);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, size);
        WriteOctal(header, 136, 12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        for (var i = 148; i < 156; i++) header[i] = (byte)' ';
        header[156] = (byte)'0';

        var ustar = System.Text.Encoding.ASCII.GetBytes("ustar");
        Array.Copy(ustar, 0, header, 257, ustar.Length);
        header[263] = (byte)'0';
        header[264] = (byte)'0';

        var checksum = 0;
        foreach (var b in header) checksum += b;
        WriteOctal(header, 148, 7, checksum);
        header[155] = 0;

        return header;
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, long value)
    {
        var s = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
        buffer[offset + length - 1] = 0;
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

        // J.M.L.A.5: tests that enable CREATE_USER=yes leave the
        // squid-tentacle system user behind. userdel'ing on Dispose
        // keeps host state clean across test runs (otherwise subsequent
        // CI runs see the user as pre-existing → useradd idempotent
        // skips, but state pollution accumulates and could mask
        // useradd regressions).
        TrySudoUserDel("squid-tentacle");

        try { Mirror.Dispose(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Sudo-wrapped <c>test -f</c>. Returns true if the file exists.
    /// Use this for paths under restricted system dirs (<c>/etc/sudoers.d/</c>,
    /// <c>/etc/squid-tentacle/</c>) — the test process runs as a non-root
    /// user and cannot <c>stat</c> files under <c>0750 root:root</c>
    /// directories. <see cref="File.Exists"/> would return FALSE even
    /// when the file actually exists, producing false-negative
    /// assertions. Caught by J.M.L.A.5.3 first runner where the .sh
    /// successfully installed the sudoers file but File.Exists couldn't
    /// see it.
    /// </summary>
    public static bool SudoFileExists(string path)
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
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        proc?.WaitForExit(5_000);
        return proc?.ExitCode == 0;
    }

    /// <summary>
    /// Sudo-wrapped <c>test -d</c>. Returns true if the directory exists.
    /// Same permission-boundary rationale as <see cref="SudoFileExists"/>:
    /// directories under <c>/etc/squid-tentacle/instances/&lt;name&gt;/</c>
    /// inherit the parent's restrictive ownership (root or the
    /// <c>squid-tentacle</c> user, mode <c>0750</c>) so a non-root test
    /// process can't <c>Directory.Exists</c> through them.
    /// Used by J.M.L.B.6/B7 to pin <c>service uninstall --purge</c>'s
    /// instance-directory contract.
    /// </summary>
    public static bool SudoDirectoryExists(string path)
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
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        proc?.WaitForExit(5_000);
        return proc?.ExitCode == 0;
    }

    /// <summary>
    /// Sudo-wrapped <c>cat</c>. Returns the file's content as a string,
    /// or empty string on read failure. Same permission-boundary
    /// rationale as <see cref="SudoFileExists"/>.
    /// </summary>
    public static string SudoReadAllText(string path)
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
        psi.ArgumentList.Add("cat");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        if (proc == null) return string.Empty;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5_000);
        return proc.ExitCode == 0 ? stdout : string.Empty;
    }

    /// <summary>
    /// Sudo-wrapped <c>stat -c %a</c>. Returns the file's mode as an
    /// octal string (e.g. <c>"440"</c>, <c>"755"</c>), or empty on
    /// failure. Used for sudoers-file mode pinning where the file lives
    /// under <c>/etc/sudoers.d/</c> (root-only readable).
    /// </summary>
    public static string SudoFileMode(string path)
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
        psi.ArgumentList.Add("stat");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("%a");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        if (proc == null) return string.Empty;
        var stdout = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(5_000);
        return proc.ExitCode == 0 ? stdout : string.Empty;
    }

    private static void TrySudoUserDel(string username)
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
            psi.ArgumentList.Add("userdel");
            psi.ArgumentList.Add(username);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch
        {
            // Best-effort. user may not exist (most tests skip CREATE_USER);
            // userdel returns 6 in that case which is fine.
        }
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
            "Could not locate squid-linux-test-service.sh. Expected at tests/Squid.LinuxTentacleE2E.TestService/.");
    }
}
