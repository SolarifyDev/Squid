using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.H — Linux boundary / operator-misuse tests. Closes
/// gaps between Linux + Windows boundary coverage:
///
/// <list type="bullet">
///   <item><b>H1.u1 Linux</b>: register with --server pointing at a
///         dead host (connection refused). Mirrors Windows
///         <c>TentacleRegisterE2ETests.ServerUnreachable_ExitsNonZero</c>
///         (C1.u2). Catches "register silently treated network errors
///         as success" regressions.</item>
///   <item><b>H2.u1 Linux</b>: register without required <c>--server</c>
///         flag → clear usage error. Mirrors Windows
///         <c>MissingServerArg</c> (C3.u1). Catches the "binary launches
///         half-configured + crashes deep in the registrar" regression.</item>
///   <item><b>H3.u1 Linux</b>: service install without sudo → must fail
///         with a clear error, not write a half-formed unit file.
///         Catches a real operator misuse path: forgetting to run with
///         sudo and then wondering why their tentacle doesn't start
///         on reboot.</item>
/// </list>
///
/// <para><b>Coverage delta vs existing Linux Section C unhappy tests</b>:
/// C1.u1 (401) and C1.u4 (non-JSON body) cover server-side rejections.
/// THIS file covers operator-side misuse: bad URL (network), missing
/// flag (usage), missing privilege (auth). All exit-code-meaningful
/// paths operator tooling depends on — exit 0 vs non-zero is the
/// branch every fleet-automation script switches on.</para>
///
/// <para>Tier: 🟢 high-fidelity (Rule 12.4) — real production binary
/// + real OS error paths (TCP connection refused, missing CLI args
/// validation, sudo permission rejection).</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxBoundaryE2ETests
{
    // ========================================================================
    // H1.u1-Linux — register --server unreachable → exit non-zero
    //
    // Operator scenario: operator typo'd the server URL or the server is
    // genuinely down for maintenance. Production register MUST fail
    // clearly so fleet-automation scripts that branch on `$?` see the
    // error and stop. If register silently exits 0 on connection failure,
    // every fleet that ever has a transient server outage gets a wave
    // of agents that THINK they registered but the server has no record.
    //
    // Mechanism: start a stub on a random port, immediately stop it.
    // Use that now-dead port as --server URL. Connection refused.
    //
    // Tier: 🟢 H. Real binary + real network error path.
    // ========================================================================

    [Fact]
    public void H1u1_Register_ServerUnreachable_ExitsNonZero()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        // Reserve a port via the stub, then dispose to free it for the
        // unreachable test. Any subsequent connect to that port → ECONNREFUSED.
        var unreachableUrl = ReserveUnreachablePort();

        var binary = new LinuxTentacleBinaryFixture();
        var (exitCode, output) = binary.SudoRun(
            "register",
            "--server", unreachableUrl,
            "--api-key", "API-H1u1-test",
            "--role", "h1-role",
            "--environment", "test",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldNotBe(0,
            customMessage: $"register against unreachable {unreachableUrl} MUST exit non-zero. Got exit {exitCode}. " +
                          "If 0: register's HTTP client is silently treating connection refused as success. " +
                          "Fleet automation scripts branching on $? would think the agent registered when it didn't. " +
                          $"\noutput:\n{output}");

        // Must mention the network failure somehow (HttpRequestException,
        // connection refused, etc.) so the operator can diagnose.
        // Defensive substring check — exact wording varies by .NET version.
        var hasNetworkErrorIndicator =
            output.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("HttpRequestException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Failed to register", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("unreachable", StringComparison.OrdinalIgnoreCase);

        hasNetworkErrorIndicator.ShouldBeTrue(
            customMessage: $"register output MUST surface a network-error indicator (connection/refused/HttpRequestException/...). " +
                          "If absent: operator gets a non-zero exit with no diagnostic — they have to guess whether it was 401, 500, network, or what. " +
                          $"\noutput:\n{output}");
    }

    // ========================================================================
    // H2.u1-Linux — register without --server arg → clear usage error
    //
    // Operator scenario: typo, forgot the flag, copy-pasted from a
    // tutorial that had the value as a placeholder. Production register
    // MUST exit non-zero AND print a usage hint so the operator knows
    // what's missing.
    //
    // Without this pin, the binary might launch RegisterCommand,
    // construct a default settings object, and crash deep in the
    // registrar with a stack trace — operator sees a 500-line error
    // dump instead of "Error: --server is required".
    //
    // Tier: 🟢 H. Real binary + real CLI validation path.
    // ========================================================================

    [Fact]
    public void H2u1_Register_MissingServerArg_ExitsWithUsageError()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        var binary = new LinuxTentacleBinaryFixture();

        // Notably MISSING --server. Other args present so the failure
        // is specifically about the missing --server, not a generic
        // CLI parse error.
        var (exitCode, output) = binary.Run(
            "register",
            "--api-key", "API-H2u1-test",
            "--role", "h2-role",
            "--environment", "test",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldNotBe(0,
            customMessage: $"register without --server MUST exit non-zero. Got exit {exitCode}. " +
                          "If 0: required-flag validation regressed — half-configured registers will silently fail downstream. " +
                          $"\noutput:\n{output}");

        // Operator-visible hint about which flag is missing. Production
        // RegisterCommand's "Error: --server is required" + Usage line.
        output.ShouldContain("--server",
            customMessage: $"output MUST mention the missing '--server' flag so the operator knows what to fix. " +
                          $"If absent: error is too generic — operator sees a non-zero exit and has to guess. " +
                          $"\noutput:\n{output}");
    }

    // ========================================================================
    // H3.u1-Linux — service install without sudo → fails cleanly (no
    //                half-formed unit file)
    //
    // Operator scenario: operator forgets to prefix `sudo` and runs:
    //
    //   squid-tentacle service install
    //
    // The systemd unit lives at /etc/systemd/system/ (root-only writable).
    // Production MUST fail clearly without writing a partial file.
    //
    // What this test catches: a regression where service install would
    // attempt the systemctl invocation, get a permission error, but
    // still leave a partial unit file on disk OR exit 0 with a stale
    // unit. Operator's `service start` later uses stale config.
    //
    // Tier: 🟢 H. Real binary + real sudo/permission path.
    //
    // Note: we use Run() (NOT SudoRun) to deliberately invoke without
    // privileges. Expected: exit non-zero + no /etc/systemd/system/
    // unit file written.
    // ========================================================================

    [Fact]
    public void H3u1_ServiceInstall_WithoutSudo_FailsCleanly()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        var binary = new LinuxTentacleBinaryFixture();
        var serviceName = $"squid-tentacle-h3-{Guid.NewGuid():N}";

        // Run WITHOUT sudo. Should fail to write /etc/systemd/system/...
        // and exit non-zero.
        var (exitCode, output) = binary.Run(
            "service", "install", "--service-name", serviceName
        );

        exitCode.ShouldNotBe(0,
            customMessage: $"service install without sudo MUST exit non-zero. Got exit {exitCode}. " +
                          "If 0: production wrote a partial unit file OR succeeded somehow without root — " +
                          "the latter would mean systemd unit dir is world-writable (security regression). " +
                          $"\noutput:\n{output}");

        // No unit file should have been written — the failure must be
        // BEFORE the file write. Validates fail-fast contract.
        var unitPath = $"/etc/systemd/system/{serviceName}.service";
        LinuxInstallScriptContext.SudoFileExists(unitPath).ShouldBeFalse(
            customMessage: $"unit file at {unitPath} MUST NOT exist after a failed (non-sudo) service install. " +
                          "If present: production wrote a partial file before the systemctl error path fired. " +
                          "Operator's subsequent `sudo service start` would use the partial config.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reserves a random loopback port via TcpListener(0), gets its
    /// bound port, then immediately disposes — leaving the port free
    /// but unbound. Any subsequent connect to <c>http://127.0.0.1:port</c>
    /// returns ECONNREFUSED.
    ///
    /// <para>Same trick the Windows ServerUnreachable test uses; ensures
    /// the test isn't dependent on an arbitrary port being free or some
    /// existing service responding unexpectedly.</para>
    /// </summary>
    private static string ReserveUnreachablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }
}
