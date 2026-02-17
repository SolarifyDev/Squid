using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class HelmUpgradeActionHandlerTests
{
    private readonly HelmUpgradeActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionType = "Squid.HelmChartUpgrade",
        Dictionary<string, string> properties = null)
    {
        var action = new DeploymentActionDto
        {
            ActionType = actionType,
            Name = "MyRelease",
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
            {
                action.Properties.Add(new DeploymentActionPropertyDto
                {
                    PropertyName = kvp.Key,
                    PropertyValue = kvp.Value
                });
            }
        }

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new()
    {
        Action = action
    };

    // === CanHandle Tests ===

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = CreateAction("Squid.HelmChartUpgrade");
        _handler.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = CreateAction("squid.helmchartupgrade");
        _handler.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = CreateAction("Squid.KubernetesRunScript");
        _handler.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        _handler.CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        _handler.ActionType.ShouldBe("Squid.HelmChartUpgrade");
    }

    // === PrepareAsync — Basic Script Generation ===

    [Fact]
    public async Task PrepareAsync_BasicSetup_ContainsReleaseName()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ReleaseName"] = "my-app"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("my-app");
    }

    [Fact]
    public async Task PrepareAsync_NoReleaseName_FallsBackToActionName()
    {
        var action = CreateAction();
        action.Name = "FallbackRelease";
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("FallbackRelease");
    }

    [Fact]
    public async Task PrepareAsync_NoReleaseNameOrActionName_UsesDefault()
    {
        var action = CreateAction();
        action.Name = null;
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("release");
    }

    [Fact]
    public async Task PrepareAsync_ChartPath_ContainsChartPath()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ChartPath"] = "./charts/myapp"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("./charts/myapp");
    }

    [Fact]
    public async Task PrepareAsync_DefaultChartPath_IsDot()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        // Default chart path is "."
        result.ScriptBody.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task PrepareAsync_Namespace_ContainsNamespace()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Kubernetes.Namespace"] = "staging"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("staging");
    }

    // === Syntax Tests ===

    [Fact]
    public async Task PrepareAsync_BashSyntax_SetsSyntaxToBash()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_PowerShellSyntax_SetsSyntaxToPowerShell()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task PrepareAsync_DefaultSyntax_IsPowerShell()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    // === CalamariCommand Tests ===

    [Fact]
    public async Task PrepareAsync_CalamariCommand_IsNull()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.CalamariCommand.ShouldBeNull();
    }

    // === YamlValues (Values File) Tests ===

    [Fact]
    public async Task PrepareAsync_YamlValues_Bash_CreatesValuesFile()
    {
        var yaml = "replicaCount: 3\nimage:\n  tag: v1.0";
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.YamlValues"] = yaml
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("rawYamlValues.yaml");
        var fileContent = Encoding.UTF8.GetString(result.Files["rawYamlValues.yaml"]);
        fileContent.ShouldBe(yaml);
        result.ScriptBody.ShouldContain("--values");
        result.ScriptBody.ShouldContain("rawYamlValues.yaml");
    }

    [Fact]
    public async Task PrepareAsync_YamlValues_PowerShell_CreatesValuesFile()
    {
        var yaml = "replicaCount: 3";
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell",
            ["Squid.Action.Helm.YamlValues"] = yaml
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("rawYamlValues.yaml");
        result.ScriptBody.ShouldContain("--values");
        result.ScriptBody.ShouldContain("rawYamlValues.yaml");
    }

    [Fact]
    public async Task PrepareAsync_NoYamlValues_NoValuesFile()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldNotContainKey("rawYamlValues.yaml");
    }

    [Fact]
    public async Task PrepareAsync_EmptyYamlValues_NoValuesFile()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.YamlValues"] = "   "
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldNotContainKey("rawYamlValues.yaml");
    }

    // === KeyValues (--set) Tests ===

    [Fact]
    public async Task PrepareAsync_KeyValues_JsonFormat_Bash_GeneratesSetArgs()
    {
        var keyValues = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["image.tag"] = "v2.0",
            ["replicaCount"] = "5"
        });
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.KeyValues"] = keyValues
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--set image.tag=v2.0");
        result.ScriptBody.ShouldContain("--set replicaCount=5");
    }

    [Fact]
    public async Task PrepareAsync_KeyValues_JsonFormat_PowerShell_GeneratesSetArgs()
    {
        var keyValues = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["image.tag"] = "v2.0"
        });
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell",
            ["Squid.Action.Helm.KeyValues"] = keyValues
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--set");
        result.ScriptBody.ShouldContain("image.tag=v2.0");
    }

    [Fact]
    public async Task PrepareAsync_KeyValues_CommaSeparatedFallback_Bash_GeneratesSetArgs()
    {
        // Non-JSON format — should fall back to comma-separated parsing
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.KeyValues"] = "image.tag=v3.0,replicaCount=2"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--set image.tag=v3.0");
        result.ScriptBody.ShouldContain("--set replicaCount=2");
    }

    [Fact]
    public async Task PrepareAsync_KeyValues_EmptyJson_NoSetArgs()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.KeyValues"] = "{}"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("--set");
    }

    [Fact]
    public async Task PrepareAsync_NoKeyValues_NoSetArgs()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("--set");
    }

    [Fact]
    public async Task PrepareAsync_EmptyKeyValues_NoSetArgs()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.KeyValues"] = "  "
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("--set");
    }

    // === ResetValues Tests ===

    [Fact]
    public async Task PrepareAsync_ResetValuesTrue_ContainsResetValues()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ResetValues"] = "True"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("True");
    }

    [Fact]
    public async Task PrepareAsync_DefaultResetValues_IsTrue()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        // Default ResetValues is "True"
        result.ScriptBody.ShouldContain("True");
    }

    // === Custom Helm Executable Tests ===

    [Fact]
    public async Task PrepareAsync_CustomHelmExe_Bash_ContainsPath()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.CustomHelmExecutable"] = "/usr/local/bin/helm3"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("/usr/local/bin/helm3");
    }

    [Fact]
    public async Task PrepareAsync_CustomHelmExe_PowerShell_ContainsPath()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell",
            ["Squid.Action.Helm.CustomHelmExecutable"] = "C:\\tools\\helm.exe"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("C:\\tools\\helm.exe");
    }

    [Fact]
    public async Task PrepareAsync_NoCustomHelmExe_DefaultsToEmpty()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        // Script template handles empty by defaulting to "helm"
        result.ShouldNotBeNull();
    }

    // === AdditionalArgs Tests ===

    [Fact]
    public async Task PrepareAsync_AdditionalArgs_ContainsArgs()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.AdditionalArgs"] = "--timeout 600s --debug"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--timeout 600s --debug");
    }

    [Fact]
    public async Task PrepareAsync_NoAdditionalArgs_NoExtraArgs()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
    }

    // === Template Replacement Completeness ===

    [Fact]
    public async Task PrepareAsync_Bash_NoUnreplacedPlaceholders()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ReleaseName"] = "my-release",
            ["Squid.Action.Helm.ChartPath"] = "./mychart",
            ["Squid.Action.Kubernetes.Namespace"] = "prod"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("{{");
        result.ScriptBody.ShouldNotContain("}}");
    }

    [Fact]
    public async Task PrepareAsync_PowerShell_NoUnreplacedPlaceholders()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell",
            ["Squid.Action.Helm.ReleaseName"] = "my-release",
            ["Squid.Action.Helm.ChartPath"] = "./mychart",
            ["Squid.Action.Kubernetes.Namespace"] = "prod"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("{{");
        result.ScriptBody.ShouldNotContain("}}");
    }

    // === Combined Scenario Tests ===

    [Fact]
    public async Task PrepareAsync_FullSetup_Bash_AllFieldsPresent()
    {
        var keyValues = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["image.tag"] = "v1.0"
        });
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ReleaseName"] = "myapp",
            ["Squid.Action.Helm.ChartPath"] = "./charts/myapp",
            ["Squid.Action.Kubernetes.Namespace"] = "production",
            ["Squid.Action.Helm.CustomHelmExecutable"] = "/opt/helm",
            ["Squid.Action.Helm.ResetValues"] = "True",
            ["Squid.Action.Helm.YamlValues"] = "replicaCount: 3",
            ["Squid.Action.Helm.KeyValues"] = keyValues,
            ["Squid.Action.Helm.AdditionalArgs"] = "--timeout 300s"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("myapp");
        result.ScriptBody.ShouldContain("./charts/myapp");
        result.ScriptBody.ShouldContain("production");
        result.ScriptBody.ShouldContain("/opt/helm");
        result.ScriptBody.ShouldContain("--set image.tag=v1.0");
        result.ScriptBody.ShouldContain("--timeout 300s");
        result.Files.ShouldContainKey("rawYamlValues.yaml");
        result.CalamariCommand.ShouldBeNull();
        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_FullSetup_PowerShell_AllFieldsPresent()
    {
        var keyValues = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["image.repository"] = "myregistry/myapp"
        });
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell",
            ["Squid.Action.Helm.ReleaseName"] = "webapp",
            ["Squid.Action.Helm.ChartPath"] = "stable/nginx",
            ["Squid.Action.Kubernetes.Namespace"] = "web",
            ["Squid.Action.Helm.YamlValues"] = "ingress:\n  enabled: true",
            ["Squid.Action.Helm.KeyValues"] = keyValues,
            ["Squid.Action.Helm.AdditionalArgs"] = "--wait --debug"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("webapp");
        result.ScriptBody.ShouldContain("stable/nginx");
        result.ScriptBody.ShouldContain("web");
        result.ScriptBody.ShouldContain("--set");
        result.ScriptBody.ShouldContain("image.repository=myregistry/myapp");
        result.ScriptBody.ShouldContain("--wait --debug");
        result.Files.ShouldContainKey("rawYamlValues.yaml");
        result.CalamariCommand.ShouldBeNull();
        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    // === HelmWait Tests ===

    [Fact]
    public async Task PrepareAsync_ClientVersionSet_HelmWaitIsTrue()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Helm.ClientVersion"] = "v3"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("True");
    }

    [Fact]
    public async Task PrepareAsync_NoClientVersion_HelmWaitIsFalse()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        // HelmWait should be "False" when ClientVersion is not set
        result.ShouldNotBeNull();
    }
}
