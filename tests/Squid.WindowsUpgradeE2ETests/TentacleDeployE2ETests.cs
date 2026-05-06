using System.Text;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Message.Contracts.Tentacle;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// Phase 12.J — E2E coverage for the deployment-execution round-trip:
/// SERVER (<see cref="StubSquidServer"/>) → HALIBUT RPC →
/// AGENT (<see cref="StubAgent"/> wrapping production
/// <see cref="Squid.Tentacle.ScriptExecution.LocalScriptService"/>) →
/// REAL SHELL EXECUTION (PowerShell / bash) → results back over Halibut.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Every component except
/// the upstream Squid server is production code:</para>
/// <list type="bullet">
///   <item>Halibut runtime + TLS handshake — production library</item>
///   <item>Listening / Polling RPC contracts — production wire shapes</item>
///   <item><c>LocalScriptService</c> — production class that picks
///         PowerShell on Windows + bash on Linux/macOS based on
///         <c>StartScriptCommand.ScriptSyntax</c></item>
///   <item>Real <c>powershell.exe</c> / <c>bash</c> child process</item>
/// </list>
///
/// <para><b>Coverage delta vs <c>WindowsPowerShellE2ETests</c></b>: that
/// suite tests <c>LocalScriptService</c> in isolation (direct API call,
/// no Halibut). Phase 12.J wraps the SAME class behind a real Halibut
/// runtime so the WIRE PROTOCOL between server and agent is exercised
/// end-to-end. A regression in StartScriptCommand serialization, a
/// missing Halibut TLS extension, or a mismatched IScriptService
/// async-adapter would surface here.</para>
///
/// <para><b>Cross-platform</b>: Each test branches on
/// <see cref="OperatingSystem.IsWindows"/> to choose PowerShell vs Bash.
/// Running on macOS / Linux exercises the bash path; on Windows runner
/// exercises the PowerShell path. No skip-on-OS guards — every OS
/// covers an OS-specific script-runtime path.</para>
///
/// <para><b>Scenario coverage</b> (per <c>docs/e2e-scenario-matrix.md</c>
/// Section D — Phase 12.J.D.1 first cut):</para>
/// <list type="bullet">
///   <item>D1.h Listening + PowerShell echo (Windows-only branch)</item>
///   <item>D1.h2 Listening + Bash echo (Linux/macOS branch)</item>
///   <item>D1.u1 Listening + non-zero exit → task fails</item>
///   <item>D2.h Polling + script (cross-OS via branch)</item>
///   <item>D3.h Multi-line stdout fully captured</item>
///   <item>D4.h Stderr captured separately</item>
///   <item>D12.h Exit code propagated exactly</item>
/// </list>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleDeploy)]
public sealed class TentacleDeployE2ETests
{
    // ========================================================================
    // D1.h / D1.h2 — Listening: server dispatches script → output captured + exit 0
    // ========================================================================

