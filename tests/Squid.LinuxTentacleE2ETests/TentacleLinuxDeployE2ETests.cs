using Squid.LinuxTentacleE2ETests.Infrastructure;
using Squid.Message.Contracts.Tentacle;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.D — first-cut Linux deploy E2E coverage. Closes part
/// of the P0 5.1 gap from the global audit: previously the Linux
/// project had ZERO Halibut deploy E2E tests despite Windows having
/// 13. Operators on a mixed fleet running the same documented
/// "register agent → server pushes script" workflow had no Linux
/// regression protection.
///
/// <para><b>Tier 🟢 high-fidelity</b> (Rule 12.4): real Halibut RPC
/// + real <c>LocalScriptService</c> (production agent-side script
/// runner) + real bash execution + real network round-trip on
/// loopback. Only mocked components are the upstream Squid server
/// (replaced by <see cref="SquidHalibutStubServer"/>) and the
/// agent's process (replaced by in-process
/// <see cref="SquidHalibutStubAgent"/> wrapping
/// <c>LocalScriptService</c>).</para>
///
/// <para><b>Coverage delta vs <see cref="TentacleLinuxUpgradeBinaryIntegrationE2ETests"/></b>:
/// that test pins the upgrade path's binary-swap mechanics. THIS
/// test pins the deploy path's server→agent script-dispatch round-
/// trip — different operator workflow, different production layer.</para>
///
/// <para><b>Why first-cut single test</b>: Phase 12.M.L.D is split:
/// PR-1 ported infrastructure + smoke (this PR is PR-2 with the
/// first real test). Subsequent PRs add the remaining ~12 deploy
/// scenarios (non-zero exit, multi-line output, stderr, long-running,
/// concurrent, polling, etc.) — each follows the pattern this PR
/// establishes.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
public sealed class TentacleLinuxDeployE2ETests
{
    // ========================================================================
    // L-D1.h-Linux — Listening: bash echo script dispatched + output captured
    //
    // Operator scenario: server has registered the Linux tentacle and
    // dispatches a deployment step (a script). Production path:
    //
    //   Server → Halibut RPC → Agent → bash → captured output → Halibut RPC → Server
    //
    // This test exercises the full round-trip in-process:
    //   1. Stub server starts (Halibut listener + cert)
    //   2. Stub agent starts as listening (registers itself)
    //   3. Server trusts agent's thumbprint
    //   4. Server dispatches `echo 'hello-from-linux-deploy-e2e'`
    //   5. Agent runs the bash script via LocalScriptService
    //   6. Output round-trips back via Halibut
    //   7. Test asserts exit 0 + output captured
    //
    // What this catches that script-tier tests don't:
    //   - Halibut serialization regressions (e.g. ProcessOutput line
    //     truncation)
    //   - LocalScriptService bash-spawn regression on Linux
    //     (PATH / shell-resolution issues)
    //   - Halibut listener/poller TLS handshake regression
    //   - Production observe-loop polling timing
    //
    // Tier: 🟢 H. Mirror of Windows
    // <c>TentacleDeployE2ETests.Listening_EchoScript_OutputCapturedAndExitZero</c>;
    // pins the same contract on Linux.
    // ========================================================================

    [Fact]
    public async Task LD1h_Listening_EchoBashScript_OutputCapturedAndExitZero()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Bash echo — Linux path (no Windows branch needed; this test is
        // skip-on-non-Linux above).
        const string scriptBody = "echo 'hello-from-linux-deploy-e2e'";
        const ScriptType scriptType = ScriptType.Bash;

        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0,
            customMessage: $"echo bash script MUST exit 0 on Linux. Got exit {result.ExitCode}.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain("hello-from-linux-deploy-e2e",
            customMessage: $"echo output MUST be captured in the Halibut RPC round-trip logs. " +
                          "If absent: LocalScriptService didn't spawn bash correctly OR Halibut serialization dropped " +
                          $"the ProcessOutput stream. " +
                          $"\nGot logs:\n{result.AllText}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="StartScriptCommand"/> with a unique ticket and
    /// dispatches it to the listening agent via the stub server's
    /// <see cref="SquidHalibutStubServer.DispatchAndObserveListeningAsync"/>.
    /// Local helper instead of a private nested class on the test (keeps
    /// the file flat; can be promoted to shared infra in a follow-up PR).
    /// </summary>
    private static async Task<ObservedScriptResult> DispatchAndObserveListeningAsync(
        SquidHalibutStubServer server,
        Uri agentUri,
        string agentThumbprint,
        string scriptBody,
        ScriptType scriptType,
        TimeSpan? observeTimeout = null)
    {
        var ticket = new ScriptTicket($"e2e-linux-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(1),
            null,
            Array.Empty<string>(),
            ticket.TaskId,
            TimeSpan.Zero)
        {
            ScriptSyntax = scriptType
        };

        var timeout = observeTimeout ?? TimeSpan.FromSeconds(30);
        return await server.DispatchAndObserveListeningAsync(agentUri, agentThumbprint, command, timeout, CancellationToken.None);
    }
}
