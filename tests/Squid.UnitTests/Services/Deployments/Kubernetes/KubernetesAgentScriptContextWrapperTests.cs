using System;
using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentScriptContextWrapperTests
{
    private readonly KubernetesAgentScriptContextWrapper _wrapper = new();

    private static ScriptContext MakeContext(ScriptSyntax syntax, Dictionary<string, string> actionProperties = null) => new()
    {
        Endpoint = new EndpointContext { EndpointJson = "{}" },
        Syntax = syntax,
        ActionProperties = actionProperties
    };

    // === WrapScript — Bash ===

    [Fact]
    public void WrapScript_Bash_PrependsNamespaceContext()
    {
        var props = MakeActionProperties("production");

        var result = _wrapper.WrapScript("echo hello", MakeContext(ScriptSyntax.Bash, props));

        result.ShouldContain("kubectl config set-context --current --namespace=\"production\"");
        result.ShouldContain("echo hello");
    }

    [Fact]
    public void WrapScript_Bash_NamespaceBeforeScript()
    {
        var props = MakeActionProperties("staging");

        var result = _wrapper.WrapScript("kubectl get pods", MakeContext(ScriptSyntax.Bash, props));

        var nsIndex = result.IndexOf("set-context", System.StringComparison.Ordinal);
        var scriptIndex = result.IndexOf("kubectl get pods", System.StringComparison.Ordinal);

        nsIndex.ShouldBeLessThan(scriptIndex);
    }

    // === WrapScript — PowerShell ===

    [Fact]
    public void WrapScript_PowerShell_PrependsNamespaceContext()
    {
        var props = MakeActionProperties("production");

        var result = _wrapper.WrapScript("Get-Process", MakeContext(ScriptSyntax.PowerShell, props));

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
        var props = MakeActionProperties(ns);

        var result = _wrapper.WrapScript("echo hi", MakeContext(ScriptSyntax.Bash, props));

        result.ShouldContain($"--namespace=\"{expected}\"");
    }

    [Fact]
    public void WrapScript_NullActionProperties_DefaultsToDefault()
    {
        var result = _wrapper.WrapScript("echo hi", MakeContext(ScriptSyntax.Bash, null));

        result.ShouldContain("--namespace=\"default\"");
    }

    [Fact]
    public void WrapScript_EmptyActionProperties_DefaultsToDefault()
    {
        var result = _wrapper.WrapScript("echo hi", MakeContext(ScriptSyntax.Bash, new Dictionary<string, string>()));

        result.ShouldContain("--namespace=\"default\"");
    }

    [Fact]
    public void WrapScript_NoNamespaceProperty_DefaultsToDefault()
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SomeOtherProperty"] = "some-value"
        };

        var result = _wrapper.WrapScript("echo hi", MakeContext(ScriptSyntax.Bash, props));

        result.ShouldContain("--namespace=\"default\"");
    }

    [Fact]
    public void WrapScript_CustomNamespace_UsesProvidedNamespace()
    {
        var props = MakeActionProperties("my-custom-ns");

        var result = _wrapper.WrapScript("kubectl apply -f deploy.yaml", MakeContext(ScriptSyntax.Bash, props));

        result.ShouldContain("--namespace=\"my-custom-ns\"");
    }

    [Fact]
    public void WrapScript_LegacyNamespaceProperty_UsesLegacyNamespace()
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Squid.Action.Kubernetes.Namespace"] = "legacy-ns"
        };

        var result = _wrapper.WrapScript("echo hi", MakeContext(ScriptSyntax.Bash, props));

        result.ShouldContain("--namespace=\"legacy-ns\"");
    }

    // === Security — Namespace Sanitization ===

    [Theory]
    [InlineData("production")]
    [InlineData("my-namespace")]
    [InlineData("ns-123")]
    [InlineData("default")]
    public void WrapScript_ValidNamespace_DoesNotThrow(string ns)
    {
        var props = MakeActionProperties(ns);

        Should.NotThrow(() => _wrapper.WrapScript("echo hi", MakeContext(ScriptSyntax.Bash, props)));
    }

    [Theory]
    [InlineData("$(cmd)")]
    [InlineData("`rm -rf /`")]
    [InlineData("ns;echo pwned")]
    [InlineData("ns\"injection")]
    [InlineData("NS_UPPER")]
    [InlineData("ns with spaces")]
    public void WrapScript_InvalidNamespace_Throws(string ns)
    {
        var props = MakeActionProperties(ns);

        Should.Throw<ArgumentException>(() => _wrapper.WrapScript("echo hi", MakeContext(ScriptSyntax.Bash, props)));
    }

    [Fact]
    public void ValidateKubernetesName_NullOrEmpty_DoesNotThrow()
    {
        Should.NotThrow(() => KubernetesAgentScriptContextWrapper.ValidateKubernetesName(null));
        Should.NotThrow(() => KubernetesAgentScriptContextWrapper.ValidateKubernetesName(""));
        Should.NotThrow(() => KubernetesAgentScriptContextWrapper.ValidateKubernetesName("  "));
    }

    // === Helpers ===

    private static Dictionary<string, string> MakeActionProperties(string ns)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Squid.Action.KubernetesContainers.Namespace"] = ns
        };
    }
}
