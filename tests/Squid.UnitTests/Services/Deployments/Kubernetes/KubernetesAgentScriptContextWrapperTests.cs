using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentScriptContextWrapperTests
{
    private readonly KubernetesAgentScriptContextWrapper _wrapper = new();

    // === WrapScript — Bash ===

    [Fact]
    public void WrapScript_Bash_PrependsNamespaceContext()
    {
        var variables = MakeVariables("production");

        var result = _wrapper.WrapScript("echo hello", "{}", null, null, ScriptSyntax.Bash, variables);

        result.ShouldContain("kubectl config set-context --current --namespace=\"production\"");
        result.ShouldContain("echo hello");
    }

    [Fact]
    public void WrapScript_Bash_NamespaceBeforeScript()
    {
        var variables = MakeVariables("staging");

        var result = _wrapper.WrapScript("kubectl get pods", "{}", null, null, ScriptSyntax.Bash, variables);

        var nsIndex = result.IndexOf("set-context", System.StringComparison.Ordinal);
        var scriptIndex = result.IndexOf("kubectl get pods", System.StringComparison.Ordinal);

        nsIndex.ShouldBeLessThan(scriptIndex);
    }

    // === WrapScript — PowerShell ===

    [Fact]
    public void WrapScript_PowerShell_PrependsNamespaceContext()
    {
        var variables = MakeVariables("production");

        var result = _wrapper.WrapScript("Get-Process", "{}", null, null, ScriptSyntax.PowerShell, variables);

        result.ShouldContain("kubectl config set-context --current --namespace=\"production\"");
        result.ShouldContain("| Out-Null");
        result.ShouldContain("Get-Process");
    }

    // === WrapScript — namespace resolution ===

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("  ", "default")]
    public void WrapScript_EmptyOrNullNamespace_DefaultsToDefault(string ns, string expected)
    {
        var variables = MakeVariables(ns);

        var result = _wrapper.WrapScript("echo hi", "{}", null, null, ScriptSyntax.Bash, variables);

        result.ShouldContain($"--namespace=\"{expected}\"");
    }

    [Fact]
    public void WrapScript_NullVariables_DefaultsToDefault()
    {
        var result = _wrapper.WrapScript("echo hi", "{}", null, null, ScriptSyntax.Bash, null);

        result.ShouldContain("--namespace=\"default\"");
    }

    [Fact]
    public void WrapScript_NoNamespaceVariable_DefaultsToDefault()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "SomeOtherVariable", Value = "some-value" }
        };

        var result = _wrapper.WrapScript("echo hi", "{}", null, null, ScriptSyntax.Bash, variables);

        result.ShouldContain("--namespace=\"default\"");
    }

    [Fact]
    public void WrapScript_CustomNamespace_UsesProvidedNamespace()
    {
        var variables = MakeVariables("my-custom-ns");

        var result = _wrapper.WrapScript("kubectl apply -f deploy.yaml", "{}", null, null, ScriptSyntax.Bash, variables);

        result.ShouldContain("--namespace=\"my-custom-ns\"");
    }

    // === Helpers ===

    private static List<VariableDto> MakeVariables(string ns)
    {
        return new List<VariableDto>
        {
            new() { Name = "Squid.Action.Kubernetes.Namespace", Value = ns }
        };
    }
}
