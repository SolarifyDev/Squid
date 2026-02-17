using System.Collections.Generic;
using System.Text.Json;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesScriptContextWrapperTests
{
    private readonly Mock<IKubernetesContextScriptBuilder> _builderMock = new();
    private readonly KubernetesScriptContextWrapper _wrapper;

    public KubernetesScriptContextWrapperTests()
    {
        _wrapper = new KubernetesScriptContextWrapper(_builderMock.Object);
    }

    private static string MakeEndpointJson(
        string clusterUrl = "https://k8s.example.com:6443",
        string ns = "default") =>
        JsonSerializer.Serialize(new KubernetesEndpointDto
        {
            CommunicationStyle = "Kubernetes",
            ClusterUrl = clusterUrl,
            Namespace = ns
        });

    private static DeploymentAccount CreateTokenAccount() => new()
    {
        AccountType = AccountType.Token,
        Token = "test-token"
    };

    // === CanWrap ===

    [Fact]
    public void CanWrap_Kubernetes_ReturnsTrue()
    {
        _wrapper.CanWrap("Kubernetes").ShouldBeTrue();
    }

    [Fact]
    public void CanWrap_CaseInsensitive_ReturnsTrue()
    {
        _wrapper.CanWrap("kubernetes").ShouldBeTrue();
    }

    [Fact]
    public void CanWrap_UpperCase_ReturnsTrue()
    {
        _wrapper.CanWrap("KUBERNETES").ShouldBeTrue();
    }

    [Fact]
    public void CanWrap_Ssh_ReturnsFalse()
    {
        _wrapper.CanWrap("Ssh").ShouldBeFalse();
    }

    [Fact]
    public void CanWrap_Empty_ReturnsFalse()
    {
        _wrapper.CanWrap(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void CanWrap_Null_ReturnsFalse()
    {
        _wrapper.CanWrap(null).ShouldBeFalse();
    }

    // === WrapScript — valid endpoint, Bash syntax ===

    [Fact]
    public void WrapScript_ValidEndpoint_Bash_CallsBuilder()
    {
        var json = MakeEndpointJson();
        var account = CreateTokenAccount();
        var variables = new List<VariableDto>();

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesEndpointDto>(),
                account,
                ScriptSyntax.Bash,
                null))
            .Returns("wrapped-bash");

        var result = _wrapper.WrapScript("echo hi", json, account, ScriptSyntax.Bash, variables);

        result.ShouldBe("wrapped-bash");
        _builderMock.Verify(b => b.WrapWithContext(
            "echo hi",
            It.IsAny<KubernetesEndpointDto>(),
            account,
            ScriptSyntax.Bash,
            null), Times.Once);
    }

    // === WrapScript — valid endpoint, PowerShell syntax ===

    [Fact]
    public void WrapScript_ValidEndpoint_PowerShell_CallsBuilder()
    {
        var json = MakeEndpointJson();
        var account = CreateTokenAccount();
        var variables = new List<VariableDto>();

        _builderMock.Setup(b => b.WrapWithContext(
                "Get-Process",
                It.IsAny<KubernetesEndpointDto>(),
                account,
                ScriptSyntax.PowerShell,
                null))
            .Returns("wrapped-powershell");

        var result = _wrapper.WrapScript("Get-Process", json, account, ScriptSyntax.PowerShell, variables);

        result.ShouldBe("wrapped-powershell");
        _builderMock.Verify(b => b.WrapWithContext(
            "Get-Process",
            It.IsAny<KubernetesEndpointDto>(),
            account,
            ScriptSyntax.PowerShell,
            null), Times.Once);
    }

    // === WrapScript — endpoint deserialization ===

    [Fact]
    public void WrapScript_DeserializesEndpointCorrectly_ClusterUrlPassedToBuilder()
    {
        var json = MakeEndpointJson(clusterUrl: "https://my-cluster:8443");
        var account = CreateTokenAccount();

        _builderMock.Setup(b => b.WrapWithContext(
                It.IsAny<string>(),
                It.Is<KubernetesEndpointDto>(e => e.ClusterUrl == "https://my-cluster:8443"),
                account,
                It.IsAny<ScriptSyntax>(),
                It.IsAny<string>()))
            .Returns("ok");

        _wrapper.WrapScript("echo", json, account, ScriptSyntax.Bash, new List<VariableDto>());

        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.Is<KubernetesEndpointDto>(e => e.ClusterUrl == "https://my-cluster:8443"),
            account,
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Once);
    }

    // === WrapScript — custom kubectl ===

    [Fact]
    public void WrapScript_WithCustomKubectl_PassesCustomPath()
    {
        var json = MakeEndpointJson();
        var account = CreateTokenAccount();
        var variables = new List<VariableDto>
        {
            new() { Name = "Squid.Action.Kubernetes.CustomKubectlExecutable", Value = "/usr/local/bin/kubectl-1.28" }
        };

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesEndpointDto>(),
                account,
                ScriptSyntax.Bash,
                "/usr/local/bin/kubectl-1.28"))
            .Returns("wrapped-with-custom-kubectl");

        var result = _wrapper.WrapScript("echo hi", json, account, ScriptSyntax.Bash, variables);

        result.ShouldBe("wrapped-with-custom-kubectl");
    }

    [Fact]
    public void WrapScript_VariablesWithoutCustomKubectl_PassesNullAsCustomPath()
    {
        var json = MakeEndpointJson();
        var account = CreateTokenAccount();
        var variables = new List<VariableDto>
        {
            new() { Name = "SomeOtherVariable", Value = "some-value" }
        };

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesEndpointDto>(),
                account,
                ScriptSyntax.Bash,
                null))
            .Returns("wrapped-no-custom");

        var result = _wrapper.WrapScript("echo hi", json, account, ScriptSyntax.Bash, variables);

        result.ShouldBe("wrapped-no-custom");
        _builderMock.Verify(b => b.WrapWithContext(
            "echo hi",
            It.IsAny<KubernetesEndpointDto>(),
            account,
            ScriptSyntax.Bash,
            null), Times.Once);
    }

    [Fact]
    public void WrapScript_NullVariables_PassesNullAsCustomPath()
    {
        var json = MakeEndpointJson();
        var account = CreateTokenAccount();

        _builderMock.Setup(b => b.WrapWithContext(
                "echo hi",
                It.IsAny<KubernetesEndpointDto>(),
                account,
                ScriptSyntax.Bash,
                null))
            .Returns("wrapped-null-vars");

        var result = _wrapper.WrapScript("echo hi", json, account, ScriptSyntax.Bash, null);

        result.ShouldBe("wrapped-null-vars");
    }

    // === WrapScript — bad input returns original script ===

    [Fact]
    public void WrapScript_NullEndpointJson_ReturnsOriginalScript()
    {
        var result = _wrapper.WrapScript("echo hi", null, CreateTokenAccount(), ScriptSyntax.Bash, null);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<KubernetesEndpointDto>(),
            It.IsAny<DeploymentAccount>(),
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WrapScript_EmptyEndpointJson_ReturnsOriginalScript()
    {
        var result = _wrapper.WrapScript("echo hi", string.Empty, CreateTokenAccount(), ScriptSyntax.Bash, null);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<KubernetesEndpointDto>(),
            It.IsAny<DeploymentAccount>(),
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WrapScript_InvalidJson_ReturnsOriginalScript()
    {
        var result = _wrapper.WrapScript("echo hi", "not-json", CreateTokenAccount(), ScriptSyntax.Bash, null);

        result.ShouldBe("echo hi");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<KubernetesEndpointDto>(),
            It.IsAny<DeploymentAccount>(),
            It.IsAny<ScriptSyntax>(),
            It.IsAny<string>()), Times.Never);
    }
}
