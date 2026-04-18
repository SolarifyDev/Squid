using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
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
}
