using System.Text;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class IntentStructuredConfigReplacerTests
{
    private static VariableDictionary MakeDict(params (string Name, string Value)[] vars)
    {
        var list = new List<VariableDto>();
        foreach (var (name, value) in vars)
            list.Add(new VariableDto { Name = name, Value = value });
        return VariableDictionaryFactory.Create(list);
    }

    private static DeploymentActionDto MakeAction(bool enabled)
    {
        var props = new List<DeploymentActionPropertyDto>();

        if (enabled)
        {
            props.Add(new DeploymentActionPropertyDto
            {
                PropertyName = SpecialVariables.Action.StructuredConfigurationVariablesEnabled,
                PropertyValue = "True"
            });
        }

        return new DeploymentActionDto { Id = 1, Name = "Test", ActionType = "Test", Properties = props };
    }

    private static DeploymentFile MakeJsonFile(string path, string json)
        => DeploymentFile.Asset(path, Encoding.UTF8.GetBytes(json));

    private static DeploymentFile MakeYamlFile(string path, string yaml)
        => DeploymentFile.Asset(path, Encoding.UTF8.GetBytes(yaml));

    // ========== Feature flag ==========

    [Fact]
    public void Disabled_ReturnsUnchanged()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { MakeYamlFile("values.yaml", "Key: old\n") }
        };
        var action = MakeAction(enabled: false);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        result.ShouldBeSameAs(intent);
        warnings.Count.ShouldBe(0);
    }

    [Fact]
    public void NullAction_ReturnsUnchanged()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { MakeYamlFile("values.yaml", "Key: old\n") }
        };

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, null, dict);

        result.ShouldBeSameAs(intent);
        warnings.Count.ShouldBe(0);
    }

    // ========== No file collections ==========

    [Fact]
    public void RunScriptIntent_NoFilesToReplace_ReturnsUnchanged()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new RunScriptIntent { Name = "run-script", ScriptBody = "echo #{Key}" };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        result.ShouldBeSameAs(intent);
        warnings.Count.ShouldBe(0);
    }

    [Fact]
    public void OpenClawInvokeIntent_NoFilesToReplace_ReturnsUnchanged()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new OpenClawInvokeIntent { Name = "openclaw", Kind = OpenClawInvocationKind.Wake };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        result.ShouldBeSameAs(intent);
        warnings.Count.ShouldBe(0);
    }

    // ========== KubernetesApplyIntent YAML replacement ==========

    [Fact]
    public void KubernetesApply_ReplacesYamlFiles()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { MakeYamlFile("values.yaml", "Key: old\n") }
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        var k8sResult = result.ShouldBeOfType<KubernetesApplyIntent>();
        var content = Encoding.UTF8.GetString(k8sResult.YamlFiles[0].Content);
        content.ShouldContain("Key: new");
        warnings.Count.ShouldBe(0);
    }

    [Fact]
    public void KubernetesApply_EmptyYamlFiles_ReturnsUnchanged()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = Array.Empty<DeploymentFile>()
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        result.ShouldBeSameAs(intent);
        warnings.Count.ShouldBe(0);
    }

    // ========== HelmUpgradeIntent values replacement ==========

    [Fact]
    public void HelmUpgrade_ReplacesValuesFiles()
    {
        var dict = MakeDict(("Port", "8080"));
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "release",
            ChartReference = "chart",
            ValuesFiles = new[] { MakeYamlFile("values.yaml", "Port: 3000\n") }
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        var helmResult = result.ShouldBeOfType<HelmUpgradeIntent>();
        var content = Encoding.UTF8.GetString(helmResult.ValuesFiles[0].Content);
        content.ShouldContain("Port: 8080");
        warnings.Count.ShouldBe(0);
    }

    [Fact]
    public void HelmUpgrade_EmptyValuesFiles_ReturnsUnchanged()
    {
        var dict = MakeDict(("Port", "8080"));
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "release",
            ChartReference = "chart",
            ValuesFiles = Array.Empty<DeploymentFile>()
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        result.ShouldBeSameAs(intent);
        warnings.Count.ShouldBe(0);
    }

    // ========== JSON file replacement ==========

    [Fact]
    public void JsonFile_ReplacesValues()
    {
        var dict = MakeDict(("ConnectionString", "Server=prod;Database=app"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { MakeJsonFile("config.json", """{"ConnectionString":"placeholder"}""") }
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        var k8sResult = result.ShouldBeOfType<KubernetesApplyIntent>();
        var content = Encoding.UTF8.GetString(k8sResult.YamlFiles[0].Content);
        content.ShouldContain("Server=prod;Database=app");
        warnings.Count.ShouldBe(0);
    }

    // ========== Reserved variables ==========

    [Fact]
    public void ReservedVariables_Excluded()
    {
        var dict = MakeDict(
            ("Squid.Machine.Name", "m1"),
            ("System.TeamProject", "proj"),
            ("UserVar", "replaced"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { MakeYamlFile("values.yaml", "UserVar: old\n") }
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        var k8sResult = result.ShouldBeOfType<KubernetesApplyIntent>();
        var content = Encoding.UTF8.GetString(k8sResult.YamlFiles[0].Content);
        content.ShouldContain("UserVar: replaced");
    }

    // ========== Warnings ==========

    [Fact]
    public void NonStructuredFile_Skipped_NoWarning()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[]
            {
                DeploymentFile.Script("deploy.sh", Encoding.UTF8.GetBytes("echo hello"), true)
            }
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        warnings.Count.ShouldBe(0);
    }

    // ========== Preserves other intent fields ==========

    [Fact]
    public void KubernetesApply_PreservesOtherFields()
    {
        var dict = MakeDict(("Key", "new"));
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { MakeYamlFile("values.yaml", "Key: old\n") },
            Namespace = "production",
            ServerSideApply = true,
            ObjectStatusCheck = true
        };
        var action = MakeAction(enabled: true);

        var (result, _) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        var k8sResult = result.ShouldBeOfType<KubernetesApplyIntent>();
        k8sResult.Namespace.ShouldBe("production");
        k8sResult.ServerSideApply.ShouldBeTrue();
        k8sResult.ObjectStatusCheck.ShouldBeTrue();
    }

    [Fact]
    public void HelmUpgrade_PreservesOtherFields()
    {
        var dict = MakeDict(("Port", "8080"));
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "my-release",
            ChartReference = "my-chart",
            Namespace = "staging",
            Wait = true,
            ValuesFiles = new[] { MakeYamlFile("values.yaml", "Port: 3000\n") }
        };
        var action = MakeAction(enabled: true);

        var (result, _) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        var helmResult = result.ShouldBeOfType<HelmUpgradeIntent>();
        helmResult.ReleaseName.ShouldBe("my-release");
        helmResult.ChartReference.ShouldBe("my-chart");
        helmResult.Namespace.ShouldBe("staging");
        helmResult.Wait.ShouldBeTrue();
    }

    // ========== No replacements available ==========

    [Fact]
    public void NoReplacementsAvailable_ReturnsUnchanged()
    {
        var dict = MakeDict(); // empty dictionary
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { MakeYamlFile("values.yaml", "Key: old\n") }
        };
        var action = MakeAction(enabled: true);

        var (result, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, action, dict);

        result.ShouldBeSameAs(intent);
        warnings.Count.ShouldBe(0);
    }
}