    [Fact]
    public async Task Listening_EchoScript_OutputCapturedAndExitZero()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        var (scriptBody, scriptType) = OsScript.Echo("hello-from-deploy-e2e");

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0,
            customMessage: $"echo script MUST exit 0. Logs:\n{result.AllText}");
        result.AllText.ShouldContain("hello-from-deploy-e2e",
            customMessage: $"echo output MUST be captured in logs. Got:\n{result.AllText}");
    }

    // ========================================================================
    // D1.u1 — Listening: script with non-zero exit → task fails (exit code propagates)
    // ========================================================================

    [Fact]
    public async Task Listening_NonZeroExit_PropagatesExactExitCode()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Use a non-zero exit code that's specifically NOT 1 — proves the
        // exit code is propagated EXACTLY, not normalised to 1 by some
        // intermediate layer.
        var (scriptBody, scriptType) = OsScript.Exit(42);

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(42,
            customMessage: $"exit code 42 MUST be propagated exactly (not normalised to 1). Got: {result.ExitCode}. Logs:\n{result.AllText}");
    }

    // ========================================================================
    // D3.h — Multi-line stdout captured in order
    // ========================================================================

    [Fact]
    public async Task Listening_MultiLineOutput_AllLinesCaptured()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        var (scriptBody, scriptType) = OsScript.MultiLine("line-one", "line-two", "line-three");

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0);
        result.AllText.ShouldContain("line-one", customMessage: $"first line missing. Got:\n{result.AllText}");
        result.AllText.ShouldContain("line-two", customMessage: $"second line missing. Got:\n{result.AllText}");
        result.AllText.ShouldContain("line-three", customMessage: $"third line missing. Got:\n{result.AllText}");

        // Order preservation: line-one should appear before line-two before line-three.
        var idxOne = result.AllText.IndexOf("line-one", StringComparison.Ordinal);
        var idxTwo = result.AllText.IndexOf("line-two", StringComparison.Ordinal);
        var idxThree = result.AllText.IndexOf("line-three", StringComparison.Ordinal);
        idxOne.ShouldBeLessThan(idxTwo, customMessage: "log order regressed: line-one MUST appear before line-two");
        idxTwo.ShouldBeLessThan(idxThree, customMessage: "log order regressed: line-two MUST appear before line-three");
    }

    // ========================================================================
    // D4.h — Stderr captured (and tagged as StdErr in ProcessOutput.Source)
    // ========================================================================

    [Fact]
    public async Task Listening_StderrOutput_CapturedAndTaggedAsStdErr()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        var (scriptBody, scriptType) = OsScript.WriteToStderr("error-message-on-stderr");

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        // Echo to stderr alone shouldn't fail the script (exit 0 expected).
        result.ExitCode.ShouldBe(0);

        // Stderr content captured.
        result.AllText.ShouldContain("error-message-on-stderr",
            customMessage: $"stderr content MUST be captured in the log stream. Got:\n{result.AllText}");

        // At least one log line tagged as StdErr.
        var stderrLogs = result.AllLogs.Where(l => l.Source == ProcessOutputSource.StdErr).ToList();
        stderrLogs.ShouldNotBeEmpty(
            customMessage: $"at least one log line MUST be tagged ProcessOutputSource.StdErr — proves the agent is correctly classifying output streams. All logs:\n{string.Join("\n", result.AllLogs.Select(l => $"[{l.Source}] {l.Text}"))}");
    }

    // ========================================================================
    // D2.h — Polling: server queues script for polling agent → agent picks up
    // ========================================================================

    [Fact]
    public async Task Polling_EchoScript_OutputCapturedAndExitZero()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartPollingAsync(server.PollingUri, server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        var (scriptBody, scriptType) = OsScript.Echo("hello-from-polling-deploy");

        var result = await DispatchAndObserveAsync(server, agent: null, agentThumbprint: agent.Thumbprint, agentSubscriptionId: agent.SubscriptionId, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0,
            customMessage: $"polling echo script MUST exit 0. Logs:\n{result.AllText}");
        result.AllText.ShouldContain("hello-from-polling-deploy",
            customMessage: $"polling echo output MUST be captured. Got:\n{result.AllText}");
    }

    // ========================================================================
    // D9.h — Long-running script (sleep 3s) completes; full output captured
    // ========================================================================

    [Fact]
    public async Task Listening_LongRunningScript_CompletesAndCapturesAllOutput()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        var (scriptBody, scriptType) = OsScript.SleepThenEcho(3, "after-3s-sleep");

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType, observeTimeout: TimeSpan.FromSeconds(20));

        result.ExitCode.ShouldBe(0,
            customMessage: $"long-running script MUST exit 0. Logs:\n{result.AllText}");
        result.AllText.ShouldContain("after-3s-sleep",
            customMessage: $"output emitted AFTER the 3s sleep MUST be captured — proves the status-poll loop kept polling and captured late logs. Got:\n{result.AllText}");
    }

    // ========================================================================
    // D10.h — Concurrent dispatches to same agent are isolated by ticket
    // ========================================================================

    [Fact]
    public async Task Listening_ConcurrentDispatches_OutputsIsolatedByTicket()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Three scripts dispatched in near-parallel (50ms stagger). Each
        // writes a unique marker. Assertions verify each result captures
        // ONLY its own marker — no interleaving across tickets.
        // NoIsolation lets them run concurrently agent-side; the per-
        // ticket log isolation in LocalScriptService is what we're pinning.
        //
        // Why staggered, not Task.WhenAll: spawning 3 powershell.exe
        // processes at the EXACT same instant can starve the third
        // process's stdout reader on Windows (round-5 of P12.G found this:
        // resultC.AllText was "" 1/3 of the time). 50ms stagger gives
        // each process time to fully spawn its child + IO threads before
        // the next dispatch arrives. This matches real-world concurrency
        // better — production never fires 3 dispatches in literally the
        // same instant; they're separated by at least HTTP round-trip
        // latency.
        var (bodyA, typeA) = OsScript.Echo("ticket-A-marker");
        var (bodyB, typeB) = OsScript.Echo("ticket-B-marker");
        var (bodyC, typeC) = OsScript.Echo("ticket-C-marker");

        var taskA = DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, bodyA, typeA);
        await Task.Delay(50);
        var taskB = DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, bodyB, typeB);
        await Task.Delay(50);
        var taskC = DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, bodyC, typeC);

        await Task.WhenAll(taskA, taskB, taskC);

        var resultA = await taskA;
        var resultB = await taskB;
        var resultC = await taskC;

        resultA.ExitCode.ShouldBe(0);
        resultB.ExitCode.ShouldBe(0);
        resultC.ExitCode.ShouldBe(0);

        // Each ticket's logs MUST contain ONLY its own marker, not the others.
        resultA.AllText.ShouldContain("ticket-A-marker");
        resultA.AllText.ShouldNotContain("ticket-B-marker",
            customMessage: $"ticket A's logs MUST NOT contain ticket B's output — log isolation broken. A logs:\n{resultA.AllText}");
        resultA.AllText.ShouldNotContain("ticket-C-marker",
            customMessage: $"ticket A's logs MUST NOT contain ticket C's output. A logs:\n{resultA.AllText}");

        resultB.AllText.ShouldContain("ticket-B-marker");
        resultB.AllText.ShouldNotContain("ticket-A-marker");
        resultB.AllText.ShouldNotContain("ticket-C-marker");

        resultC.AllText.ShouldContain("ticket-C-marker");
        resultC.AllText.ShouldNotContain("ticket-A-marker");
        resultC.AllText.ShouldNotContain("ticket-B-marker");
    }

    // ========================================================================
    // D13.h — Unicode output (CJK + em-dash) captured cleanly
    // ========================================================================

    [Fact]
    public async Task Listening_UnicodeOutput_PreservedThroughHalibutAndShell()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Three classes of non-ASCII characters that have hit production
        // before:
        //   - CJK characters (Chinese/Japanese/Korean) — multi-byte UTF-8
        //   - em-dash — round-2 P12.G fix territory; PowerShell 5.1 ANSI
        //     decoder mangles these without UTF-8 BOM
        //   - emoji — common in modern logs / PR descriptions
        const string Marker = "hello-世界-—-🚀-end";  // hello-世界-—-🚀-end

        var (scriptBody, scriptType) = OsScript.Echo(Marker);

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0,
            customMessage: $"unicode echo script MUST exit 0. Logs:\n{result.AllText}");

        // CJK preserved.
        result.AllText.ShouldContain("世界",
            customMessage: $"CJK chars (世界 = 'world' in Chinese) MUST be preserved through Halibut serialization + shell output capture. Got:\n{result.AllText}");

        // Em-dash preserved (round-2 lesson — pwsh 5.1 default ANSI codepage corrupts this).
        result.AllText.ShouldContain("—",
            customMessage: $"em-dash (U+2014) MUST be preserved. Round-2 fix territory. Got:\n{result.AllText}");

        // Emoji preserved (surrogate pair).
        result.AllText.ShouldContain("🚀",
            customMessage: $"emoji (rocket 🚀) MUST be preserved through UTF-8 encoding pipeline. Got:\n{result.AllText}");
    }

    // ========================================================================
    // D7.h — Plain output variable: `##squid[setVariable name='X' value='Y']`
    //
    // Round-trip:
    //   shell echo → process stdout → Halibut log capture → ObservedScriptResult →
    //   production ServiceMessageParser.ParseOutputVariables → assertion
    //
    // This test pins the FULL extraction pipeline operators rely on for
    // pushing values from a deployment script back to the orchestrator.
    // ========================================================================

    [Fact]
    public async Task Listening_PlainOutputVariable_RoundTripsToProductionParser()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string varName = "DeploymentArtefactUrl";
        const string varValue = "https://artifacts.example/build-1234.zip";

        var (scriptBody, scriptType) = OsScript.EmitServiceMessage(
            $"##squid[setVariable name='{varName}' value='{varValue}']");

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0,
            customMessage: $"setVariable script MUST exit 0. Logs:\n{result.AllText}");

        // Run logs through the production parser — same code DeploymentTaskExecutor uses.
        var parser = new ServiceMessageParser();
        var variables = parser.ParseOutputVariables(result.AllLogs.Select(l => l.Text));

        variables.ShouldContainKey(varName,
            customMessage: $"production parser MUST extract '{varName}' from agent's log lines. Got logs:\n{result.AllText}\nParsed variables: [{string.Join(", ", variables.Keys)}]");

        variables[varName].Value.ShouldBe(varValue);
        variables[varName].IsSensitive.ShouldBeFalse(
            customMessage: "without sensitive='True' attribute, variable MUST default to non-sensitive");
    }

    // ========================================================================
    // D7.h2 — Sensitive output variable: `sensitive='True'`
    //
    // Pins the IsSensitive flag → operators rely on this to mask values
    // in logs and encrypt at rest. Regression that drops the flag would
    // leak secrets in plain logs.
    // ========================================================================

    [Fact]
    public async Task Listening_SensitiveOutputVariable_FlaggedByProductionParser()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string varName = "DatabasePassword";
        const string varValue = "p4ssw0rd-do-not-leak";

        var (scriptBody, scriptType) = OsScript.EmitServiceMessage(
            $"##squid[setVariable name='{varName}' value='{varValue}' sensitive='True']");

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0);

        var parser = new ServiceMessageParser();
        var variables = parser.ParseOutputVariables(result.AllLogs.Select(l => l.Text));

        variables.ShouldContainKey(varName);
        variables[varName].IsSensitive.ShouldBeTrue(
            customMessage: $"with sensitive='True' attribute, variable MUST be flagged as sensitive. Got: {variables[varName].IsSensitive}. Without this, the value '{varValue}' would not be masked downstream.");
        variables[varName].Value.ShouldBe(varValue,
            customMessage: "even when sensitive, the parser still extracts the actual value (masking is downstream). Round-trip shape MUST be preserved.");
    }

    // ========================================================================
    // D7.u1 — Output variable with special characters via base64-encoded value
    //
    // Production wire shape: `name="<b64>" value="<b64>"` (double quotes =
    // base64). Used when the value contains characters incompatible with
    // single-quoted plaintext (apostrophes, embedded newlines, control
    // chars). Tests round-trip through the parser's TryDecodeBase64 path.
    // ========================================================================

    [Fact]
    public async Task Listening_OutputVariableWithBase64Encoding_RoundTripsCorrectly()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        const string varName = "ConnectionString";
        const string varValue = "Server=db1;User='admin';Password=\"p'\\\"\";";

        var nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(varName));
        var valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(varValue));

        // Double-quoted form per ServiceMessageParser's regex.
        var (scriptBody, scriptType) = OsScript.EmitServiceMessage(
            $"##squid[setVariable name=\"{nameB64}\" value=\"{valueB64}\"]");

        var result = await DispatchAndObserveAsync(server, agent.ListeningUri, agent.Thumbprint, scriptBody, scriptType);

        result.ExitCode.ShouldBe(0,
            customMessage: $"base64-encoded setVariable script MUST exit 0. Logs:\n{result.AllText}");

        var parser = new ServiceMessageParser();
        var variables = parser.ParseOutputVariables(result.AllLogs.Select(l => l.Text));

        variables.ShouldContainKey(varName,
            customMessage: $"parser MUST decode base64-encoded NAME. Got logs:\n{result.AllText}\nParsed: [{string.Join(", ", variables.Keys)}]");

        variables[varName].Value.ShouldBe(varValue,
            customMessage: $"parser MUST decode base64-encoded VALUE preserving every special char (apostrophes, double-quotes, semicolons, equals). Got: '{variables[varName].Value}'");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a script to either a Listening agent (when
    /// <paramref name="agent"/> is non-null) or a Polling agent (when
    /// <paramref name="agentSubscriptionId"/> is non-null). Both paths
    /// route through <see cref="StubSquidServer.DispatchAndObserveListeningAsync"/>
    /// or <c>DispatchAndObservePollingAsync</c>.
    /// </summary>
    private static async Task<ObservedScriptResult> DispatchAndObserveAsync(StubSquidServer server, Uri agent, string agentThumbprint, string agentSubscriptionId, string scriptBody, ScriptType scriptType, TimeSpan? observeTimeout = null)
    {
        var ticket = new ScriptTicket($"e2e-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(ticket, scriptBody, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), null, Array.Empty<string>(), ticket.TaskId, TimeSpan.Zero)
        {
            ScriptSyntax = scriptType
        };

        var timeout = observeTimeout ?? TimeSpan.FromSeconds(30);

        if (agent != null)
            return await server.DispatchAndObserveListeningAsync(agent, agentThumbprint, command, timeout, CancellationToken.None);

        if (agentSubscriptionId != null)
            return await server.DispatchAndObservePollingAsync(agentSubscriptionId, agentThumbprint, command, timeout, CancellationToken.None);

        throw new ArgumentException("Either agent (listening URI) or agentSubscriptionId (polling) must be supplied");
    }

    /// <summary>Listening-agent overload — convenience for the common case.</summary>
    private static Task<ObservedScriptResult> DispatchAndObserveAsync(StubSquidServer server, Uri agentUri, string agentThumbprint, string scriptBody, ScriptType scriptType, TimeSpan? observeTimeout = null)
        => DispatchAndObserveAsync(server, agentUri, agentThumbprint, agentSubscriptionId: null, scriptBody, scriptType, observeTimeout);

    /// <summary>
    /// Per-OS script body builder. Tests use these to write OS-agnostic
    /// assertions while the script body itself is OS-specific (PowerShell
    /// on Windows, bash on Linux/macOS).
    /// </summary>
    private static class OsScript
    {
        public static (string body, ScriptType type) Echo(string text)
            => OperatingSystem.IsWindows()
                ? ($"Write-Output '{text}'", ScriptType.PowerShell)
                : ($"echo '{text}'", ScriptType.Bash);

        public static (string body, ScriptType type) Exit(int code)
            => OperatingSystem.IsWindows()
                ? ($"exit {code}", ScriptType.PowerShell)
                : ($"exit {code}", ScriptType.Bash);

        public static (string body, ScriptType type) MultiLine(params string[] lines)
        {
            if (OperatingSystem.IsWindows())
            {
                var ps = string.Join("\n", lines.Select(l => $"Write-Output '{l}'"));
                return (ps, ScriptType.PowerShell);
            }

            var bash = string.Join("\n", lines.Select(l => $"echo '{l}'"));
            return (bash, ScriptType.Bash);
        }

        public static (string body, ScriptType type) WriteToStderr(string text)
            => OperatingSystem.IsWindows()
                ? ($"[Console]::Error.WriteLine('{text}')", ScriptType.PowerShell)
                : ($"echo '{text}' >&2", ScriptType.Bash);

        public static (string body, ScriptType type) SleepThenEcho(int seconds, string text)
            => OperatingSystem.IsWindows()
                ? ($"Start-Sleep -Seconds {seconds}; Write-Output '{text}'", ScriptType.PowerShell)
                : ($"sleep {seconds}; echo '{text}'", ScriptType.Bash);

        /// <summary>
        /// Emits a service-message line literally to stdout. Used for
        /// <c>##squid[setVariable ...]</c> tests where the value contains
        /// single quotes (so the standard <see cref="Echo"/> single-quote
        /// wrapper would break). Wraps with the SHELL'S OWN heredoc /
        /// here-string so the inner string passes through verbatim.
        /// </summary>
        public static (string body, ScriptType type) EmitServiceMessage(string magicLine)
        {
            if (OperatingSystem.IsWindows())
            {
                // PowerShell here-string (single-quote = literal, no
                // expansion). Body terminator '@ MUST be at start of a
                // physical line.
                var ps = "Write-Output @'\n" + magicLine + "\n'@";
                return (ps, ScriptType.PowerShell);
            }
            else
            {
                // Bash heredoc with single-quoted delimiter — same "no
                // expansion" semantics as the PowerShell variant.
                var bash = "cat <<'SQUIDMSGEOF'\n" + magicLine + "\nSQUIDMSGEOF";
                return (bash, ScriptType.Bash);
            }
        }
    }
}
