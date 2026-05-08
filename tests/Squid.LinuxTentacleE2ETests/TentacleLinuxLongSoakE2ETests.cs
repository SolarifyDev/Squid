using System.Diagnostics;
using System.Globalization;
using Squid.LinuxTentacleE2ETests.Infrastructure;
using Squid.Message.Contracts.Tentacle;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// P1-#5 — long-soak E2E. Runs the polling agent for a configurable
/// window (default 120s; operator can crank via env var) with periodic
/// dispatches + capability probes interleaved. Catches issues invisible
/// to short tests:
///
/// <list type="bullet">
///   <item><b>Memory leak</b> — each dispatch allocates LogWriter +
///         workdir + Process. Pre-fix LocalScriptService leaked
///         ScriptCancellationRegistry entries (Phase 12.G follow-up
///         already pinned at unit tier; E2E catches the higher-level
///         compositional leaks)</item>
///   <item><b>File descriptor leak</b> — each dispatch opens stdin/
///         stdout/stderr pipes. /proc/&lt;pid&gt;/fd should stabilise
///         (process FD count returns to baseline after each dispatch
///         completes + cleans up)</item>
///   <item><b>Polling-loop drift</b> — Halibut polling client uses
///         Task.Delay-based backoff. A regression that stretches the
///         delay (e.g. accidental TimeSpan.FromHours instead of
///         FromSeconds) wouldn't surface in 25s soak (R2h) but would
///         in 2-min soak</item>
///   <item><b>TLS session expiry</b> — long-running connections may
///         negotiate fresh sessions; if cert/key handling regresses,
///         dispatches fail mid-soak</item>
/// </list>
///
/// <para><b>Tier 🟢 H</b> per Rule 12.4. Real binary, real systemd,
/// real Halibut. Same fidelity as R1h/R2h.</para>
///
/// <para><b>Configurable duration</b>: <c>SQUID_TENTACLE_E2E_SOAK_SECONDS</c>
/// env var overrides the default. CI nightly might set 300; PR runs
/// keep the default 120 for cycle-time. Min 30, max 1800 (30 min).</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxLongSoakE2ETests
{
    /// <summary>
    /// Operator override for soak duration. Default 120s — short enough
    /// to keep CI cycle time reasonable while long enough to catch
    /// most leak-class regressions. Operators investigating a suspected
    /// leak can crank this to 600s+ via workflow_dispatch input.
    ///
    /// <para>Pinned literal — renaming breaks operators who set this
    /// for diagnostic runs.</para>
    /// </summary>
    public const string SoakDurationEnvVar = "SQUID_TENTACLE_E2E_SOAK_SECONDS";

    public const int DefaultSoakSeconds = 120;
    public const int MinSoakSeconds = 30;
    public const int MaxSoakSeconds = 30 * 60;  // 30 min hard cap

    /// <summary>Inter-dispatch delay during the soak. ~10s gives a
    /// genuine "production-ish" rate (12 dispatches over 2 min).</summary>
    private const int DispatchEverySeconds = 10;

    /// <summary>Inter-probe delay during the soak. ~15s so probes
    /// happen between dispatches, not concurrently.</summary>
    private const int ProbeEverySeconds = 15;

    /// <summary>Acceptable VmRSS growth over the soak window (KB).
    /// Production startup is ~150MB; a 50MB growth over 2min would
    /// indicate a runaway leak. Tightening to 20MB once we have
    /// data — for now wide bound to avoid false positives.</summary>
    private const long MaxVmRssGrowthKb = 50 * 1024;

    /// <summary>Acceptable FD count growth over the soak window.
    /// Each dispatch opens ~3 pipes that close on completion; net
    /// growth should be 0. Tolerance 20 for transient runtime
    /// behaviour (GC finalizer queues, etc.).</summary>
    private const int MaxFdGrowth = 20;

    // ========================================================================
    // R6.h-Linux — long soak: continuous dispatch + probe loop, assert no leaks
    //
    // Operator scenario: Tentacle's been polling for weeks, deployment
    // pipeline has dispatched thousands of scripts. Memory + FD usage
    // should stay bounded. If a leak ships, the agent OOMs after some
    // weeks (specific outage class not caught by short tests).
    //
    // Mechanism (5 phases):
    //   1. Setup polling agent (R1h-style: register + install + active +
    //      polling channel up)
    //   2. Capture baseline VmRSS + FD count
    //   3. Soak loop (default 120s):
    //      - Dispatch every 10s
    //      - Probe capabilities every 15s
    //      - On each dispatch: assert exit 0 + marker round-trips
    //      - On each probe: assert version stable
    //   4. Capture final VmRSS + FD count
    //   5. Assertions:
    //      - All N dispatches passed
    //      - All probes returned consistent version
    //      - Process still alive
    //      - VmRSS growth < MaxVmRssGrowthKb
    //      - FD growth < MaxFdGrowth
    //
    // Tier: 🟢 H. Same fidelity as R1h/R2h, just over a longer window.
    //
    // Expected runtime: SoakDurationEnvVar (default 120s) + setup ~20s +
    //                   cleanup ~5s = ~145s default; up to 30 min if
    //                   operator cranks the env var.
    // ========================================================================

    [Fact]
    public async Task R6h_PollingAgent_LongSoak_NoMemoryOrFdLeaks()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        var soakSeconds = ResolveSoakSeconds();

        await using var ctx = await LongSoakContext.CreateAsync();

        // ── Phase 1: setup (R1h-style) ────────────────────────────────────
        var (regExit, regOutput) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.ServerUri.ToString().TrimEnd('/'),
            "--comms-url", ctx.Stub.PollingUri.ToString().TrimEnd('/'),
            "--api-key", "API-R6H-LONGSOAK-1234",
            "--role", "r6h-soak-agent",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");
        regExit.ShouldBe(0, $"R6h precondition: register MUST succeed.\noutput:\n{regOutput}");

        var registration = ctx.Stub.ReceivedRegistrations.Single();
        var agentThumbprint = registration.AgentThumbprint;
        var agentSubscriptionId = registration.SubscriptionId;
        ctx.Stub.TrustAgent(agentThumbprint);

        var (installExit, _) = ctx.Binary.SudoRun(
            "service", "install", "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0, "R6h precondition: service install MUST succeed");

        WaitForActive(ctx.ServiceName, TimeSpan.FromSeconds(20)).ShouldBeTrue();

        var initialCaps = await WaitForPollingChannelAsync(
            ctx.Stub, agentSubscriptionId, agentThumbprint, TimeSpan.FromSeconds(30));
        initialCaps.ShouldNotBeNull("R6h precondition: initial polling channel MUST come up");
        var initialVersion = initialCaps.AgentVersion;

        // ── Phase 2: capture baseline VmRSS + FD ──────────────────────────
        var pid = ResolveServicePid(ctx.ServiceName);
        pid.ShouldBeGreaterThan(0,
            "R6h precondition: must resolve service PID via systemctl show MainPID");

        var baselineVmRssKb = ReadVmRssKb(pid);
        var baselineFdCount = ReadFdCount(pid);

        baselineVmRssKb.ShouldBeGreaterThan(0,
            $"R6h precondition: baseline VmRSS read failed (pid {pid} /proc unreadable?)");
        baselineFdCount.ShouldBeGreaterThan(0,
            $"R6h precondition: baseline FD count read failed (pid {pid} /proc unreadable?)");

        // ── Phase 3: soak loop ────────────────────────────────────────────
        var soakStart = DateTime.UtcNow;
        var soakDeadline = soakStart + TimeSpan.FromSeconds(soakSeconds);

        var dispatchCount = 0;
        var probeCount = 0;
        var nextDispatchAt = soakStart + TimeSpan.FromSeconds(DispatchEverySeconds);
        var nextProbeAt = soakStart + TimeSpan.FromSeconds(ProbeEverySeconds);

        var probeVersions = new List<string>();
        var failedDispatches = new List<string>();

        while (DateTime.UtcNow < soakDeadline)
        {
            var now = DateTime.UtcNow;

            if (now >= nextDispatchAt)
            {
                dispatchCount++;
                var marker = $"r6h-soak-{dispatchCount:D3}-{Guid.NewGuid():N}";
                var ticket = new ScriptTicket($"r6h-soak-{Guid.NewGuid():N}");
                var command = new StartScriptCommand(
                    ticket,
                    $"sleep 1; echo '{marker}'",
                    ScriptIsolationLevel.NoIsolation,
                    TimeSpan.FromMinutes(1),
                    null,
                    Array.Empty<string>(),
                    ticket.TaskId,
                    TimeSpan.Zero)
                {
                    ScriptSyntax = ScriptType.Bash
                };

                try
                {
                    using var dispatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var result = await ctx.Stub.DispatchAndObservePollingAsync(
                        agentSubscriptionId, agentThumbprint, command,
                        TimeSpan.FromSeconds(15), dispatchCts.Token);

                    if (result.ExitCode != 0 || !result.AllText.Contains(marker))
                        failedDispatches.Add($"#{dispatchCount} (t+{(now - soakStart).TotalSeconds:F0}s): exit={result.ExitCode}, marker present={result.AllText.Contains(marker)}");
                }
                catch (Exception ex)
                {
                    failedDispatches.Add($"#{dispatchCount} (t+{(now - soakStart).TotalSeconds:F0}s): exception {ex.GetType().Name}: {ex.Message}");
                }

                nextDispatchAt = now + TimeSpan.FromSeconds(DispatchEverySeconds);
            }

            if (now >= nextProbeAt)
            {
                probeCount++;
                try
                {
                    using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var caps = await ctx.Stub.ProbeCapabilitiesPollingAsync(
                        agentSubscriptionId, agentThumbprint, probeCts.Token);
                    probeVersions.Add(caps.AgentVersion);
                }
                catch (Exception ex)
                {
                    probeVersions.Add($"<exception:{ex.GetType().Name}>");
                }

                nextProbeAt = now + TimeSpan.FromSeconds(ProbeEverySeconds);
            }

            await Task.Delay(500);
        }

        // ── Phase 4: capture final VmRSS + FD ─────────────────────────────
        var finalVmRssKb = ReadVmRssKb(pid);
        var finalFdCount = ReadFdCount(pid);
        var actualSoakDuration = DateTime.UtcNow - soakStart;

        // ── Phase 5: assertions ───────────────────────────────────────────

        // (a) All dispatches succeeded.
        failedDispatches.ShouldBeEmpty(
            customMessage: $"R6h: {failedDispatches.Count} of {dispatchCount} dispatches failed during {actualSoakDuration.TotalSeconds:F0}s soak. " +
                          $"Production-equivalent: agent silently dropping deployment tasks. " +
                          $"\n\nFailures:\n{string.Join("\n", failedDispatches)}");

        dispatchCount.ShouldBeGreaterThanOrEqualTo(soakSeconds / DispatchEverySeconds - 2,
            customMessage: $"R6h: only {dispatchCount} dispatches over {actualSoakDuration.TotalSeconds:F0}s — " +
                          $"expected at least ~{soakSeconds / DispatchEverySeconds}. Soak loop drifted or stalled.");

        // (b) All probes returned consistent version.
        probeVersions.Distinct().Count().ShouldBe(1,
            customMessage: $"R6h: probe versions varied during soak: [{string.Join(", ", probeVersions.Distinct())}]. " +
                          "Capabilities reporting MUST be stable across the agent's lifetime.");
        probeVersions[0].ShouldBe(initialVersion,
            customMessage: $"R6h: first soak-loop probe version '{probeVersions[0]}' != initial '{initialVersion}'. " +
                          "Agent's CapabilitiesService is non-deterministic.");

        // (c) Process still alive.
        var (postIsActiveExit, postIsActiveOutput) = RunSystemctl("is-active", ctx.ServiceName);
        postIsActiveExit.ShouldBe(0,
            customMessage: $"R6h: service '{ctx.ServiceName}' MUST still be active after {actualSoakDuration.TotalSeconds:F0}s soak. " +
                          $"Got exit {postIsActiveExit}, status: '{postIsActiveOutput.Trim()}'.");

        // (d) VmRSS growth bounded.
        var vmRssGrowthKb = finalVmRssKb - baselineVmRssKb;
        vmRssGrowthKb.ShouldBeLessThanOrEqualTo(MaxVmRssGrowthKb,
            customMessage: $"R6h: VmRSS grew {vmRssGrowthKb / 1024.0:F1}MB during {actualSoakDuration.TotalSeconds:F0}s soak " +
                          $"(baseline {baselineVmRssKb / 1024.0:F1}MB → final {finalVmRssKb / 1024.0:F1}MB). " +
                          $"Cap is {MaxVmRssGrowthKb / 1024}MB. " +
                          $"Memory-leak class regression: extrapolating to days/weeks of production runtime, " +
                          "the agent OOMs. Investigate whether the leak grows linearly with dispatch count " +
                          $"(was {dispatchCount} dispatches) or remains roughly flat after the first few.");

        // (e) FD growth bounded.
        var fdGrowth = finalFdCount - baselineFdCount;
        fdGrowth.ShouldBeLessThanOrEqualTo(MaxFdGrowth,
            customMessage: $"R6h: FD count grew by {fdGrowth} during {actualSoakDuration.TotalSeconds:F0}s soak " +
                          $"(baseline {baselineFdCount} → final {finalFdCount}). Cap is {MaxFdGrowth}. " +
                          $"FD-leak class regression: ulimit (default 1024) hits after ~{1024 / Math.Max(fdGrowth, 1)}min " +
                          "of soak — production blocks new connections. Each dispatch's process pipes (stdin/stdout/stderr) " +
                          "should close on script completion via Process.Dispose.");

        // ── Cleanup ────────────────────────────────────────────────────────
        var (uninstallExit, _) = ctx.Binary.SudoRun(
            "service", "uninstall", "--purge", "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0);

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the soak duration from <see cref="SoakDurationEnvVar"/>,
    /// clamped to <c>[MinSoakSeconds, MaxSoakSeconds]</c>. Default
    /// <see cref="DefaultSoakSeconds"/> when unset.
    /// </summary>
    internal static int ResolveSoakSeconds()
    {
        var raw = Environment.GetEnvironmentVariable(SoakDurationEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultSoakSeconds;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return DefaultSoakSeconds;
        return Math.Clamp(seconds, MinSoakSeconds, MaxSoakSeconds);
    }

    /// <summary>
    /// Reads the service's main PID via <c>systemctl show -p MainPID</c>.
    /// Returns 0 on failure.
    /// </summary>
    private static int ResolveServicePid(string serviceName)
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
            psi.ArgumentList.Add("systemctl");
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("MainPID");
            psi.ArgumentList.Add("--value");
            psi.ArgumentList.Add(serviceName);

            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5_000);
            return int.TryParse(stdout, out var pid) ? pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads VmRSS (resident set size, kB) from
    /// <c>/proc/&lt;pid&gt;/status</c>. Uses sudo because the systemd
    /// service runs as root (or service user), and this test process
    /// runs as `runner` — non-self <c>/proc/&lt;pid&gt;/status</c>
    /// reads require either same-user or root via sudo on Linux.
    ///
    /// <para>Round-3 first-runner CI surfaced this:
    /// <c>baselineFdCount == 0</c> because <c>Directory.GetFileSystemEntries</c>
    /// on a root-owned <c>/proc/&lt;pid&gt;/fd</c> returned empty for
    /// the non-root test process. Same applies (less critically) to
    /// VmRSS reads — wrapping both in sudo is the symmetric fix.</para>
    ///
    /// <para>Returns 0 on failure (parse error, permission denied, process
    /// gone).</para>
    /// </summary>
    private static long ReadVmRssKb(int pid)
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
            psi.ArgumentList.Add("cat");
            psi.ArgumentList.Add($"/proc/{pid}/status");

            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
            if (proc.ExitCode != 0) return 0;

            foreach (var line in stdout.Split('\n'))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                // Format: "VmRSS:    12345 kB"
                if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                    return kb;
            }
        }
        catch
        {
            // sudo failed / process gone / parse error — return 0
        }
        return 0;
    }

    /// <summary>
    /// Reads the count of file descriptors from
    /// <c>/proc/&lt;pid&gt;/fd</c> via <c>sudo ls</c>. Each entry is
    /// one open FD.
    ///
    /// <para><b>Why sudo</b>: <c>/proc/&lt;pid&gt;/fd</c> is owned by
    /// the process owner (root for systemd-launched service); the test
    /// process runs as <c>runner</c> on GHA. Non-self <c>/proc</c> FD
    /// directory enumeration requires root via sudo.</para>
    ///
    /// <para>Returns 0 on failure (sudo declined, process gone,
    /// permission still denied).</para>
    /// </summary>
    private static int ReadFdCount(int pid)
    {
        try
        {
            // `sudo -n ls /proc/<pid>/fd | wc -l` would need a shell;
            // use sudo-ls + count lines in stdout instead.
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("ls");
            psi.ArgumentList.Add("-1");
            psi.ArgumentList.Add($"/proc/{pid}/fd");

            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
            if (proc.ExitCode != 0) return 0;

            // Count non-empty lines.
            var count = 0;
            foreach (var line in stdout.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) count++;
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<CapabilitiesResponse> WaitForPollingChannelAsync(
        StubSquidServer stub, string agentSubscriptionId, string agentThumbprint, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                return await stub.ProbeCapabilitiesPollingAsync(
                    agentSubscriptionId, agentThumbprint, probeCts.Token);
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        return null;
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

    /// <summary>
    /// Per-test context. Mirrors RealBinaryPollingContext (R1h) but
    /// scoped to long-soak markers.
    /// </summary>
    private sealed class LongSoakContext : IAsyncDisposable
    {
        private bool _clean;
        private bool _uninstalledViaCli;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public StubSquidServer Stub { get; }
        public string ServiceName { get; } = $"squid-tentacle-r6h-{Guid.NewGuid():N}";

        private LongSoakContext(StubSquidServer stub)
        {
            Stub = stub;
            TrySudo("mkdir", "-p", "/etc/squid-tentacle/instances");
        }

        public static async Task<LongSoakContext> CreateAsync()
        {
            var stub = await StubSquidServer.StartAsync();
            return new LongSoakContext(stub);
        }

        public void MarkUninstalled() => _uninstalledViaCli = true;
        public void MarkClean() => _clean = true;

        public async ValueTask DisposeAsync()
        {
            if (!_clean)
                Console.WriteLine($"[LongSoakContext] Dispose without MarkClean — R6h test for '{ServiceName}' failed.");

            if (!_uninstalledViaCli)
            {
                TrySudo("systemctl", "stop", ServiceName);
                TrySudo("systemctl", "disable", ServiceName);
                TrySudo("rm", "-f", $"/etc/systemd/system/{ServiceName}.service");
                TrySudo("systemctl", "daemon-reload");
            }

            TrySudo("rm", "-f", "/etc/squid-tentacle/instances/Default.config.json");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default");
            TrySudo("rm", "-f", "/etc/squid-tentacle/instances.json");
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle/instances");
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle");

            try { await Stub.DisposeAsync(); } catch { /* best-effort */ }
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
                proc?.WaitForExit(5_000);
            }
            catch { /* best-effort */ }
        }
    }
}
