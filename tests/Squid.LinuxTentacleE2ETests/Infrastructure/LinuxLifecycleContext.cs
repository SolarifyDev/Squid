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

    /// <summary>
    /// Test-private mirror of production's per-OS config-dir (Linux:
    /// <c>/etc/squid-tentacle</c>). Used by E15.h-Linux to assert that
    /// the .sh's swap of INSTALL_DIR does NOT touch sibling config trees.
    ///
    /// <para>The .sh has no config-dir env override — it only writes
    /// under STATE_DIR (= upgrade artefacts) and INSTALL_DIR (= binary
    /// staging). Anything OUTSIDE those two MUST survive byte-for-byte.
    /// We stage instance state here so the test can prove that contract
    /// without polluting the host's real <c>/etc/squid-tentacle</c>.</para>
    /// </summary>
    public string ConfigDirOverride { get; }

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

        ConfigDirOverride = Path.Combine(Path.GetTempPath(), $"squid-linux-lifecycle-config-{unique}");
        // E15.h-Linux pre-stages instance state under here. NOT created by
        // default — callers opt-in via StageInstanceState. Keeps the dir
        // off-disk for non-E15 tests so Dispose has nothing to clean.
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
        // Load production .sh template directly + substitute all placeholders
        // manually. Mirrors what Windows
        // UpgradeLifecycleContext.RenderProductionScriptForVersion does.
        //
        // Why NOT call LinuxTentacleUpgradeStrategy.BuildScript: the strategy
        // hardcodes INSTALL_DIR / SERVICE_NAME / SERVICE_USER from its
        // Default* fields (production paths). For E2E tests we need these
        // to be the fixture's GUID-suffixed paths (so the systemctl restart
        // hits OUR test service, not the production "squid-tentacle"
        // unit). Loading the template directly + manually substituting is
        // the cleanest way — drift detector at LinuxScript_PlaceholderSet_*
        // pins the substitution map.
        //
        // J.L.E.7 first Linux runner caught this: BuildScript's default
        // SERVICE_NAME=squid-tentacle made systemctl restart fail with
        // "Unit squid-tentacle.service not found" — manual substitution
        // with Fixture.ServiceName fixes it.
        var template = File.ReadAllText(LocateLinuxTemplate());

        // DOWNLOAD_URL: mirror serves any tar.gz path; pattern matches
        // production strategy's BuildDownloadUrl shape with $RID rewrite.
        var downloadUrl = $"{Mirror.BaseUri.ToString().TrimEnd('/')}/{targetVersion}/squid-tentacle-{targetVersion}-$RID.tar.gz";

        // INSTALL_METHODS: tarball-only render (skips apt+yum probes).
        var installMethodsBlock = new TarballUpgradeMethod().RenderDetectAndInstall(targetVersion);

        // SERVICE_USER: current user. The .sh's chown line targets this
        // user; on the GHA runner it's typically "runner". Defaulting to
        // Environment.UserName makes ownership transfers no-ops (current
        // user owns INSTALL_DIR already).
        var serviceUser = Environment.UserName;

        return template
            .Replace("{{TARGET_VERSION}}", targetVersion, StringComparison.Ordinal)
            .Replace("{{DOWNLOAD_URL}}", downloadUrl, StringComparison.Ordinal)
            .Replace("{{EXPECTED_SHA256}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{INSTALL_DIR}}", Fixture.InstallDir, StringComparison.Ordinal)
            .Replace("{{SERVICE_NAME}}", Fixture.ServiceName, StringComparison.Ordinal)
            .Replace("{{SERVICE_USER}}", serviceUser, StringComparison.Ordinal)
            // HEALTHCHECK_URL default 127.0.0.1:8080/healthz works because
            // the test service's python3 responder binds there. Tests
            // wanting a different port set HEALTHCHECK_URL env first.
            .Replace("{{HEALTHCHECK_URL}}", "http://127.0.0.1:8080/healthz", StringComparison.Ordinal)
            .Replace("{{STATE_DIR}}", StateDirOverride, StringComparison.Ordinal)
            // HEALTHCHECK_RETRIES=10: ~10-15s window. python3 healthz
            // responder takes ~500ms-2s to spawn after systemd starts the
            // service; retries=1 gives <1s and fails. retries=10 is the
            // safe-margin balance: well under production's 90s default,
            // generous enough for any reasonable python3 startup variance.
            // J.L.E.7 first Linux runner pass with retries=1 timed out.
            .Replace("{{HEALTHCHECK_RETRIES}}", "10", StringComparison.Ordinal)
            .Replace("{{INSTALL_METHODS}}", installMethodsBlock, StringComparison.Ordinal);
    }

    private static string LocateLinuxTemplate()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Squid.Core", "Resources", "Upgrade", "upgrade-linux-tentacle.sh");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate upgrade-linux-tentacle.sh from the test assembly's directory tree");
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
    public byte[] BuildV2BundleTarGz(string targetVersion, bool failHealthz = false)
    {
        var serviceScriptBytes = File.ReadAllBytes(TestServiceScript);

        // J.L.E.9: optional surgical mutation that flips the embedded
        // python3 healthz responder's success code (200) to a 5xx (503),
        // so .sh's Phase B `curl -fsS` against /healthz fails (-f rejects
        // 5xx as exit 22). HEALTH_OK stays 0 → retry loop exhausts →
        // rollback path fires. Same script binds the same port, returns
        // a real HTTP response — just an unhealthy one.
        //
        // Why not a separate v2 script: the .sh's Phase B mv-swap would
        // work either way, but a separate file means the test infra has
        // to maintain two service scripts. A surgical 1-call swap on a
        // SINGLE script keeps the contract clean: same script, optional
        // failure mode toggled per build. Mirrors Windows' `crashOnStart`
        // parameter on `BuildV2BundleZip`.
        if (failHealthz)
        {
            var script = Encoding.UTF8.GetString(serviceScriptBytes);
            const string healthyResponse = "self.send_response(200)";
            const string unhealthyResponse = "self.send_response(503)";

            if (!script.Contains(healthyResponse))
                throw new InvalidOperationException(
                    $"Cannot inject failHealthz mutation: test service script does not contain expected sentinel '{healthyResponse}'. " +
                    "If the script's healthz responder was rewritten, update this mutation site to match the new sentinel.");

            script = script.Replace(healthyResponse, unhealthyResponse, StringComparison.Ordinal);
            serviceScriptBytes = Encoding.UTF8.GetBytes(script);
        }

        // version.txt content is the FULL target version (NOT stripped)
        // because the .sh's Phase B post-restart sanity check compares
        // EXACT match between the running binary's `version` subcommand
        // output AND TARGET_VERSION:
        //   if [ "$RUNNING_VERSION" != "$TARGET_VERSION" ]; then rollback; fi
        // J.L.E.7.5 runner caught this — pre-release "2.0.0-test" in
        // TARGET_VERSION mismatched stripped "2.0.0" in version.txt →
        // Phase B treated mismatch as failure → rolled back. Real
        // Squid.Tentacle's version output IS the full InformationalVersion
        // (includes pre-release suffix); production parity needs same.
        var versionTxtBytes = Encoding.UTF8.GetBytes(targetVersion);

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
    /// Pre-stages instance registry + per-instance config + cert bytes
    /// under <see cref="ConfigDirOverride"/>, mirroring the layout
    /// production's <c>InstanceRegistry</c> + <c>register</c> CLI write.
    ///
    /// <para>Used by E15.h-Linux to assert the .sh's Phase B mv-swap
    /// of INSTALL_DIR does NOT touch sibling config trees. Returns the
    /// three concrete paths (registry / config / cert) so the caller can
    /// hash them pre + post upgrade and pin byte-for-byte preservation.</para>
    ///
    /// <para>Why instance name is GUID-suffixed: tests run concurrently
    /// against the same systemd namespace, so even though
    /// <see cref="ConfigDirOverride"/> is per-test, baking a unique
    /// instance name in too keeps logs readable when multiple test
    /// instances overlap in the runner output.</para>
    /// </summary>
    public InstanceConfigPaths StageInstanceState(string instanceName)
    {
        Directory.CreateDirectory(ConfigDirOverride);

        var registryPath = Path.Combine(ConfigDirOverride, "instances.json");
        var configPath = Path.Combine(ConfigDirOverride, "instances", $"{instanceName}.config.json");
        var certsDir = Path.Combine(ConfigDirOverride, "instances", instanceName, "certs");
        var certPath = Path.Combine(certsDir, $"{instanceName}.pem");

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(certsDir);

        var registryJson = $@"{{ ""instances"": [{{ ""name"": ""{instanceName}"", ""configPath"": ""{configPath}"", ""createdAt"": ""2026-05-01T00:00:00+00:00"" }}] }}";
        var configJson = $@"{{ ""serverUrl"": ""https://test-server.example.com"", ""serverThumbprint"": ""ABCDEF1234567890ABCDEF1234567890ABCDEF12"", ""subscriptionId"": ""{Guid.NewGuid():N}"", ""agentName"": ""{instanceName}"" }}";

        // 2KB of pseudo-random bytes representing a real cert/key blob.
        // Content doesn't need to be a real PEM — we're testing FILE
        // preservation, not cert validation. SHA256 comparison drives
        // the assertion. Seed pinned for deterministic cross-run hashes
        // (same seed → same byte sequence → easier to debug if a digest
        // mysteriously diverges).
        var certBytes = new byte[2048];
        new Random(42).NextBytes(certBytes);

        File.WriteAllText(registryPath, registryJson);
        File.WriteAllText(configPath, configJson);
        File.WriteAllBytes(certPath, certBytes);

        return new InstanceConfigPaths(registryPath, configPath, certPath);
    }

    /// <summary>
    /// Spawns a background process that holds an EXCLUSIVE kernel flock
    /// on <see cref="LockFilePath"/> for <paramref name="holdSeconds"/>
    /// seconds (default: 30). Used by E11.u1-Linux to simulate a
    /// concurrent in-flight upgrade — the next .sh dispatch must hit the
    /// `flock -n` failure branch and exit 0 as a no-op.
    ///
    /// <para>The Linux .sh uses kernel flock (BSD-style), NOT file-content
    /// detection. Pre-staging a "stale" lockfile alone does NOT block a
    /// second dispatch — the kernel auto-releases flocks when the holding
    /// process exits, so an unattended file is just data. This helper
    /// genuinely holds the lock at the kernel level for the test window.</para>
    ///
    /// <para>Returns a disposable wrapper. Caller MUST `using` it (or
    /// Dispose explicitly) to release the lock + reap the child process
    /// even if the test asserts mid-flight. The kernel ALSO auto-releases
    /// the flock on process death, so even Process.Kill() during a panic
    /// is safe.</para>
    ///
    /// <para>Implementation detail: invokes the <c>flock</c> CLI from
    /// util-linux (universally available on every modern Linux distro
    /// + GHA ubuntu-latest). Skipped on macOS via the test's
    /// <see cref="IsAvailable"/> guard.</para>
    /// </summary>
    public FlockHolder StartFlockHolder(int holdSeconds = 30)
    {
        // Pre-create the lockfile so flock has something to grab. The .sh
        // also auto-creates it via touch (line 285) but creating it here
        // gives us deterministic existence timing.
        Directory.CreateDirectory(StateDirOverride);
        if (!File.Exists(LockFilePath))
            File.WriteAllText(LockFilePath, string.Empty);

        // `flock -n -x <file> sleep N` — acquires an exclusive lock,
        // runs `sleep N` while holding it, releases on sleep exit.
        // `-n` makes the acquire non-blocking (fail-fast if already held,
        // which we don't expect since this IS the holder).
        var psi = new ProcessStartInfo
        {
            FileName = "flock",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add(LockFilePath);
        psi.ArgumentList.Add("sleep");
        psi.ArgumentList.Add(holdSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to spawn flock holder — `flock` CLI missing? util-linux must be installed.");

        // Brief race window: flock acquires + sleeps, but our caller's
        // upgrade .sh must run AFTER the lock is held. flock acquires the
        // lock before exec'ing sleep, so a 100ms wait is conservative —
        // gives the kernel time to register the lock without making tests
        // sluggish. If flock dies before sleep starts, the lock is gone
        // and Process.HasExited surfaces it on the next assertion.
        Thread.Sleep(100);

        if (proc.HasExited)
            throw new InvalidOperationException(
                $"flock holder exited prematurely with code {proc.ExitCode}. " +
                $"Stderr: {proc.StandardError.ReadToEnd()}. " +
                "The kernel flock acquire failed — likely another holder already exists, " +
                "or sudo permissions on the lockfile path are wrong.");

        return new FlockHolder(proc);
    }

    /// <summary>
    /// Spawns a background watchdog Task that polls for the .bak directory
    /// (created by Phase B's `sudo mv $INSTALL_DIR $BAK_DIR`) and, on
    /// detection, corrupts its embedded service script so any subsequent
    /// rollback attempt's `systemctl start` fires the broken script and
    /// exits 1 immediately.
    ///
    /// <para>Used by E1.uRollbackCritical-Linux to drive the worst-case
    /// path: Phase B fails (failHealthz=true v2 triggers the rollback
    /// chain) AND the rollback's restore-from-.bak ALSO fails (because
    /// .bak's v1 is now corrupted) → ROLLBACK_OK=0 → exit 9 +
    /// ROLLBACK_CRITICAL_FAILED status.</para>
    ///
    /// <para>Why a watchdog (not pre-corruption): the .bak directory is
    /// created at runtime by Phase B's mv. Pre-corrupting INSTALL_DIR's
    /// script would crash the running v1 service before the test even
    /// gets to v1-marker validation. Watchdog waits for .bak to exist
    /// (= Phase B's mv just completed) THEN corrupts.</para>
    ///
    /// <para>Why ownership works: <c>sudo mv</c> preserves file ownership.
    /// The test process owns INSTALL_DIR (under /tmp). After mv, .bak
    /// is still owned by the test process — we can write to it without
    /// sudo. Setting mode 0755 keeps the script executable so systemd
    /// CAN run it (and observe the immediate exit-1).</para>
    ///
    /// <para>Returns a Task that completes when corruption is applied OR
    /// the deadline expires. Caller awaits it after the .sh returns to
    /// confirm the watchdog actually fired (vs the .sh somehow finishing
    /// before .bak was even created — which would invalidate the test).</para>
    /// </summary>
    public Task<bool> StartBakCorruptionWatchdog(TimeSpan deadline)
    {
        var bakDir = Fixture.InstallDir + ".bak";
        var bakScriptPath = Path.Combine(bakDir, "squid-linux-test-service.sh");

        return Task.Run(() =>
        {
            var stopAt = DateTime.UtcNow + deadline;
            while (DateTime.UtcNow < stopAt)
            {
                try
                {
                    if (Directory.Exists(bakDir) && File.Exists(bakScriptPath))
                    {
                        // Replace the v1 script with a deliberately broken
                        // one. systemctl start will exec bash on this →
                        // bash exits 1 → systemd marks unit failed → rollback
                        // wait loop never sees is-active, ROLLBACK_OK=0.
                        File.WriteAllText(bakScriptPath, "#!/bin/bash\nexit 1\n");
                        File.SetUnixFileMode(bakScriptPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        return true;
                    }
                }
                catch
                {
                    // .bak might be in flux during Phase B's mv — retry.
                }
                Thread.Sleep(50);
            }
            return false;
        });
    }

    /// <summary>
    /// SHA256 over the file's bytes, lowercase hex. Stable across runs
    /// (same content → same digest). Used by E15.h-Linux to pin
    /// byte-for-byte preservation of pre-staged instance state across
    /// the upgrade swap.
    /// </summary>
    public static string HashFile(string path)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
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

        // Test-isolated config dir (only present if E15.h-Linux staged
        // instance state — otherwise this branch is a no-op).
        try
        {
            if (Directory.Exists(ConfigDirOverride))
                Directory.Delete(ConfigDirOverride, recursive: true);
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

/// <summary>
/// Triple of paths returned by <see cref="LinuxLifecycleContext.StageInstanceState"/>.
/// Mirrors production's <c>InstanceRegistry</c> on-disk layout
/// (instances.json + per-instance config + per-instance cert dir) so
/// E15.h-Linux can pin byte-for-byte preservation across an upgrade.
/// </summary>
public sealed record InstanceConfigPaths(string Registry, string Config, string Cert);

/// <summary>
/// Disposable wrapper around the background <c>flock</c> child process
/// that holds an exclusive kernel flock on the upgrade lockfile during
/// E11.u1-Linux. Disposing kills + reaps the child; the kernel
/// auto-releases the flock on process death.
/// </summary>
public sealed class FlockHolder : IDisposable
{
    private readonly Process _proc;
    private bool _disposed;

    internal FlockHolder(Process proc)
    {
        _proc = proc;
    }

    /// <summary>True if the kernel flock holder is still alive.</summary>
    public bool IsAlive => !_disposed && !_proc.HasExited;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch
        {
            // Best-effort. Even on panic the kernel auto-releases the
            // flock on process death — leaking a Process handle is the
            // worst-case impact.
        }

        try { _proc.Dispose(); } catch { /* best-effort */ }
    }
}
