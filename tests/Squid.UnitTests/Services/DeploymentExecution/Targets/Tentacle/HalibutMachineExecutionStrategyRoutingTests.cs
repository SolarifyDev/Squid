using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class HalibutMachineExecutionStrategyRoutingTests
{
    [Fact]
    public void ParseMachineEndpoint_TentacleListening_ReturnsHttpsEndpoint()
    {
        var machine = new Machine
        {
            Name = "tentacle-web-01",
            Endpoint = """{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933/","Thumbprint":"AABBCCDD"}"""
        };

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.Scheme.ShouldBe("https");
        endpoint.BaseUri.Host.ShouldBe("10.0.0.5");
        endpoint.BaseUri.Port.ShouldBe(10933);
    }

    [Fact]
    public void ParseMachineEndpoint_TentaclePolling_ReturnsPollEndpoint()
    {
        var machine = new Machine
        {
            Name = "tentacle-web-02",
            Endpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"tentacle-abc","Thumbprint":"EEFF0011"}"""
        };

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.Scheme.ShouldBe("poll");
        endpoint.BaseUri.ToString().ShouldBe("poll://tentacle-abc/");
    }

    [Fact]
    public void ParseMachineEndpoint_KubernetesAgent_ReturnsPollEndpoint_BackwardCompatible()
    {
        var machine = new Machine
        {
            Name = "k8s-agent-01",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"k8s-sub-01","Thumbprint":"112233"}"""
        };

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.Scheme.ShouldBe("poll");
        endpoint.BaseUri.ToString().ShouldBe("poll://k8s-sub-01/");
    }

    [Fact]
    public void ParseMachineEndpoint_NoCommunicationStyle_FallsThroughToPolling()
    {
        var machine = new Machine
        {
            Name = "legacy",
            Endpoint = """{"SubscriptionId":"legacy-sub","Thumbprint":"AABB"}"""
        };

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("poll://legacy-sub/");
    }

    [Fact]
    public void ParseMachineEndpoint_InvalidJson_ReturnsNull()
    {
        var machine = new Machine
        {
            Name = "broken",
            Endpoint = "not-json"
        };

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        endpoint.ShouldBeNull();
    }

    [Fact]
    public void ParseMachineEndpoint_EmptyEndpoint_ReturnsNull()
    {
        var machine = new Machine
        {
            Name = "empty",
            Endpoint = ""
        };

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        endpoint.ShouldBeNull();
    }

    // ========================================================================
    // MapSyntax — wire-protocol mapping must cover every server-side syntax,
    // not silently downgrade to Bash (root cause of the 2026-04-18 incident
    // where Python scripts hit Tentacles as `bash script.sh` and crashed
    // with "import: command not found").
    // ========================================================================

    [Theory]
    [InlineData(Squid.Message.Models.Deployments.Execution.ScriptSyntax.Bash, ScriptType.Bash)]
    [InlineData(Squid.Message.Models.Deployments.Execution.ScriptSyntax.PowerShell, ScriptType.PowerShell)]
    [InlineData(Squid.Message.Models.Deployments.Execution.ScriptSyntax.Python, ScriptType.Python)]
    [InlineData(Squid.Message.Models.Deployments.Execution.ScriptSyntax.CSharp, ScriptType.CSharp)]
    [InlineData(Squid.Message.Models.Deployments.Execution.ScriptSyntax.FSharp, ScriptType.FSharp)]
    public void MapSyntax_KnownSyntaxes_PreserveSyntaxAcrossWire(
        Squid.Message.Models.Deployments.Execution.ScriptSyntax serverSyntax, ScriptType expected)
    {
        HalibutMachineExecutionStrategy.MapSyntax(serverSyntax).ShouldBe(expected);
    }

    [Fact]
    public void MapSyntax_UnknownSyntax_ThrowsRatherThanSilentlyDowngradingToBash()
    {
        // Adding a new ScriptSyntax server-side must force an explicit mapping
        // change here — silent downgrade was the original 2026-04-18 bug.
        Should.Throw<InvalidOperationException>(() =>
            HalibutMachineExecutionStrategy.MapSyntax((Squid.Message.Models.Deployments.Execution.ScriptSyntax)999))
                .Message.ShouldContain("Unsupported script syntax");
    }

    // ── ARCH.7 (Phase-6, post-Phase-5 deep audit) ─────────────────────────────
    //
    // Pre-fix: GenerateTicketId derived BOTH the ScriptTicket AND the
    // IsolationMutexName from `SHA256(taskId|step|action|machineId)[..32]`.
    // Two consecutive dispatches of the same logical action (e.g. retry after
    // a network glitch on the original StartScript RPC) produced IDENTICAL
    // tickets — so agent-side state from the first attempt (mutex, workspace)
    // could trap the retry; the agent couldn't distinguish "redelivery" from
    // "fresh attempt". Octopus matches this by using `Guid.NewGuid()` per
    // dispatch.
    //
    // Post-fix:
    //   - ScriptTicket: Guid.NewGuid() (32-char "N" form) per call → distinct
    //     attempts get distinct tickets → no agent-side state trap.
    //   - IsolationMutexName: stable SHA256 derivation kept → concurrent
    //     dispatches of the same action still serialise on the agent (the
    //     thing the original derivation was actually trying to do).

    [Fact]
    public void GenerateTicketId_TwoCalls_ProduceDifferentTickets()
    {
        // Pin Guid-per-attempt: even SAME (taskId, step, action, machineId)
        // → DIFFERENT tickets across calls.
        var t1 = HalibutMachineExecutionStrategy.GenerateTicketId(123, "Deploy", "Apply", 7);
        var t2 = HalibutMachineExecutionStrategy.GenerateTicketId(123, "Deploy", "Apply", 7);

        t1.ShouldNotBe(t2,
            customMessage:
                "ARCH.7 — same dispatch tuple must produce DIFFERENT tickets across calls. " +
                "SHA256-derived tickets caused agent-side state from a previous attempt to trap retries.");
    }

    [Fact]
    public void GenerateTicketId_ProducesStableLengthAndFormat()
    {
        // Format pin: 32 chars hex/Guid-N — keeps the ticket compact in logs
        // and the workspace dir name (`squid-tentacle-{ticketId}`).
        var ticket = HalibutMachineExecutionStrategy.GenerateTicketId(1, "step", "action", 1);

        ticket.Length.ShouldBe(32);
        ticket.ShouldMatch("^[a-f0-9]{32}$",
            customMessage: "ticket must be lowercase hex (Guid 'N' format) — agent's TicketIdWhitelist regex pins this.");
    }

    [Fact]
    public void GenerateMutexName_StableForSameActionTuple()
    {
        // The IsolationMutexName MUST stay deterministic so two concurrent
        // dispatches of the same logical action (e.g. server-side double-send
        // bug, or genuine retry overlapping with original) serialise on the
        // agent. Different tickets, same mutex name = correct agent behaviour.
        var m1 = HalibutMachineExecutionStrategy.GenerateMutexName(123, "Deploy", "Apply", 7);
        var m2 = HalibutMachineExecutionStrategy.GenerateMutexName(123, "Deploy", "Apply", 7);

        m1.ShouldBe(m2);
        m1.Length.ShouldBe(32);
        m1.ShouldMatch("^[a-f0-9]{32}$");
    }

    [Theory]
    [InlineData(1, "step", "action", 1, 2, "step", "action", 1)]    // taskId differs
    [InlineData(1, "step", "action", 1, 1, "STEP", "action", 1)]    // step differs
    [InlineData(1, "step", "action", 1, 1, "step", "ACTION", 1)]    // action differs
    [InlineData(1, "step", "action", 1, 1, "step", "action", 2)]    // machineId differs
    public void GenerateMutexName_DifferentTuple_DifferentMutex(
        int t1, string s1, string a1, int m1,
        int t2, string s2, string a2, int m2)
    {
        var n1 = HalibutMachineExecutionStrategy.GenerateMutexName(t1, s1, a1, m1);
        var n2 = HalibutMachineExecutionStrategy.GenerateMutexName(t2, s2, a2, m2);

        n1.ShouldNotBe(n2,
            customMessage: "different (taskId, step, action, machineId) tuples must hash to different mutex names so unrelated dispatches don't serialise.");
    }
}
