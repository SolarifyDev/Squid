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
        // Set env vars for resolver-driven placeholders. Strategy reads:
        //   - DownloadBaseUrl → mirror's HTTP listener
        //   - StateDir       → test-isolated per-test dir (12.L.E.2)
        //   - HealthcheckRetries → 1 attempt for fast tests (12.L.E.6)
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar, Mirror.BaseUri.ToString().TrimEnd('/'));
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.StateDirEnvVar, StateDirOverride);

        // retries=1 cuts the .sh's 90s healthz wait down to 1s. With the
        // test service's python3 healthz responder (SQUID_TEST_SERVICE_HEALTHZ=1
        // set in systemd unit Environment), the responder is up by the
        // time .sh's curl probe runs → HEALTH_OK=1 → success path.
        Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, "1");

        // Tarball-only method order. Skips apt+yum probes which would
        // otherwise slow tests + introduce host-config sensitivity (apt's
        // dpkg-query / yum's rpm -q on systems where these tools may or
        // may not have squid-tentacle installed).
        var tarballOnly = new ILinuxUpgradeMethod[] { new TarballUpgradeMethod() };

        return LinuxTentacleUpgradeStrategy.BuildScript(targetVersion, tarballOnly);
    }

    /// <summary>
    /// Builds a multi-entry tar.gz mirroring the production GitHub
    /// release tarball's shape — what the .sh's Phase A extracts +
    /// Phase B swaps into INSTALL_DIR. Used by full-lifecycle tests
    /// (J.L.E.7+) where the .sh's existence check
    /// (<c>NEW_BIN="$EXTRACT/Squid.Tentacle"</c>) + the symlink chain +
    /// the post-restart healthz poll all need a properly-shaped
    /// payload.
    ///
    /// <para>Contents:</para>
    /// <list type="bullet">
    ///   <item><c>Squid.Tentacle</c> — placeholder shim (a copy of the
    ///         test service script, chmod +x'd by .ps1 line 591).
    ///         The .sh creates symlinks <c>squid-tentacle</c> → this,
    ///         and global <c>/usr/local/bin/squid-tentacle</c>. Real
    ///         systemd unit's ExecStart points at the bash script
    ///         below — Squid.Tentacle is just for the .sh's
    ///         existence-check + symlink chain.</item>
    ///   <item><c>squid-linux-test-service.sh</c> — the actual systemd
    ///         ExecStart target. After swap, INSTALL_DIR has this file
    ///         and the systemd unit's restart picks it up.</item>
    ///   <item><c>version.txt</c> — the test service reads on Start;
    ///         the marker file's content is what tests assert against
    ///         to prove the swap actually landed.</item>
    /// </list>
    /// </summary>
    public byte[] BuildV2BundleTarGz(string targetVersion)
    {
        var serviceScriptBytes = File.ReadAllBytes(TestServiceScript);

        // version.txt content is what test service reads on Start →
        // writes to the marker. Strip pre-release suffix so marker
        // assertions compare cleanly (e.g. "2.0.0-test" → "2.0.0").
        var serviceVersion = targetVersion.Split('-')[0];
        var versionTxtBytes = Encoding.UTF8.GetBytes(serviceVersion);

        // Squid.Tentacle placeholder: just a copy of the test service
        // script (an executable bash file). chmod +x at .sh line 591
        // works on any bash file. Symlink chain works. The systemd
        // unit's ExecStart points elsewhere (the .sh script below) so
        // this placeholder isn't actually invoked as a service.
        var squidTentacleBytes = serviceScriptBytes;

        var entries = new (string Name, byte[] Content)[]
        {
            ("Squid.Tentacle", squidTentacleBytes),
            ("squid-linux-test-service.sh", serviceScriptBytes),
            ("version.txt", versionTxtBytes)
        };

        return BuildTarGz(entries);
    }

    /// <summary>
    /// Hand-rolled multi-entry POSIX tar + gzip. <see cref="LocalReleaseMirror"/>
    /// has a single-file BuildTarGz; this helper handles multiple files
    /// (the J.L.E.7+ full-bundle case).
    /// </summary>
    private static byte[] BuildTarGz((string Name, byte[] Content)[] entries)
    {
        // Build uncompressed tar first (each entry's 512-byte header +
        // content padded to 512 + 1024-byte trailer).
        using var tarMs = new MemoryStream();
        foreach (var entry in entries)
        {
            var header = BuildTarHeader(entry.Name, entry.Content.Length);
            tarMs.Write(header, 0, header.Length);
            tarMs.Write(entry.Content, 0, entry.Content.Length);
            // Pad to 512-byte block
            var pad = (512 - (entry.Content.Length % 512)) % 512;
            if (pad > 0) tarMs.Write(new byte[pad], 0, pad);
        }
        // 1024-byte zero terminator
        tarMs.Write(new byte[1024], 0, 1024);

        // gzip-wrap
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

        // Name (100 bytes, NUL-terminated)
        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));

        // Mode 0755 — chmod-x ready (for Squid.Tentacle + the script)
        WriteOctal(header, offset: 100, length: 8, value: 0x1ED);
        WriteOctal(header, 108, 8, 0);   // uid
        WriteOctal(header, 116, 8, 0);   // gid
        WriteOctal(header, 124, 12, size);
        WriteOctal(header, 136, 12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Checksum field initially space-filled (for the checksum sum
        // calculation below); type flag '0' = regular file.
        for (var i = 148; i < 156; i++) header[i] = (byte)' ';
        header[156] = (byte)'0';

        // Magic "ustar" + version "00"
        var ustar = Encoding.ASCII.GetBytes("ustar");
        Array.Copy(ustar, 0, header, 257, ustar.Length);
        header[263] = (byte)'0';
        header[264] = (byte)'0';

        // Checksum
        var checksum = 0;
        foreach (var b in header) checksum += b;
        WriteOctal(header, 148, 7, checksum);
        header[155] = 0;

        return header;
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, long value)
    {
        var s = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
        buffer[offset + length - 1] = 0;
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
        try { Environment.SetEnvironmentVariable(LinuxTentacleUpgradeStrategy.HealthcheckRetriesEnvVar, null); } catch { }
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
