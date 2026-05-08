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
        // Plain service-message format — no base64 encoding. Sleep 1s
        // before emit (timing-resilience) — same pattern Windows
        // EmitServiceMessage adopted: bash spawn time can occasionally
        // overlap with the emit, leaving the agent's stdout reader
        // unattached when the line surfaces. Caught by PR #266 first
        // runner where LD7h failed in an unrelated PR (new-certificate
        // change shouldn't affect LD7h, surfacing the underlying flake).
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            $"sleep 1; echo \"##squid[setVariable name='{varName}' value='{varValue}']\"",
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

    // ========================================================================
    // L-D8.h-Linux — Polling: echo script dispatched + output captured
    //
    // Tests the POLLING dispatch path (vs Listening). In production,
    // operators pick polling for tentacles behind firewalls (agent
    // dials out, server queues commands). This test exercises:
    //
    //   Stub server → Halibut polling listener → Agent polls in →
    //   Server queues StartScript → Agent runs bash → output back
    //
    // Different code path from Listening (server-initiated TCP) and
    // can break independently. Mirror of Windows
    // <c>Polling_EchoScript_OutputCapturedAndExitZero</c>.
    //
    // Uses `sleep 1; echo` (not bare echo) to ensure output emits
    // AFTER the stdout reader is attached — same timing-resilience
    // pattern as Windows polling tests after their flaky-runs analysis.
    // ========================================================================

    [Fact]
    public async Task LD8h_Polling_EchoBashScript_OutputCapturedAndExitZero()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartPollingAsync(server.PollingUri, server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string marker = "hello-from-linux-polling-deploy";
        // sleep 1 + echo: gives bash spawn + stdout reader attach time
        // before the marker is emitted (timing-resilience pattern).
        var result = await DispatchAndObservePollingAsync(server, agent.SubscriptionId, agent.Thumbprint,
            $"sleep 1; echo '{marker}'", ScriptType.Bash,
            observeTimeout: TimeSpan.FromSeconds(15));

        result.ExitCode.ShouldBe(0,
            customMessage: $"polling echo script MUST exit 0.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(marker,
            customMessage: $"polling echo output MUST be captured via the polling RPC path. " +
                          $"If absent: agent's polling client failed to dial in OR Halibut polling listener " +
                          $"didn't queue the script for the agent to pick up. " +
                          $"\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D9.u1-Linux — Polling: non-zero exit propagates exactly
    // ========================================================================

    [Fact]
    public async Task LD9u1_Polling_NonZeroExit_PropagatesExactExitCode()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartPollingAsync(server.PollingUri, server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const int expectedExit = 99;
        var result = await DispatchAndObservePollingAsync(server, agent.SubscriptionId, agent.Thumbprint,
            $"exit {expectedExit}", ScriptType.Bash);

        result.ExitCode.ShouldBe(expectedExit,
            customMessage: $"polling exit code {expectedExit} MUST propagate exactly. Got {result.ExitCode}. " +
                          $"\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D10.h-Linux — Concurrent listening dispatches: outputs isolated by ticket
    //
    // Operator scenario: server triggers 3 simultaneous deploys to the
    // same agent (different steps in a release pipeline that fan out).
    // Each dispatch must:
    //   (a) Run independently without interfering
    //   (b) Return its OWN output, not bleed into other tickets' results
    //
    // Without this pin, a regression in StartScriptCommand isolation
    // could let two tickets share the same workdir / stdout reader,
    // surfacing each other's output.
    //
    // Uses `sleep 2; echo` so all three scripts overlap for ~2s — REAL
    // concurrent execution, not staggered. 100ms inter-dispatch stagger
    // avoids the first 100ms of bash spawn-thread contention.
    // ========================================================================

    [Fact]
    public async Task LD10h_Listening_ConcurrentDispatches_OutputsIsolatedByTicket()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string scriptA = "sleep 2; echo 'ticket-A-marker'";
        const string scriptB = "sleep 2; echo 'ticket-B-marker'";
        const string scriptC = "sleep 2; echo 'ticket-C-marker'";

        var taskA = DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint, scriptA, ScriptType.Bash,
            observeTimeout: TimeSpan.FromSeconds(20));
        await Task.Delay(100);
        var taskB = DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint, scriptB, ScriptType.Bash,
            observeTimeout: TimeSpan.FromSeconds(20));
        await Task.Delay(100);
        var taskC = DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint, scriptC, ScriptType.Bash,
            observeTimeout: TimeSpan.FromSeconds(20));

        var resultA = await taskA;
        var resultB = await taskB;
        var resultC = await taskC;

        resultA.ExitCode.ShouldBe(0, $"ticket A MUST exit 0.\nLogs:\n{resultA.AllText}");
        resultB.ExitCode.ShouldBe(0, $"ticket B MUST exit 0.\nLogs:\n{resultB.AllText}");
        resultC.ExitCode.ShouldBe(0, $"ticket C MUST exit 0.\nLogs:\n{resultC.AllText}");

        // Output isolation: each result contains ONLY its own marker.
        resultA.AllText.ShouldContain("ticket-A-marker",
            customMessage: $"ticket A MUST contain its marker.\nLogs:\n{resultA.AllText}");
        resultA.AllText.ShouldNotContain("ticket-B-marker",
            customMessage: $"ticket A leaked B's output — concurrent isolation regression.\nLogs:\n{resultA.AllText}");
        resultA.AllText.ShouldNotContain("ticket-C-marker",
            customMessage: $"ticket A leaked C's output — concurrent isolation regression.\nLogs:\n{resultA.AllText}");

        resultB.AllText.ShouldContain("ticket-B-marker",
            customMessage: $"ticket B MUST contain its marker.\nLogs:\n{resultB.AllText}");
        resultB.AllText.ShouldNotContain("ticket-A-marker",
            customMessage: $"ticket B leaked A's output.\nLogs:\n{resultB.AllText}");
        resultB.AllText.ShouldNotContain("ticket-C-marker",
            customMessage: $"ticket B leaked C's output.\nLogs:\n{resultB.AllText}");

        resultC.AllText.ShouldContain("ticket-C-marker",
            customMessage: $"ticket C MUST contain its marker.\nLogs:\n{resultC.AllText}");
        resultC.AllText.ShouldNotContain("ticket-A-marker",
            customMessage: $"ticket C leaked A's output.\nLogs:\n{resultC.AllText}");
        resultC.AllText.ShouldNotContain("ticket-B-marker",
            customMessage: $"ticket C leaked B's output.\nLogs:\n{resultC.AllText}");
    }

    // ========================================================================
    // L-D11.h-Linux — File transfer: server-attached file readable from script's workdir
    //
    // Operator scenario: deployment step has resources (config templates,
    // package files, secrets) that must be transferred to the agent
    // BEFORE script execution. Production: server attaches files to
    // StartScriptCommand; agent's LocalScriptService writes them to the
    // script's working directory (via WriteAdditionalFiles); the script
    // can reference them by bare filename.
    //
    // This test pins:
    //   - Server-attached ScriptFile reaches the agent intact
    //   - Agent writes it to the script's workdir before bash runs
    //   - Bash can `cat` it by bare filename and the content matches
    //
    // Mirror of Windows <c>Listening_SingleFileTransfer_AgentWritesAndScriptReads</c>.
    // ========================================================================

    [Fact]
    public async Task LD11h_Listening_FileTransfer_AgentWritesAndScriptReads()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string fileName = "linux-input-marker.txt";
        const string fileContent = "marker-LINUX-DEPLOY-FILE-XFER-from-server-to-agent";

        // Build StartScriptCommand WITH a file attachment. Bash script
        // simply cats the file — proves the agent persisted it under
        // the script's workdir before bash ran.
        var ticket = new ScriptTicket($"e2e-linux-files-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            $"cat '{fileName}'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(1),
            null,
            Array.Empty<string>(),
            ticket.TaskId,
            TimeSpan.Zero,
            new ScriptFile(fileName, Halibut.DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes(fileContent))))
        {
            ScriptSyntax = ScriptType.Bash
        };

        var result = await server.DispatchAndObserveListeningAsync(agent.ListeningUri, agent.Thumbprint, command,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        result.ExitCode.ShouldBe(0,
            customMessage: $"file-read script MUST exit 0 — proves file was present + readable in workdir.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(fileContent,
            customMessage: $"echoed file content MUST match what server sent. " +
                          $"If absent: agent's WriteAdditionalFiles regressed (didn't write to workdir, OR " +
                          $"wrote with wrong filename, OR encoded bytes differently). " +
                          $"\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D12.h-Linux — Sensitive output variable flagged correctly
    //
    // Operator scenario: deployment script emits a credential that
    // MUST NOT appear in plain logs. Production uses
    // <c>##squid[setVariable name='X' value='Y' sensitive='True']</c>
    // — server-side parser sees sensitive=True and routes the value
    // through encrypted storage + redaction in subsequent logs.
    //
    // This test pins the agent-side surface: the service-message line
    // (including the `sensitive='True'` flag) round-trips through
    // bash → ProcessOutput → Halibut → server logs intact. Server-
    // side parsing + redaction is covered separately at integration
    // tier; THIS test ensures the dispatch path doesn't strip / mangle
    // the sensitive flag.
    //
    // Mirror of Windows
    // <c>Listening_SensitiveOutputVariable_FlaggedByProductionParser</c>.
    // ========================================================================

    [Fact]
    public async Task LD12h_Listening_SensitiveOutputVariable_FlagRoundTripsExactly()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string varName = "MyApiKey";
        const string varValue = "secret-value-that-must-not-leak";

        // sleep 1 before emit for timing resilience — same as LD7h.
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            $"sleep 1; echo \"##squid[setVariable name='{varName}' value='{varValue}' sensitive='True']\"",
            ScriptType.Bash);

        result.ExitCode.ShouldBe(0,
            customMessage: $"sensitive output-variable script MUST exit 0.\nLogs:\n{result.AllText}");

        // Service message must round-trip with sensitive flag intact.
        // Server-side parsing (in DeploymentTaskExecutor.CaptureOutputVariables)
        // checks for sensitive='True' to route the value through the
        // encrypted-variables sink. If the flag gets stripped here,
        // operators leak credentials in subsequent step logs.
        result.AllText.ShouldContain("sensitive='True'",
            customMessage: $"sensitive='True' flag MUST round-trip through bash → Halibut → server logs intact. " +
                          "If absent: the flag was stripped/mangled by the dispatch path — server-side parser " +
                          "would treat it as non-sensitive and the credential leaks into plain logs of subsequent " +
                          $"deployment steps.\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(varName,
            customMessage: $"variable name '{varName}' MUST surface in logs.\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D13.h-Linux — Multi-file transfer: all files available to script
    //
    // Operator scenario: deployment step has multiple resource files
    // (config + secrets + data files). Server attaches them all to
    // StartScriptCommand; agent must persist EACH in the script's workdir.
    //
    // Pins:
    //   - 3 ScriptFile attachments all reach the agent
    //   - Each writes to the workdir under its declared name
    //   - Bash can read all 3 by bare filename
    //
    // Mirror of Windows
    // <c>Listening_MultipleFileTransfer_AllFilesAvailableToScript</c>.
    // ========================================================================

    [Fact]
    public async Task LD13h_Listening_MultipleFileTransfer_AllFilesAvailableToScript()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string fileA = "config-a.txt";
        const string fileB = "secrets-b.txt";
        const string fileC = "data-c.txt";
        const string contentA = "content-A-config-marker-zzz";
        const string contentB = "content-B-secrets-marker-yyy";
        const string contentC = "content-C-data-marker-xxx";

        // Bash script reads each file + adds a per-file separator so
        // even if the file contents are identical we can detect a
        // missing one.
        var ticket = new ScriptTicket($"e2e-linux-multifiles-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            $"cat '{fileA}'; echo; cat '{fileB}'; echo; cat '{fileC}'; echo",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(1),
            null,
            Array.Empty<string>(),
            ticket.TaskId,
            TimeSpan.Zero,
            new ScriptFile(fileA, Halibut.DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes(contentA))),
            new ScriptFile(fileB, Halibut.DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes(contentB))),
            new ScriptFile(fileC, Halibut.DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes(contentC))))
        {
            ScriptSyntax = ScriptType.Bash
        };

        var result = await server.DispatchAndObserveListeningAsync(agent.ListeningUri, agent.Thumbprint, command,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        result.ExitCode.ShouldBe(0,
            customMessage: $"multi-file-read script MUST exit 0 — proves all 3 files were present + readable.\nLogs:\n{result.AllText}");

        // ALL three contents must be present. Catches a regression
        // where WriteAdditionalFiles silently drops files 2+ when
        // multiple are passed.
        result.AllText.ShouldContain(contentA,
            customMessage: $"file A content '{contentA}' MUST be present.\nLogs:\n{result.AllText}");
        result.AllText.ShouldContain(contentB,
            customMessage: $"file B content '{contentB}' MUST be present. " +
                          "If absent but A is: agent dropped subsequent files in the multi-file path. " +
                          $"\nLogs:\n{result.AllText}");
        result.AllText.ShouldContain(contentC,
            customMessage: $"file C content '{contentC}' MUST be present.\nLogs:\n{result.AllText}");
    }

    // ========================================================================
    // L-D14.h-Linux — Long output (500 lines) all captured (no truncation)
    //
    // Operator scenario: deployment scripts that produce verbose output
    // (e.g. `kubectl describe pod` or large log dumps) MUST have all
    // output captured — operators rely on full logs to debug failures.
    //
    // Pins:
    //   - 500 distinct line emits all reach the server's log stream
    //   - No mid-stream truncation by Halibut serialization
    //   - No final-line drop by the observe loop
    //
    // Without this pin, a regression in the agent's stdout streaming
    // batch size OR Halibut's log message length cap would silently
    // truncate output for verbose scripts — operators see "task succeeded"
    // but their diagnostic logs are incomplete.
    // ========================================================================

    [Fact]
    public async Task LD14h_Listening_LongOutput_500Lines_AllCaptured()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await SquidHalibutStubServer.StartAsync();
        await using var agent = await SquidHalibutStubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Bash for loop emitting 500 lines with line numbers. Specific
        // markers at line 1 / 250 / 500 — verifies start, middle, end
        // all captured (a partial-truncation regression usually drops
        // either start or end depending on which buffer wraps).
        const int expectedLineCount = 500;
        const string scriptBody = "for i in $(seq 1 500); do echo \"line-$i-marker\"; done";
        var result = await DispatchAndObserveListeningAsync(server, agent.ListeningUri, agent.Thumbprint,
            scriptBody, ScriptType.Bash,
            observeTimeout: TimeSpan.FromSeconds(30));

        result.ExitCode.ShouldBe(0,
            customMessage: $"long-output script MUST exit 0.\nLogs (truncated):\n{result.AllText.Substring(0, Math.Min(result.AllText.Length, 500))}...");

        // Markers at start, middle, end — proves NO truncation on any
        // of the 3 likely loss points.
        result.AllText.ShouldContain("line-1-marker",
            customMessage: $"first line missing — start-of-stream truncation regression.");
        result.AllText.ShouldContain("line-250-marker",
            customMessage: $"middle line missing — mid-stream batch / serialization truncation regression.");
        result.AllText.ShouldContain("line-500-marker",
            customMessage: $"last line missing — end-of-stream / observe-loop final-flush regression.");

        // Pin total line count via regex match — actual capture must
        // contain ALL 500 marker lines, not a sample.
        var markerCount = System.Text.RegularExpressions.Regex.Matches(result.AllText, @"\bline-\d+-marker\b").Count;
        markerCount.ShouldBe(expectedLineCount,
            customMessage: $"expected exactly {expectedLineCount} 'line-N-marker' instances in output. Got {markerCount}. " +
                          $"If less: streaming truncation regression. If more: log-line duplication regression " +
                          "(Halibut RPC poll re-emitted previously-seen lines).");
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

    /// <summary>
    /// Polling-mode dispatch helper. Mirror of <see cref="DispatchAndObserveListeningAsync"/>
    /// but routes via the polling-agent path: stub server queues the command
    /// for the agent's subscription ID; the agent (already polling against
    /// the stub's <see cref="SquidHalibutStubServer.PollingUri"/>) picks it up.
    /// </summary>
    private static async Task<ObservedScriptResult> DispatchAndObservePollingAsync(
        SquidHalibutStubServer server,
        string agentSubscriptionId,
        string agentThumbprint,
        string scriptBody,
        ScriptType scriptType,
        TimeSpan? observeTimeout = null)
    {
        var ticket = new ScriptTicket($"e2e-linux-poll-{Guid.NewGuid():N}");
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
        return await server.DispatchAndObservePollingAsync(agentSubscriptionId, agentThumbprint, command, timeout, CancellationToken.None);
    }
}
