using System.Collections.Generic;
using System.Text.Json;
using Moq;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

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

    private static (AccountType, string) TokenAccount() =>
        (AccountType.Token, JsonSerializer.Serialize(new TokenCredentials { Token = "test-token" }));

    // === WrapScript — valid endpoint, Bash syntax ===

    [Fact]
    public void WrapScript_ValidEndpoint_Bash_CallsBuilder()
    {
        var json = MakeEndpointJson();
        var (at, cj) = TokenAccount();
        var variables = new List<VariableDto>();

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesApiEndpointDto>(),
                at, cj,
                ScriptSyntax.Bash,
                null))
            .Returns("wrapped-bash");

        var result = _wrapper.WrapScript("echo hi", json, at, cj, ScriptSyntax.Bash, variables);

        result.ShouldBe("wrapped-bash");
        _builderMock.Verify(b => b.WrapWithContext(
            "echo hi",
            It.IsAny<KubernetesApiEndpointDto>(),
            at, cj,
            ScriptSyntax.Bash,
            null), Times.Once);
    }

    // === WrapScript — valid endpoint, PowerShell syntax ===

    [Fact]
    public void WrapScript_ValidEndpoint_PowerShell_CallsBuilder()
    {
        var json = MakeEndpointJson();
        var (at, cj) = TokenAccount();
        var variables = new List<VariableDto>();

        _builderMock.Setup(b => b.WrapWithContext(
                "Get-Process",
                It.IsAny<KubernetesApiEndpointDto>(),
                at, cj,
                ScriptSyntax.PowerShell,
                null))
            .Returns("wrapped-powershell");

        var result = _wrapper.WrapScript("Get-Process", json, at, cj, ScriptSyntax.PowerShell, variables);

        result.ShouldBe("wrapped-powershell");
        _builderMock.Verify(b => b.WrapWithContext(
            "Get-Process",
            It.IsAny<KubernetesApiEndpointDto>(),
            at, cj,
            ScriptSyntax.PowerShell,
            null), Times.Once);
    }

    // === WrapScript — endpoint deserialization ===

    [Fact]
    public void WrapScript_DeserializesEndpointCorrectly_ClusterUrlPassedToBuilder()
    {
        var json = MakeEndpointJson(clusterUrl: "https://my-cluster:8443");
        var (at, cj) = TokenAccount();

        _builderMock.Setup(b => b.WrapWithContext(
                It.IsAny<string>(),
                It.Is<KubernetesApiEndpointDto>(e => e.ClusterUrl == "https://my-cluster:8443"),
                at, cj,
                It.IsAny<ScriptSyntax>(),
                It.IsAny<string>()))
            .Returns("ok");

        _wrapper.WrapScript("echo", json, at, cj, ScriptSyntax.Bash, new List<VariableDto>());

        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.Is<KubernetesApiEndpointDto>(e => e.ClusterUrl == "https://my-cluster:8443"),
            at, cj,
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Once);
    }

    // === WrapScript — custom kubectl ===

    [Fact]
    public void WrapScript_WithCustomKubectl_PassesCustomPath()
    {
        var json = MakeEndpointJson();
        var (at, cj) = TokenAccount();
        var variables = new List<VariableDto>
        {
            new() { Name = "Squid.Action.Kubernetes.CustomKubectlExecutable", Value = "/usr/local/bin/kubectl-1.28" }
        };

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesApiEndpointDto>(),
                at, cj,
                ScriptSyntax.Bash,
                "/usr/local/bin/kubectl-1.28"))
            .Returns("wrapped-with-custom-kubectl");

        var result = _wrapper.WrapScript("echo hi", json, at, cj, ScriptSyntax.Bash, variables);

        result.ShouldBe("wrapped-with-custom-kubectl");
    }

    [Fact]
    public void WrapScript_VariablesWithoutCustomKubectl_PassesNullAsCustomPath()
    {
        var json = MakeEndpointJson();
        var (at, cj) = TokenAccount();
        var variables = new List<VariableDto>
        {
            new() { Name = "SomeOtherVariable", Value = "some-value" }
        };

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesApiEndpointDto>(),
                at, cj,
                ScriptSyntax.Bash,
                null))
            .Returns("wrapped-no-custom");

        var result = _wrapper.WrapScript("echo hi", json, at, cj, ScriptSyntax.Bash, variables);

        result.ShouldBe("wrapped-no-custom");
        _builderMock.Verify(b => b.WrapWithContext(
            "echo hi",
            It.IsAny<KubernetesApiEndpointDto>(),
            at, cj,
            ScriptSyntax.Bash,
            null), Times.Once);
    }

    [Fact]
    public void WrapScript_NullVariables_PassesNullAsCustomPath()
    {
        var json = MakeEndpointJson();
        var (at, cj) = TokenAccount();

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesApiEndpointDto>(),
                at, cj,
                ScriptSyntax.Bash,
                null))
            .Returns("wrapped-null-vars");

        var result = _wrapper.WrapScript("echo hi", json, at, cj, ScriptSyntax.Bash, null);

        result.ShouldBe("wrapped-null-vars");
    }

    // === WrapScript — bad input returns original script ===

    [Fact]
    public void WrapScript_NullEndpointJson_ReturnsOriginalScript()
    {
        var (at, cj) = TokenAccount();

        var result = _wrapper.WrapScript("echo hi", null, at, cj, ScriptSyntax.Bash, null);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<KubernetesApiEndpointDto>(),
            It.IsAny<AccountType?>(),
            It.IsAny<string>(),
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WrapScript_EmptyEndpointJson_ReturnsOriginalScript()
    {
        var (at, cj) = TokenAccount();

        var result = _wrapper.WrapScript("echo hi", string.Empty, at, cj, ScriptSyntax.Bash, null);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<KubernetesApiEndpointDto>(),
            It.IsAny<AccountType?>(),
            It.IsAny<string>(),
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WrapScript_InvalidJson_ReturnsOriginalScript()
    {
        var (at, cj) = TokenAccount();

        var result = _wrapper.WrapScript("echo hi", "not-json", at, cj, ScriptSyntax.Bash, null);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<KubernetesApiEndpointDto>(),
            It.IsAny<AccountType?>(),
            It.IsAny<string>(),
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Never);
    }
}
