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

    // ========================================================================
    // L-D2.u1-Linux — Non-zero exit code propagates EXACTLY (not normalised)
    // ========================================================================

    [Fact]
    public async Task LD2u1_Listening_NonZeroExit_PropagatesExactExitCode()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Exit 42 — specifically NOT 0 or 1, proves the exit code is
        // propagated EXACTLY through the Halibut RPC + ProcessOutput
        // pipeline, not normalised to 1 by an intermediate layer.
        const int expectedExit = 42;
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            $"exit {expectedExit}", ScriptType.Bash);

        result.ExitCode.ShouldBe(expectedExit,
            customMessage: $"exit code {expectedExit} MUST be propagated exactly. Got: {result.ExitCode}. " +
                          $"If 1: normalisation regression in LocalScriptService OR Halibut serialization. " +
                          $"\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D3.h-Linux — Multi-line stdout captured + order preserved
    // ========================================================================

    [Fact]
    public async Task LD3h_Listening_MultiLineOutput_AllLinesCapturedInOrder()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string scriptBody = "echo 'line-one'\necho 'line-two'\necho 'line-three'";
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            scriptBody, ScriptType.Bash);

        result.ExitCode.ShouldBe(0,
            customMessage: $"multi-line script MUST exit 0.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain("line-one", customMessage: $"first line missing.\nLogs:\n{result.AllText}");
        result.AllText.ShouldContain("line-two", customMessage: $"second line missing.\nLogs:\n{result.AllText}");
        result.AllText.ShouldContain("line-three", customMessage: $"third line missing.\nLogs:\n{result.AllText}");

        // Order preservation: line-one BEFORE line-two BEFORE line-three.
        // Catches log-stream reorder regressions in the agent's
        // ProcessOutput emission OR Halibut RPC log batching.
        var idxOne = result.AllText.IndexOf("line-one", StringComparison.Ordinal);
        var idxTwo = result.AllText.IndexOf("line-two", StringComparison.Ordinal);
        var idxThree = result.AllText.IndexOf("line-three", StringComparison.Ordinal);
        idxOne.ShouldBeLessThan(idxTwo,
            customMessage: $"log order regressed: 'line-one' MUST appear before 'line-two'. Got idxOne={idxOne}, idxTwo={idxTwo}.");
        idxTwo.ShouldBeLessThan(idxThree,
            customMessage: $"log order regressed: 'line-two' MUST appear before 'line-three'. Got idxTwo={idxTwo}, idxThree={idxThree}.");
    }

    // ========================================================================
    // L-D4.h-Linux — Stderr output captured (and tagged as StdErr in Source)
    // ========================================================================

    [Fact]
    public async Task LD4h_Listening_StderrOutput_CapturedAndTaggedAsStdErr()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string marker = "error-message-on-stderr";
        // bash redirect to fd 2 — the canonical stderr write.
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            $"echo '{marker}' >&2", ScriptType.Bash);

        // stderr alone shouldn't fail the script (exit 0 expected).
        result.ExitCode.ShouldBe(0,
            customMessage: $"echo to stderr MUST not fail the script.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(marker,
            customMessage: $"stderr message MUST be captured by the agent and surfaced to the server. " +
                          $"If absent: LocalScriptService.StartScript stderr capture is broken OR Halibut " +
                          $"ProcessOutput serialization dropped Source=StdErr lines. " +
                          $"\nLogs:\n{result.AllText}");

        // Cross-pin: at least one ProcessOutput line tagged as StdErr.
        // Catches a regression where stderr surfaces as StdOut (operators
        // using log filters by Source would miss the error).
        var hasStdErrTagged = result.AllLogs.Any(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains(marker, StringComparison.Ordinal));
        hasStdErrTagged.ShouldBeTrue(
            customMessage: $"at least one ProcessOutput line MUST be tagged Source=StdErr containing '{marker}'. " +
                          $"If only StdOut-tagged: stderr/stdout conflation regression.\n" +
                          $"All logs:\n{string.Join("\n", result.AllLogs.Select(l => $"  [{l.Source}] {l.Text}"))}");
    }

    // ========================================================================
    // L-D5.h-Linux — Long-running script (3s sleep) completes, output captured
    // ========================================================================

    [Fact]
    public async Task LD5h_Listening_LongRunningScript_CompletesAndCapturesOutput()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string marker = "after-3s-sleep";
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            $"sleep 3; echo '{marker}'", ScriptType.Bash,
            observeTimeout: TimeSpan.FromSeconds(15));

        result.ExitCode.ShouldBe(0,
            customMessage: $"long-running script MUST exit 0 cleanly.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(marker,
            customMessage: $"output emitted AFTER the sleep MUST be captured. " +
                          $"If absent: agent's StartScript timeout fired prematurely OR " +
                          $"the observe loop stopped polling before final logs flushed. " +
                          $"\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D6.h-Linux — Unicode output round-trips correctly (UTF-8 contract)
    // ========================================================================

    [Fact]
    public async Task LD6h_Listening_UnicodeOutput_RoundTripsCorrectly()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Mix of multi-byte UTF-8: Chinese + emoji + accented Latin.
        // If any layer (script invocation, stdout reader, Halibut
        // serialization, deserialization) corrupts encoding, the marker
        // won't match.
        const string marker = "deploy-unicode-測試-🚀-é";
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            $"echo '{marker}'", ScriptType.Bash);

        result.ExitCode.ShouldBe(0,
            customMessage: $"unicode echo MUST exit 0.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(marker,
            customMessage: $"unicode marker '{marker}' MUST round-trip exactly through bash → ProcessOutput → Halibut RPC → server logs. " +
                          $"If absent: encoding regression at one of the layers (most likely bash invocation locale " +
                          $"OR Halibut JSON serialization defaulting to latin-1). " +
                          $"\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D7.h-Linux — Output variable round-trip (no base64 encoding)
    //
    // Production agent's IScriptService captures `##squid[setVariable
    // name='X' value='Y']` log lines and surfaces them as ScriptOutputVariables
    // in the ScriptStatusResponse. Mirror of Windows
    // <c>Listening_OutputVariableWithoutBase64Encoding_RoundTripsCorrectly</c>;
    // counterpart base64 variant in PR-4+.
    // ========================================================================

    [Fact]
    public async Task LD7h_Listening_OutputVariable_NoBase64_RoundTripsCorrectly()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string varName = "MyOutputVar";
        const string varValue = "value-from-deploy-no-base64";
        // Plain service-message format — no base64 encoding.
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            $"echo \"##squid[setVariable name='{varName}' value='{varValue}']\"",
            ScriptType.Bash);

        result.ExitCode.ShouldBe(0,
            customMessage: $"output-variable script MUST exit 0.\nLogs:\n{result.AllText}");

        // The service-message line itself must surface in logs (operators
        // tail logs to verify the agent actually emitted it).
        result.AllText.ShouldContain(varName,
            customMessage: $"output-variable line MUST surface in logs (agent didn't echo, OR Halibut dropped it). " +
                          $"\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(varValue,
            customMessage: $"output-variable VALUE MUST surface in logs.\nLogs:\n{result.AllText}");

        // Note: parsing the service-message + extracting the variable into
        // a typed structure is the SERVER's responsibility (see
        // VariableExpander / DeploymentTaskExecutor.CaptureOutputVariables);
        // for THIS test we just verify the line round-trips through the
        // dispatch path. Server-side parsing is covered in
        // Squid.E2ETests/.../KubernetesVariableSubstitutionE2ETests.
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
