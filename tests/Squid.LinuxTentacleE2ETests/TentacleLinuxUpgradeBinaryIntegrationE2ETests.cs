using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.U — E2E coverage for the binary-swap layer of the
/// Linux upgrade flow. Closes the largest remaining P0 gap identified
/// in the global audit: the "real binary v1→v2 cycles correctly with
/// no state loss" contract.
///
/// <para><b>Coverage delta vs <see cref="TentacleLinuxUpgradeLifecycleE2ETests"/></b>:
/// that suite tests the bash <c>upgrade-linux-tentacle.sh</c> script
/// end-to-end against a placeholder bash binary — proves the script's
/// download / SHA / Phase A / Phase B / rollback / events.jsonl /
/// last-upgrade.json paths. THIS suite tests the same OS-level
/// guarantee the script depends on but with REAL Squid.Tentacle
/// binaries: stop the service running v1, swap the binary file's
/// content to v2's bytes (simulating what
/// <c>upgrade-linux-tentacle.sh</c>'s Phase B does at the filesystem
/// layer), restart the service, verify v2 is served + cert identity
/// preserved + config preserved.</para>
///
/// <para><b>Why this matters cross-tier</b>: the bash-script tier
/// proves the script's logic. This binary-integration tier proves the
/// END-RESULT operators care about — after the script runs, the agent
/// IS running the new version AND retains its identity. Without this
/// pin, a regression in:
/// <list type="bullet">
///   <item>systemd unit's <c>Type=simple</c> + <c>Restart=on-failure</c>
///         coordinating with the binary's startup</item>
///   <item>Per-instance config persistence path (cert dir + config.json)
///         surviving a binary-only swap</item>
///   <item><c>show-thumbprint</c> reading the same cert from disk
///         post-upgrade as it did pre-upgrade</item>
/// </list>
/// would silently break upgrades — the script reports "swapped + restarted"
/// successfully but the agent's identity is gone or it doesn't actually
/// run the new code. Operators see the UI report "upgraded" but the
/// poll handshake breaks hours later.</para>
///
/// <para><b>Tier 🟢 high-fidelity</b> (Rule 12.4): real production
/// binaries (built via <c>dotnet publish</c> with two distinct
/// <c>-p:Version</c> stamps) + real systemd + real <c>/etc/squid-
/// tentacle/</c> filesystem state. Only "mocked" component is the
/// stub register server — same as Section B/C.</para>
///
/// <para><b>Why simulate the swap directly instead of running
/// upgrade-linux-tentacle.sh</b>: the script is already covered by the
/// lifecycle suite. The DELTA this suite adds is "what happens
/// AFTER the swap mechanics" — that contract is provable with a direct
/// stop / file-overwrite / start sequence. Driving the full script
/// would: (a) duplicate lifecycle-suite coverage, (b) require staging
/// a real download mirror with computed SHAs, (c) add 30+s to runtime
/// without complementary coverage. Direct swap is the surgical pin.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxUpgradeBinaryIntegrationE2ETests
{
    // ========================================================================
    // U1.h-Linux — Binary swap with restart preserves cert identity AND
    //               serves new version
    //
    // The single most important upgrade-confidence pin operators need:
    // after upgrade, the agent IS running new code AND retains its
    // identity (server's trust list still matches the agent's cert).
    //
    // Test mechanism:
    //   1. Stage v1 binary at a per-test path (copy from fixture)
    //      → so the binary swap doesn't corrupt the shared fixture
    //   2. Register against StubSquidServer using v1
    //   3. service install --service-name <unique> using v1
    //      → unit file's ExecStart points to per-test v1 path
    //   4. Wait for is-active = active
    //   5. Capture pre-upgrade state:
    //      - version output (must be v1's stamp, 99.99.99)
    //      - thumbprint from show-thumbprint
    //   6. Stop service
    //   7. SWAP: copy v2 binary content over per-test path's v1 file
    //      → simulates Phase B's mv $tarball/Squid.Tentacle <bin path>
    //   8. Start service
    //   9. Wait for is-active = active again
    //   10. Capture post-upgrade state + assert:
    //       - version output is now v2 (100.0.0)
    //       - thumbprint UNCHANGED (cert preserved through swap)
    //       - service still active (binary works)
    //
    // Cleanup: service uninstall --purge (also removes per-test files).
    //
    // Tier: 🟢 H. Two real binaries + real systemd + real filesystem.
    // Expected runtime: ~60-90s (cold v2 build first time + ~25s for
    // register + install + wait active + swap + restart + assertions).
    // ========================================================================

    [Fact]
    public void U1h_BinarySwapWithRestart_PreservesIdentityAndServesNewVersion()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeBinaryIntegrationTestContext();

        // ── Phase 1: register against stub using per-test v1 binary ───────
        var (regExit, regOutput) = SudoRunBinary(ctx.PerTestBinaryPath,
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-U1-test-key",
            "--role", "u1-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.ListeningPort.ToString(CultureInfo.InvariantCulture));
        regExit.ShouldBe(0,
            customMessage: $"U1h precondition: register MUST succeed using per-test v1 binary.\noutput:\n{regOutput}");

        // ── Phase 2: service install (unit file references per-test path) ──
        // ServiceCommand uses Environment.ProcessPath for ExecStart, so
        // installing via the per-test binary writes a unit pointing at
        // per-test path — which we'll later swap.
        var (installExit, installOutput) = SudoRunBinary(ctx.PerTestBinaryPath,
            "service", "install", "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0,
            customMessage: $"U1h precondition: service install MUST succeed.\noutput:\n{installOutput}");

        // ── Phase 3: wait for service active (v1 running) ─────────────────
        WaitForActive(ctx.ServiceName, TimeSpan.FromSeconds(15)).ShouldBeTrue(
            customMessage: $"U1h precondition: service '{ctx.ServiceName}' MUST reach active running v1. " +
                          $"\nstatus dump:\n{RunSystemctl("status", ctx.ServiceName).output}\n" +
                          $"journal tail:\n{RunJournalctl(ctx.ServiceName)}");

        // ── Phase 4: capture pre-upgrade state ────────────────────────────
        var preVersionOutput = RunBinary(ctx.PerTestBinaryPath, "version").output;
        var preVersion = preVersionOutput.Trim();
        preVersion.ShouldBe(LinuxTentacleBinaryFixture.BuildVersion,
            customMessage: $"U1h precondition: pre-upgrade version MUST be v1 stamp '{LinuxTentacleBinaryFixture.BuildVersion}'. " +
                          $"Got: '{preVersion}'.");

        var preThumbprintOutput = RunBinary(ctx.PerTestBinaryPath, "show-thumbprint").output;
        var preThumbprint = ExtractThumbprint(preThumbprintOutput);
        preThumbprint.ShouldNotBeNullOrEmpty(
            customMessage: $"U1h precondition: pre-upgrade thumbprint MUST be readable.\noutput:\n{preThumbprintOutput}");

        // ── Phase 5: stop service ─────────────────────────────────────────
        var (stopExit, _) = SudoRunBinary(ctx.PerTestBinaryPath,
            "service", "stop", "--service-name", ctx.ServiceName);
        stopExit.ShouldBe(0, "U1h: service stop MUST succeed before swap");

        WaitForInactive(ctx.ServiceName, TimeSpan.FromSeconds(10)).ShouldBeTrue(
            customMessage: $"U1h: service '{ctx.ServiceName}' MUST reach inactive before binary swap. " +
                          "Without inactive, the swap could overwrite a file with an open file handle.");

        // ── Phase 6: SWAP — overwrite per-test path with v2 content ───────
        // This simulates upgrade-linux-tentacle.sh's Phase B step:
        //   mv $extracted_dir/Squid.Tentacle $bin_path
        // We do it via File.Copy + chmod since the fixture's v2 binary
        // is at a separate cache path and we need to overwrite per-test
        // path's bytes WHILE preserving its existing executable mode.
        TrySudoCopyExecutable(ctx.Binary.EnsureBuiltV2(), ctx.PerTestBinaryPath);

        // ── Phase 7: start service (now running v2) ───────────────────────
        var (startExit, _) = SudoRunBinary(ctx.PerTestBinaryPath,
            "service", "start", "--service-name", ctx.ServiceName);
        startExit.ShouldBe(0,
            customMessage: $"U1h: service start MUST succeed after binary swap. " +
                          "If non-zero: v2 binary failed to load OR systemd refused to restart " +
                          "(check unit file ExecStart still references per-test path correctly).");

        WaitForActive(ctx.ServiceName, TimeSpan.FromSeconds(15)).ShouldBeTrue(
            customMessage: $"U1h: service '{ctx.ServiceName}' MUST reach active running v2 within 15s. " +
                          "If timeout: v2 binary crashes on startup, OR config-load failed (cert ownership " +
                          "broken by swap?), OR listening port collision. " +
                          $"\nstatus:\n{RunSystemctl("status", ctx.ServiceName).output}" +
                          $"\njournal:\n{RunJournalctl(ctx.ServiceName)}");

        // ── Phase 8: capture post-upgrade state + assert pin ──────────────
        var postVersionOutput = RunBinary(ctx.PerTestBinaryPath, "version").output;
        var postVersion = postVersionOutput.Trim();

        postVersion.ShouldBe(LinuxTentacleBinaryFixture.BuildVersionV2,
            customMessage: $"U1h pin (NEW VERSION ACTIVE): post-upgrade `version` MUST return v2 stamp " +
                          $"'{LinuxTentacleBinaryFixture.BuildVersionV2}'. Got: '{postVersion}'. " +
                          $"If still '{LinuxTentacleBinaryFixture.BuildVersion}': the swap didn't take effect — " +
                          "binary file wasn't actually overwritten, OR systemd cached the old binary somehow, " +
                          "OR the per-test binary path resolution drifted between install + start.");

        var postThumbprintOutput = RunBinary(ctx.PerTestBinaryPath, "show-thumbprint").output;
        var postThumbprint = ExtractThumbprint(postThumbprintOutput);

        // ── THE PIN: cert identity preserved through binary swap ──────────
        // This is the most operator-critical upgrade contract. If the
        // thumbprint changed, the agent's identity drifted — server's
        // trust list still has pre-upgrade thumbprint but agent now
        // presents post-upgrade. Polling fails silently after upgrade.
        postThumbprint.ToUpperInvariant().ShouldBe(preThumbprint.ToUpperInvariant(),
            customMessage: $"U1h pin (CERT IDENTITY PRESERVED): thumbprint MUST be IDENTICAL pre/post upgrade. " +
                          $"\n\nPre-upgrade:  {preThumbprint}\nPost-upgrade: {postThumbprint}\n\n" +
                          $"If different: TentacleCertificateManager regenerated the cert during v2's startup — " +
                          "agent's identity is now divorced from the server's trust list, polling will fail. " +
                          "Most likely cause: per-instance config's CertsPath got reset OR cert file got deleted " +
                          "during the binary-only swap (which it shouldn't — config + cert dir are separate from " +
                          "the binary file).");

        // Cleanup via production CLI's --purge (removes service unit +
        // config + cert dir + registry entry).
        var (purgeExit, _) = SudoRunBinary(ctx.PerTestBinaryPath,
            "service", "uninstall", "--purge", "--service-name", ctx.ServiceName);
        purgeExit.ShouldBe(0, "U1h cleanup: --purge must succeed");

        ctx.MarkServiceUninstalled();
        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the 40-char hex thumbprint from show-thumbprint's output
    /// (last hex match, defensive against Serilog log lines bleeding into
    /// stdout — same pattern as D1h Linux + W-D1h Windows).
    /// </summary>
    private static string ExtractThumbprint(string output)
    {
        var matches = Regex.Matches(output, @"\b[0-9A-Fa-f]{40}\b");
        return matches.Count > 0 ? matches[matches.Count - 1].Value : string.Empty;
    }

    /// <summary>
    /// Runs <paramref name="binaryPath"/> directly (no sudo) with the given
    /// args. Returns (exitCode, combined stdout+stderr). 60s wall-clock cap.
    /// Used for read-only commands (version, show-thumbprint) that don't
    /// need root.
    /// </summary>
    private static (int exitCode, string output) RunBinary(string binaryPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch {binaryPath}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{binaryPath} {string.Join(' ', args)} did not complete within 60s.");
        }
        return (proc.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
    }

    /// <summary>
    /// Runs <paramref name="binaryPath"/> as root via <c>sudo -n</c> with
    /// the given args. Used for commands that mutate /etc/systemd/system
    /// or run systemctl.
    /// </summary>
    private static (int exitCode, string output) SudoRunBinary(string binaryPath, params string[] args)
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
        psi.ArgumentList.Add(binaryPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch sudo {binaryPath}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"sudo {binaryPath} {string.Join(' ', args)} did not complete within 60s.");
        }
        return (proc.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
    }

    /// <summary>
    /// Copies an executable file via sudo (preserves permissions through
    /// chmod +x). Used by U1h to overwrite per-test v1 binary path with
    /// v2 binary content while keeping the file executable for systemd.
    /// </summary>
    private static void TrySudoCopyExecutable(string source, string dest)
    {
        TrySudo("cp", "-f", source, dest);
        TrySudo("chmod", "+x", dest);
    }

    private static void TrySudo(string cmd, params string[] args)
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
            psi.ArgumentList.Add(cmd);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(15_000);
        }
        catch { /* best-effort */ }
    }

    private static (int exitCode, string output) RunSystemctl(string verb, string serviceName)
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
        psi.ArgumentList.Add("systemctl");
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(serviceName);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sudo systemctl");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10_000);
        return (proc.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string RunJournalctl(string serviceName)
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
            psi.ArgumentList.Add("journalctl");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(serviceName);
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("30");
            psi.ArgumentList.Add("--no-pager");

            using var proc = Process.Start(psi);
            if (proc == null) return "(journalctl failed to start)";
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
            return stdout;
        }
        catch (Exception ex)
        {
            return $"(journalctl failed: {ex.Message})";
        }
    }

    private static bool WaitForActive(string serviceName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (exit, output) = RunSystemctl("is-active", serviceName);
            if (exit == 0 && output.Trim().StartsWith("active", StringComparison.OrdinalIgnoreCase))
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool WaitForInactive(string serviceName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (exit, output) = RunSystemctl("is-active", serviceName);
            if (exit != 0 && output.Trim().StartsWith("inactive", StringComparison.OrdinalIgnoreCase))
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>
    /// Per-test context: owns a per-test isolated v1 binary path
    /// (initially copied from fixture v1) so the upgrade swap doesn't
    /// corrupt the shared fixture binary. Owns the stub server +
    /// service name. Cleans up: service uninstall (defensive systemctl
    /// path if CLI uninstall didn't run) + rm per-test binary +
    /// /etc/squid-tentacle/ instance state.
    /// </summary>
    private sealed class UpgradeBinaryIntegrationTestContext : IDisposable
    {
        private bool _clean;
        private bool _serviceUninstalled;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public LinuxStubSquidServer Stub { get; } = LinuxStubSquidServer.Start();
        public string ServiceName { get; } = $"squid-tentacle-u1-{Guid.NewGuid():N}";
        public int ListeningPort { get; } = 51970;

        /// <summary>
        /// Per-test isolated path where the v1 binary is staged. The
        /// upgrade swap targets THIS path, not the fixture's shared
        /// path — so failure mid-test doesn't poison the fixture.
        /// </summary>
        public string PerTestBinaryPath { get; }

        private readonly string _perTestDir;

        public UpgradeBinaryIntegrationTestContext()
        {
            // Pre-create /etc/squid-tentacle/instances/ to mimic post-install
            // state (matches B3h / C1h / G1h precondition pattern).
            TrySudoCmd("mkdir", "-p", "/etc/squid-tentacle/instances");

            // Per-test isolated binary dir. /tmp is safe for this — systemd
            // can ExecStart binaries from /tmp on most distros (the runner's
            // /tmp doesn't have noexec).
            _perTestDir = $"/tmp/tentacle-u1-{Guid.NewGuid():N}";
            Directory.CreateDirectory(_perTestDir);

            PerTestBinaryPath = Path.Combine(_perTestDir, "Squid.Tentacle");

            // Copy v1 fixture binary to per-test path + make executable.
            File.Copy(Binary.EnsureBuilt(), PerTestBinaryPath, overwrite: true);
            // chmod 755 — needs to be executable by current user (test
            // process) AND root (systemd ExecStart).
            File.SetUnixFileMode(PerTestBinaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        public void MarkClean() => _clean = true;
        public void MarkServiceUninstalled() => _serviceUninstalled = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[UpgradeBinaryIntegrationTestContext] Dispose without MarkClean — service '{ServiceName}' test failed before completion.");

            // Defensive systemd cleanup if CLI --purge didn't run.
            if (!_serviceUninstalled)
            {
                TrySudoCmd("systemctl", "stop", ServiceName);
                TrySudoCmd("systemctl", "disable", ServiceName);
                TrySudoCmd("rm", "-f", $"/etc/systemd/system/{ServiceName}.service");
                TrySudoCmd("systemctl", "daemon-reload");
            }

            // /etc/squid-tentacle/ instance state cleanup.
            TrySudoCmd("rm", "-f", "/etc/squid-tentacle/instances/Default.config.json");
            TrySudoCmd("rm", "-rf", "/etc/squid-tentacle/instances/Default");
            TrySudoCmd("rm", "-f", "/etc/squid-tentacle/instances.json");

            // Per-test binary dir cleanup.
            try { if (Directory.Exists(_perTestDir)) Directory.Delete(_perTestDir, recursive: true); } catch { }

            try { Stub.Dispose(); } catch { /* best-effort */ }
        }

        private static void TrySudoCmd(string cmd, params string[] args)
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
                psi.ArgumentList.Add(cmd);
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(5_000);
            }
            catch { /* best-effort */ }
        }
    }
}
