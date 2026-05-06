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

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a script to either a Listening agent (when
    /// <paramref name="agent"/> is non-null) or a Polling agent (when
    /// <paramref name="agentSubscriptionId"/> is non-null). Both paths
    /// route through <see cref="StubSquidServer.DispatchAndObserveListeningAsync"/>
    /// or <c>DispatchAndObservePollingAsync</c>.
    /// </summary>
    private static async Task<ObservedScriptResult> DispatchAndObserveAsync(StubSquidServer server, Uri agent, string agentThumbprint, string agentSubscriptionId, string scriptBody, ScriptType scriptType)
    {
        var ticket = new ScriptTicket($"e2e-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(ticket, scriptBody, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), null, Array.Empty<string>(), ticket.TaskId, TimeSpan.Zero)
        {
            ScriptSyntax = scriptType
        };

        if (agent != null)
            return await server.DispatchAndObserveListeningAsync(agent, agentThumbprint, command, TimeSpan.FromSeconds(30), CancellationToken.None);

        if (agentSubscriptionId != null)
            return await server.DispatchAndObservePollingAsync(agentSubscriptionId, agentThumbprint, command, TimeSpan.FromSeconds(30), CancellationToken.None);

        throw new ArgumentException("Either agent (listening URI) or agentSubscriptionId (polling) must be supplied");
    }

    /// <summary>Listening-agent overload — convenience for the common case.</summary>
    private static Task<ObservedScriptResult> DispatchAndObserveAsync(StubSquidServer server, Uri agentUri, string agentThumbprint, string scriptBody, ScriptType scriptType)
        => DispatchAndObserveAsync(server, agentUri, agentThumbprint, agentSubscriptionId: null, scriptBody, scriptType);

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
    }
}
