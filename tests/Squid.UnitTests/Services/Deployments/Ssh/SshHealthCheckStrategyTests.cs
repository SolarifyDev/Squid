using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshHealthCheckStrategyTests
{
    private readonly Mock<IEndpointContextBuilder> _endpointContextBuilder = new();
    private readonly Mock<ISshConnectionFactory> _connectionFactory = new();

    private SshHealthCheckStrategy CreateStrategy() => new(_endpointContextBuilder.Object, _connectionFactory.Object);

    private static Machine MakeSshMachine(string host = "ssh.example.com", int port = 22, string fingerprint = "abc123", string remoteWorkDir = null)
    {
        var endpoint = new SshEndpointDto
        {
            CommunicationStyle = "Ssh",
            Host = host,
            Port = port,
            Fingerprint = fingerprint,
            RemoteWorkingDirectory = remoteWorkDir,
            ResourceReferences = new List<EndpointResourceReference>()
        };

        return new Machine
        {
            Id = 1,
            Name = "test-ssh",
            Endpoint = JsonSerializer.Serialize(endpoint),
            Roles = "[]"
        };
    }

    [Fact]
    public async Task HealthCheck_InvalidEndpoint_ReturnsFalse()
    {
        var machine = new Machine { Id = 1, Name = "bad", Endpoint = "not-json", Roles = "[]" };
        var strategy = CreateStrategy();

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Failed to parse SSH endpoint");
    }

    [Fact]
    public async Task HealthCheck_EmptyHost_ReturnsFalse()
    {
        var machine = MakeSshMachine(host: "");
        var strategy = CreateStrategy();

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("SSH host is empty");
    }

    [Fact]
    public async Task HealthCheck_ConnectionFailed_ReturnsFalseWithDetail()
    {
        _endpointContextBuilder.Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<IEndpointVariableContributor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointContext { EndpointJson = "{}" });

        _connectionFactory.Setup(f => f.CreateScope(It.IsAny<SshConnectionInfo>()))
            .Throws(new InvalidOperationException("No auth method"));

        var machine = MakeSshMachine();
        var strategy = CreateStrategy();

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("error");
    }

    [Fact]
    public async Task HealthCheck_NullEndpoint_ReturnsFalse()
    {
        var machine = new Machine { Id = 1, Name = "null-ep", Endpoint = null, Roles = "[]" };
        var strategy = CreateStrategy();

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task HealthCheck_EmptyJsonEndpoint_ReturnsFalse()
    {
        var machine = new Machine { Id = 1, Name = "empty-json", Endpoint = "{}", Roles = "[]" };
        var strategy = CreateStrategy();

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task HealthCheck_UsesConnectTimeoutFromPolicy()
    {
        var endpointContext = new EndpointContext { EndpointJson = "{}" };

        _endpointContextBuilder.Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<IEndpointVariableContributor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(endpointContext);

        SshConnectionInfo capturedInfo = null;
        _connectionFactory.Setup(f => f.CreateScope(It.IsAny<SshConnectionInfo>()))
            .Callback<SshConnectionInfo>(info => capturedInfo = info)
            .Throws(new InvalidOperationException("test"));

        var policy = new MachineConnectivityPolicyDto { ConnectTimeoutSeconds = 30 };
        var machine = MakeSshMachine();
        var strategy = CreateStrategy();

        await strategy.CheckHealthAsync(machine, policy, CancellationToken.None);

        capturedInfo.ShouldNotBeNull();
        capturedInfo.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HealthCheck_NullPolicy_UsesDefaultTimeout()
    {
        var endpointContext = new EndpointContext { EndpointJson = "{}" };

        _endpointContextBuilder.Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<IEndpointVariableContributor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(endpointContext);

        SshConnectionInfo capturedInfo = null;
        _connectionFactory.Setup(f => f.CreateScope(It.IsAny<SshConnectionInfo>()))
            .Callback<SshConnectionInfo>(info => capturedInfo = info)
            .Throws(new InvalidOperationException("test"));

        var machine = MakeSshMachine();
        var strategy = CreateStrategy();

        await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        capturedInfo.ShouldNotBeNull();
        capturedInfo.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(SshHealthCheckStrategy.DefaultConnectTimeoutSeconds));
    }

    // ========================================================================
    // ResolveCustomScript
    // ========================================================================

    [Fact]
    public void ResolveCustomScript_NullPolicy_ReturnsNull()
    {
        SshHealthCheckStrategy.ResolveCustomScript(null).ShouldBeNull();
    }

    [Fact]
    public void ResolveCustomScript_OnlyConnectivity_ReturnsNull()
    {
        var policy = new MachineHealthCheckPolicyDto { HealthCheckType = PolicyHealthCheckType.OnlyConnectivity };

        SshHealthCheckStrategy.ResolveCustomScript(policy).ShouldBeNull();
    }

    [Fact]
    public void ResolveCustomScript_RunScript_NullScriptPolicies_ReturnsNull()
    {
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckType = PolicyHealthCheckType.RunScript,
            ScriptPolicies = null
        };

        SshHealthCheckStrategy.ResolveCustomScript(policy).ShouldBeNull();
    }

    [Fact]
    public void ResolveCustomScript_RunScript_NoBashKey_ReturnsNull()
    {
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckType = PolicyHealthCheckType.RunScript,
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["PowerShell"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "Get-Process" }
            }
        };

        SshHealthCheckStrategy.ResolveCustomScript(policy).ShouldBeNull();
    }

    [Fact]
    public void ResolveCustomScript_RunScript_BashCustomScript_ReturnsBody()
    {
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckType = PolicyHealthCheckType.RunScript,
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["Bash"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo healthy" }
            }
        };

        SshHealthCheckStrategy.ResolveCustomScript(policy).ShouldBe("echo healthy");
    }

    [Fact]
    public void ResolveCustomScript_RunScript_BashInheritFromDefault_ReturnsNull()
    {
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckType = PolicyHealthCheckType.RunScript,
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["Bash"] = new() { RunType = ScriptPolicyRunType.InheritFromDefault, ScriptBody = "echo test" }
            }
        };

        SshHealthCheckStrategy.ResolveCustomScript(policy).ShouldBeNull();
    }

    [Fact]
    public void ResolveCustomScript_RunScript_NullBashPolicy_ReturnsNull()
    {
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckType = PolicyHealthCheckType.RunScript,
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["Bash"] = null
            }
        };

        SshHealthCheckStrategy.ResolveCustomScript(policy).ShouldBeNull();
    }
}
