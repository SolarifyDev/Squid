using System.Collections.Generic;
using System.Text.Json;
using Moq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiScriptContextWrapperTests
{
    private readonly Mock<IKubernetesApiContextScriptBuilder> _builderMock = new();
    private readonly KubernetesApiScriptContextWrapper _wrapper;

    public KubernetesApiScriptContextWrapperTests()
    {
        _wrapper = new KubernetesApiScriptContextWrapper(_builderMock.Object);
    }

    private static string MakeEndpointJson(
        string clusterUrl = "https://k8s.example.com:6443",
        string ns = "default") =>
        JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = clusterUrl,
            Namespace = ns
        });

    private const string DefaultSentinel = "__DEFAULT__";

    private static ScriptContext MakeContext(
        string endpointJson = DefaultSentinel,
        AccountType? accountType = null,
        string credentialsJson = null,
        ScriptSyntax syntax = ScriptSyntax.Bash,
        List<VariableDto> variables = null)
    {
        var resolvedJson = endpointJson == DefaultSentinel ? MakeEndpointJson() : endpointJson;

        var endpoint = new EndpointContext { EndpointJson = resolvedJson };

        if (accountType.HasValue)
            endpoint.SetAccountData(accountType.Value, credentialsJson);

        return new ScriptContext
        {
            Endpoint = endpoint,
            Syntax = syntax,
            Variables = variables
        };
    }

    // === WrapScript — valid endpoint, Bash syntax ===

    [Fact]
    public void WrapScript_ValidEndpoint_Bash_CallsBuilder()
    {
        var ctx = MakeContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "test-token" }),
            variables: new List<VariableDto>());

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<ScriptContext>(),
                null))
            .Returns("wrapped-bash");

        var result = _wrapper.WrapScript("echo hi", ctx);

        result.ShouldBe("wrapped-bash");
        _builderMock.Verify(b => b.WrapWithContext(
            "echo hi",
            It.IsAny<ScriptContext>(),
            null), Times.Once);
    }

    // === WrapScript — valid endpoint, PowerShell syntax ===

    [Fact]
    public void WrapScript_ValidEndpoint_PowerShell_CallsBuilder()
    {
        var ctx = MakeContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "test-token" }),
            syntax: ScriptSyntax.PowerShell,
            variables: new List<VariableDto>());

        _builderMock.Setup(b => b.WrapWithContext(
                "Get-Process",
                It.IsAny<ScriptContext>(),
                null))
            .Returns("wrapped-powershell");

        var result = _wrapper.WrapScript("Get-Process", ctx);

        result.ShouldBe("wrapped-powershell");
        _builderMock.Verify(b => b.WrapWithContext(
            "Get-Process",
            It.IsAny<ScriptContext>(),
            null), Times.Once);
    }

    // === WrapScript — custom kubectl ===

    [Fact]
    public void WrapScript_WithCustomKubectl_PassesCustomPath()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Squid.Action.Kubernetes.CustomKubectlExecutable", Value = "/usr/local/bin/kubectl-1.28" }
        };

        var ctx = MakeContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "test-token" }),
            variables: variables);

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<ScriptContext>(),
                "/usr/local/bin/kubectl-1.28"))
            .Returns("wrapped-with-custom-kubectl");

        var result = _wrapper.WrapScript("echo hi", ctx);

        result.ShouldBe("wrapped-with-custom-kubectl");
    }

    [Fact]
    public void WrapScript_VariablesWithoutCustomKubectl_PassesNullAsCustomPath()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "SomeOtherVariable", Value = "some-value" }
        };

        var ctx = MakeContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "test-token" }),
            variables: variables);

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<ScriptContext>(),
                null))
            .Returns("wrapped-no-custom");

        var result = _wrapper.WrapScript("echo hi", ctx);

        result.ShouldBe("wrapped-no-custom");
        _builderMock.Verify(b => b.WrapWithContext(
            "echo hi",
            It.IsAny<ScriptContext>(),
            null), Times.Once);
    }

    [Fact]
    public void WrapScript_NullVariables_PassesNullAsCustomPath()
    {
        var ctx = MakeContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "test-token" }),
            variables: null);

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<ScriptContext>(),
                null))
            .Returns("wrapped-null-vars");

        var result = _wrapper.WrapScript("echo hi", ctx);

        result.ShouldBe("wrapped-null-vars");
    }

    // === WrapScript — bad input returns original script ===

    [Fact]
    public void WrapScript_NullEndpointJson_ReturnsOriginalScript()
    {
        var ctx = MakeContext(endpointJson: null);

        var result = _wrapper.WrapScript("echo hi", ctx);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WrapScript_EmptyEndpointJson_ReturnsOriginalScript()
    {
        var ctx = MakeContext(endpointJson: string.Empty);

        var result = _wrapper.WrapScript("echo hi", ctx);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WrapScript_InvalidJson_ReturnsOriginalScript()
    {
        var ctx = MakeContext(endpointJson: "not-json");

        var result = _wrapper.WrapScript("echo hi", ctx);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Never);
    }
}
