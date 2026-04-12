using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class IntentVariableExpanderTests
{
    private static VariableDictionary MakeDict(params (string Name, string Value)[] vars)
    {
        var list = new List<VariableDto>();
        foreach (var (name, value) in vars)
            list.Add(new VariableDto { Name = name, Value = value });
        return VariableDictionaryFactory.Create(list);
    }

    // ========== RunScriptIntent ==========

    [Fact]
    public void RunScriptIntent_ExpandsScriptBody()
    {
        var dict = MakeDict(("Env", "Production"));
        var intent = new RunScriptIntent { Name = "run-script", ScriptBody = "echo #{Env}" };

        var result = IntentVariableExpander.Expand(intent, dict);

        result.ShouldBeOfType<RunScriptIntent>();
        ((RunScriptIntent)result).ScriptBody.ShouldBe("echo Production");
    }

    [Fact]
    public void RunScriptIntent_NullScriptBody_NoOp()
    {
        var dict = MakeDict(("Env", "Production"));
        var intent = new RunScriptIntent { Name = "run-script", ScriptBody = null! };

        var result = IntentVariableExpander.Expand(intent, dict);

        result.ShouldBeOfType<RunScriptIntent>();
        ((RunScriptIntent)result).ScriptBody.ShouldBeNull();
    }

    [Fact]
    public void RunScriptIntent_NoTokens_Unchanged()
    {
        var dict = MakeDict(("Env", "Production"));
        var intent = new RunScriptIntent { Name = "run-script", ScriptBody = "echo hello" };

        var result = IntentVariableExpander.Expand(intent, dict);

        ((RunScriptIntent)result).ScriptBody.ShouldBe("echo hello");
    }

    [Fact]
    public void RunScriptIntent_IndirectVariable_Resolved()
    {
        var dict = MakeDict(("Target", "#{ApiUrl}"), ("ApiUrl", "https://api.example.com"));
        var intent = new RunScriptIntent { Name = "run-script", ScriptBody = "curl #{Target}" };

        var result = IntentVariableExpander.Expand(intent, dict);

        ((RunScriptIntent)result).ScriptBody.ShouldBe("curl https://api.example.com");
    }

    [Fact]
    public void RunScriptIntent_PreservesNonExpandableFields()
    {
        var dict = MakeDict(("Env", "Production"));
        var intent = new RunScriptIntent
        {
            Name = "run-script",
            ScriptBody = "echo #{Env}",
            StepName = "Deploy",
            ActionName = "Run Script",
            Syntax = ScriptSyntax.PowerShell,
            InjectRuntimeBundle = false
        };

        var result = (RunScriptIntent)IntentVariableExpander.Expand(intent, dict);

        result.Name.ShouldBe("run-script");
        result.StepName.ShouldBe("Deploy");
        result.ActionName.ShouldBe("Run Script");
        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        result.InjectRuntimeBundle.ShouldBeFalse();
    }

    // ========== HelmUpgradeIntent ==========

    [Fact]
    public void HelmUpgradeIntent_ExpandsAllStringFields()
    {
        var dict = MakeDict(
            ("Release", "my-app"),
            ("Chart", "nginx"),
            ("Ns", "production"),
            ("HelmExe", "/usr/local/bin/helm"),
            ("Args", "--debug"),
            ("Timeout", "5m"));
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "#{Release}",
            ChartReference = "#{Chart}",
            Namespace = "#{Ns}",
            CustomHelmExecutable = "#{HelmExe}",
            AdditionalArgs = "#{Args}",
            Timeout = "#{Timeout}"
        };

        var result = (HelmUpgradeIntent)IntentVariableExpander.Expand(intent, dict);

        result.ReleaseName.ShouldBe("my-app");
        result.ChartReference.ShouldBe("nginx");
        result.Namespace.ShouldBe("production");
        result.CustomHelmExecutable.ShouldBe("/usr/local/bin/helm");
        result.AdditionalArgs.ShouldBe("--debug");
        result.Timeout.ShouldBe("5m");
    }

    [Fact]
    public void HelmUpgradeIntent_ExpandsInlineValues()
    {
        var dict = MakeDict(("Port", "8080"), ("Image", "nginx:latest"));
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "release",
            ChartReference = "chart",
            InlineValues = new Dictionary<string, string>
            {
                ["service.port"] = "#{Port}",
                ["image.tag"] = "#{Image}",
                ["plain"] = "no-token"
            }
        };

        var result = (HelmUpgradeIntent)IntentVariableExpander.Expand(intent, dict);

        result.InlineValues["service.port"].ShouldBe("8080");
        result.InlineValues["image.tag"].ShouldBe("nginx:latest");
        result.InlineValues["plain"].ShouldBe("no-token");
    }

    [Fact]
    public void HelmUpgradeIntent_EmptyInlineValues_StaysEmpty()
    {
        var dict = MakeDict(("X", "Y"));
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "release",
            ChartReference = "chart",
            InlineValues = new Dictionary<string, string>()
        };

        var result = (HelmUpgradeIntent)IntentVariableExpander.Expand(intent, dict);

        result.InlineValues.Count.ShouldBe(0);
    }

    // ========== KubernetesApplyIntent ==========

    [Fact]
    public void KubernetesApplyIntent_ExpandsNamespace()
    {
        var dict = MakeDict(("Ns", "staging"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = Array.Empty<Squid.Core.Services.DeploymentExecution.Script.Files.DeploymentFile>(),
            Namespace = "#{Ns}"
        };

        var result = (KubernetesApplyIntent)IntentVariableExpander.Expand(intent, dict);

        result.Namespace.ShouldBe("staging");
    }

    [Fact]
    public void KubernetesApplyIntent_PreservesNonStringFields()
    {
        var dict = MakeDict(("Ns", "staging"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = Array.Empty<Squid.Core.Services.DeploymentExecution.Script.Files.DeploymentFile>(),
            Namespace = "#{Ns}",
            ServerSideApply = true,
            ForceConflicts = true,
            ObjectStatusCheck = true,
            StatusCheckTimeoutSeconds = 600
        };

        var result = (KubernetesApplyIntent)IntentVariableExpander.Expand(intent, dict);

        result.ServerSideApply.ShouldBeTrue();
        result.ForceConflicts.ShouldBeTrue();
        result.ObjectStatusCheck.ShouldBeTrue();
        result.StatusCheckTimeoutSeconds.ShouldBe(600);
    }

    // ========== KubernetesKustomizeIntent ==========

    [Fact]
    public void KubernetesKustomizeIntent_ExpandsAllStringFields()
    {
        var dict = MakeDict(
            ("Overlay", "./overlays/prod"),
            ("KustomizePath", "/usr/local/bin/kustomize"),
            ("Ns", "production"),
            ("Args", "--enable-helm"));
        var intent = new KubernetesKustomizeIntent
        {
            Name = "k8s-kustomize",
            OverlayPath = "#{Overlay}",
            CustomKustomizePath = "#{KustomizePath}",
            Namespace = "#{Ns}",
            AdditionalArgs = "#{Args}"
        };

        var result = (KubernetesKustomizeIntent)IntentVariableExpander.Expand(intent, dict);

        result.OverlayPath.ShouldBe("./overlays/prod");
        result.CustomKustomizePath.ShouldBe("/usr/local/bin/kustomize");
        result.Namespace.ShouldBe("production");
        result.AdditionalArgs.ShouldBe("--enable-helm");
    }

    // ========== OpenClawInvokeIntent ==========

    [Fact]
    public void OpenClawInvokeIntent_ExpandsParameterValues()
    {
        var dict = MakeDict(("ToolName", "kubectl"), ("Action", "apply"));
        var intent = new OpenClawInvokeIntent
        {
            Name = "openclaw-invoke",
            Kind = OpenClawInvocationKind.InvokeTool,
            Parameters = new Dictionary<string, string>
            {
                ["tool"] = "#{ToolName}",
                ["action"] = "#{Action}",
                ["plain"] = "literal"
            }
        };

        var result = (OpenClawInvokeIntent)IntentVariableExpander.Expand(intent, dict);

        result.Parameters["tool"].ShouldBe("kubectl");
        result.Parameters["action"].ShouldBe("apply");
        result.Parameters["plain"].ShouldBe("literal");
    }

    [Fact]
    public void OpenClawInvokeIntent_EmptyParameters_StaysEmpty()
    {
        var dict = MakeDict(("X", "Y"));
        var intent = new OpenClawInvokeIntent
        {
            Name = "openclaw-invoke",
            Kind = OpenClawInvocationKind.Wake,
            Parameters = new Dictionary<string, string>()
        };

        var result = (OpenClawInvokeIntent)IntentVariableExpander.Expand(intent, dict);

        result.Parameters.Count.ShouldBe(0);
    }

    // ========== ManualInterventionIntent ==========

    [Fact]
    public void ManualInterventionIntent_ExpandsInstructions()
    {
        var dict = MakeDict(("AppName", "My Service"));
        var intent = new ManualInterventionIntent
        {
            Name = "manual-intervention",
            Instructions = "Please approve deployment of #{AppName}"
        };

        var result = (ManualInterventionIntent)IntentVariableExpander.Expand(intent, dict);

        result.Instructions.ShouldBe("Please approve deployment of My Service");
    }

    // ========== Unknown intent type ==========

    [Fact]
    public void UnknownIntentType_ReturnedUnchanged()
    {
        var dict = MakeDict(("X", "Y"));
        var intent = new TestOnlyIntent { Name = "test-only" };

        var result = IntentVariableExpander.Expand(intent, dict);

        result.ShouldBeSameAs(intent);
    }

    private sealed record TestOnlyIntent : ExecutionIntent;
}
