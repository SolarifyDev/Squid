using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class HelmUpgradeActionHandlerTests
{
    private readonly HelmUpgradeActionHandler _handler = new();

    private static string B64(string value) => ShellEscapeHelper.Base64Encode(value);

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

    // === CanHandle Tests (default interface implementation) ===

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = CreateAction("Squid.HelmChartUpgrade");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = CreateAction("squid.helmchartupgrade");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = CreateAction("Squid.Script");
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        ((IActionHandler)_handler).CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = null };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.HelmChartUpgrade);
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

        result.ScriptBody.ShouldContain(B64("my-app"));
    }

    [Fact]
    public async Task PrepareAsync_NoReleaseName_FallsBackToActionName()
    {
        var action = CreateAction();
        action.Name = "FallbackRelease";
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("FallbackRelease"));
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

        result.ScriptBody.ShouldContain(B64("./charts/myapp"));
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

        result.ScriptBody.ShouldContain(B64("staging"));
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

        result.ScriptBody.ShouldContain("--set");
        result.ScriptBody.ShouldContain("image.tag=");
        result.ScriptBody.ShouldContain("replicaCount=");
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

        result.ScriptBody.ShouldContain("--set");
        result.ScriptBody.ShouldContain("image.tag=v3.0");
        result.ScriptBody.ShouldContain("replicaCount=2");
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

        result.ScriptBody.ShouldContain(B64("/usr/local/bin/helm3"));
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

        result.ScriptBody.ShouldContain(B64("C:\\tools\\helm.exe"));
    }

    [Fact]
    public async Task PrepareAsync_NoCustomHelmExe_ScriptDoesNotContainCustomPath()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("/usr/local/bin/helm3");
        result.ScriptBody.ShouldNotContain("C:\\tools\\helm.exe");
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

        result.ScriptBody.ShouldContain(B64("--timeout 600s --debug"));
    }

    [Fact]
    public async Task PrepareAsync_NoAdditionalArgs_ScriptDoesNotContainExtraFlags()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("--debug");
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

        result.ScriptBody.ShouldContain(B64("myapp"));
        result.ScriptBody.ShouldContain(B64("./charts/myapp"));
        result.ScriptBody.ShouldContain(B64("production"));
        result.ScriptBody.ShouldContain(B64("/opt/helm"));
        result.ScriptBody.ShouldContain("image.tag=");
        result.ScriptBody.ShouldContain(B64("--timeout 300s"));
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

        result.ScriptBody.ShouldContain(B64("webapp"));
        result.ScriptBody.ShouldContain(B64("stable/nginx"));
        result.ScriptBody.ShouldContain(B64("web"));
        result.ScriptBody.ShouldContain("--set");
        result.ScriptBody.ShouldContain("image.repository=");
        result.ScriptBody.ShouldContain(B64("--wait --debug"));
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

        result.ScriptBody.ShouldContain(B64("False"));
    }

    // === Security — No eval in Bash Template ===

    [Fact]
    public async Task PrepareAsync_Bash_DoesNotUseEval()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("eval ");
    }

    [Fact]
    public async Task PrepareAsync_Bash_UsesArrayExecution()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("HELM_CMD=(");
        result.ScriptBody.ShouldContain("\"${HELM_CMD[@]}\"");
    }

    [Fact]
    public async Task PrepareAsync_Bash_ReleaseNameWithSemicolon_NotInjectable()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ReleaseName"] = "release; rm -rf /"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("eval ");
        result.ScriptBody.ShouldContain("HELM_CMD=(");
    }

    [Fact]
    public async Task PrepareAsync_Bash_SetValueWithSpecialChars_Escaped()
    {
        var keyValues = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["password"] = "p@ss$word\"quote"
        });
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.KeyValues"] = keyValues
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("HELM_CMD+=(\"--set\"");
    }

    [Fact]
    public async Task PrepareAsync_Bash_ValuesFile_UsesArraySyntax()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.YamlValues"] = "key: value"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("HELM_CMD+=(\"--values\"");
    }

    [Fact]
    public async Task PrepareAsync_Bash_UsesEnvBashShebang()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("#!/usr/bin/env bash");
    }

    // === Wait/Timeout Tests ===

    [Fact]
    public async Task PrepareAsync_WaitEnabled_ContainsWaitFlag()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.Wait"] = "True"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("\"--wait\"");
    }

    [Fact]
    public async Task PrepareAsync_WaitForJobsEnabled_ContainsWaitForJobsFlag()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.WaitForJobs"] = "True"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("True"));
    }

    [Fact]
    public async Task PrepareAsync_TimeoutSet_ContainsTimeoutFlag()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.Timeout"] = "10m"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("10m"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task PrepareAsync_WaitAndWaitForJobsCombinations(bool wait, bool waitForJobs)
    {
        var props = new Dictionary<string, string> { ["Squid.Action.Script.Syntax"] = "Bash" };
        if (wait) props["Squid.Action.Helm.Wait"] = "True";
        if (waitForJobs) props["Squid.Action.Helm.WaitForJobs"] = "True";
        var action = CreateAction(properties: props);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var expectedWait = wait ? B64("True") : B64("False");
        var expectedWaitForJobs = waitForJobs ? B64("True") : B64("False");

        result.ScriptBody.ShouldContain($"b64d '{expectedWait}'");
        result.ScriptBody.ShouldContain($"b64d '{expectedWaitForJobs}'");
    }

    [Fact]
    public async Task PrepareAsync_LegacyHelmWait_BackwardsCompatible()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ClientVersion"] = "3"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("True"));
    }

    // === Multi-Source Values Tests ===

    [Fact]
    public async Task PrepareAsync_MultiSourceValues_InlineYaml_WritesValuesFile()
    {
        var valueSources = "[{\"Type\":\"InlineYaml\",\"Value\":\"replicas: 3\\nimage: nginx\"}]";
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ValueSources"] = valueSources
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("values-0.yaml");
        result.ScriptBody.ShouldContain("values-0.yaml");
    }

    [Fact]
    public async Task PrepareAsync_MultiSourceValues_KeyValues_EmitsSetFlags()
    {
        var valueSources = "[{\"Type\":\"KeyValues\",\"Value\":\"{\\\"app.name\\\":\\\"myapp\\\"}\"}]";
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ValueSources"] = valueSources
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--set");
        result.ScriptBody.ShouldContain("app.name");
    }

    [Fact]
    public async Task PrepareAsync_MultiSourceValues_MultipleInlineYaml_WritesMultipleFilesInOrder()
    {
        var valueSources = "[{\"Type\":\"InlineYaml\",\"Value\":\"a: 1\"},{\"Type\":\"InlineYaml\",\"Value\":\"b: 2\"}]";
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ValueSources"] = valueSources
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("values-0.yaml");
        result.Files.ShouldContainKey("values-1.yaml");
    }

    [Fact]
    public async Task PrepareAsync_NoValueSources_FallsBackToLegacyProperties()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.YamlValues"] = "legacy: true"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("rawYamlValues.yaml");
    }

    [Fact]
    public async Task PrepareAsync_EmptyValueSources_NoValuesFlags()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ValueSources"] = "[]"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldNotContainKey("rawYamlValues.yaml");
    }

    // === Feed Integration — Backward Compatibility ===

    private static HelmUpgradeActionHandler CreateHandlerWithFeed(ExternalFeed feed)
    {
        var mock = new Mock<IExternalFeedDataProvider>();
        mock.Setup(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        return new HelmUpgradeActionHandler(mock.Object);
    }

    private static ExternalFeed CreateHelmFeed(int id = 1, string feedUri = "https://charts.example.com", string username = null, string password = null)
    {
        return new ExternalFeed { Id = id, FeedUri = feedUri, Username = username, Password = password };
    }

    private static ActionExecutionContext CreateContextWithFeed(DeploymentActionDto action, string version = null)
    {
        var ctx = new ActionExecutionContext { Action = action };

        if (version != null)
        {
            ctx.SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = action.Name, Version = version }
            };
        }

        return ctx;
    }

    [Fact]
    public async Task PrepareAsync_NoFeedId_UsesChartPath()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Helm.ChartPath"] = "./local-chart"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("./local-chart"));
        result.ScriptBody.ShouldNotContain("squid-helm-repo");
    }

    [Fact]
    public async Task PrepareAsync_EmptyFeedId_UsesChartPath()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "",
            ["Squid.Action.Helm.ChartPath"] = "./local-chart"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("./local-chart"));
        result.ScriptBody.ShouldNotContain("squid-helm-repo");
    }

    [Fact]
    public async Task PrepareAsync_InvalidFeedId_UsesChartPath()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "not-a-number",
            ["Squid.Action.Helm.ChartPath"] = "./local-chart"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("./local-chart"));
    }

    [Fact]
    public async Task PrepareAsync_NullProvider_UsesChartPath()
    {
        var handler = new HelmUpgradeActionHandler(null);
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "mychart",
            ["Squid.Action.Helm.ChartPath"] = "./local-chart"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("./local-chart"));
        result.ScriptBody.ShouldNotContain("squid-helm-repo");
    }

    // === Feed Integration — Happy Path ===

    [Fact]
    public async Task PrepareAsync_FeedWithPackage_Bash_GeneratesRepoAddAndChartRef()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed(feedUri: "https://charts.example.com"));
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("squid-helm-repo/openclaw"));
        result.ScriptBody.ShouldContain("repo add squid-helm-repo");
        result.ScriptBody.ShouldContain(B64("https://charts.example.com"));
        result.ScriptBody.ShouldContain("repo update squid-helm-repo");
    }

    [Fact]
    public async Task PrepareAsync_FeedWithPackage_PowerShell_GeneratesRepoAddAndChartRef()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed(feedUri: "https://charts.example.com"));
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("squid-helm-repo/openclaw"));
        result.ScriptBody.ShouldContain("repo add squid-helm-repo");
        result.ScriptBody.ShouldContain("repo update squid-helm-repo");
    }

    [Fact]
    public async Task PrepareAsync_FeedOverridesChartPath()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw",
            ["Squid.Action.Helm.ChartPath"] = "./should-be-ignored"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("squid-helm-repo/openclaw"));
        result.ScriptBody.ShouldNotContain(B64("./should-be-ignored"));
    }

    // === Feed Integration — Version Pinning ===

    [Fact]
    public async Task PrepareAsync_FeedWithSelectedPackageVersion_Bash_AddsVersionFlag()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action, version: "2.1.0");

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("2.1.0"));
        result.ScriptBody.ShouldContain("--version");
    }

    [Fact]
    public async Task PrepareAsync_FeedWithVariableVersion_FallsBackToVariable()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);
        ctx.Variables = new List<VariableDto>
        {
            new() { Name = "Squid.Action.Package.PackageVersion", Value = "3.0.0" }
        };

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("3.0.0"));
    }

    [Fact]
    public async Task PrepareAsync_FeedWithNoVersion_EmptyChartVersion()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        // ChartVersion placeholder is replaced with empty B64 — runtime conditional won't fire
        result.ScriptBody.ShouldContain("b64d ''");
    }

    [Fact]
    public async Task PrepareAsync_NoFeed_EmptyChartVersion()
    {
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash"
        });
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        // ChartVersion placeholder is replaced with empty B64 — runtime conditional won't fire
        result.ScriptBody.ShouldContain("b64d ''");
    }

    // === Feed Integration — Credentials ===

    [Fact]
    public async Task PrepareAsync_FeedWithCredentials_Bash_IncludesAuthFlags()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed(username: "admin", password: "s3cret"));
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("admin"));
        result.ScriptBody.ShouldContain(B64("s3cret"));
        result.ScriptBody.ShouldContain("--username");
        result.ScriptBody.ShouldContain("--password");
    }

    [Fact]
    public async Task PrepareAsync_FeedWithCredentials_PowerShell_IncludesAuthFlags()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed(username: "admin", password: "s3cret"));
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "PowerShell",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("--username");
        result.ScriptBody.ShouldContain("--password");
    }

    [Fact]
    public async Task PrepareAsync_PublicFeed_NoAuthFlags()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("--username");
        result.ScriptBody.ShouldNotContain("--password");
    }

    // === Feed Integration — Error Handling ===

    [Fact]
    public async Task PrepareAsync_FeedNotFound_FallsBackToChartPath()
    {
        var mock = new Mock<IExternalFeedDataProvider>();
        mock.Setup(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((ExternalFeed)null);
        var handler = new HelmUpgradeActionHandler(mock.Object);
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "999",
            ["Squid.Action.Package.PackageId"] = "openclaw",
            ["Squid.Action.Helm.ChartPath"] = "./fallback"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("./fallback"));
        result.ScriptBody.ShouldNotContain("squid-helm-repo");
    }

    [Fact]
    public async Task PrepareAsync_FeedIdButNoPackageId_FallsBackToChartPath()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Helm.ChartPath"] = "./fallback"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("./fallback"));
        result.ScriptBody.ShouldNotContain("squid-helm-repo");
    }

    // === Feed Integration — Combined ===

    [Fact]
    public async Task PrepareAsync_FeedWithYamlValues_BothCoexist()
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw",
            ["Squid.Action.Helm.YamlValues"] = "replicas: 3"
        });
        var ctx = CreateContextWithFeed(action, version: "1.0.0");

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64("squid-helm-repo/openclaw"));
        result.ScriptBody.ShouldContain("--version");
        result.Files.ShouldContainKey("rawYamlValues.yaml");
        result.ScriptBody.ShouldContain("--values");
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public async Task PrepareAsync_FeedWithPackage_NoUnreplacedPlaceholders(string syntaxStr)
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed(username: "user", password: "pass"));
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = syntaxStr,
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw",
            ["Squid.Action.Helm.ReleaseName"] = "my-release",
            ["Squid.Action.Kubernetes.Namespace"] = "prod"
        });
        var ctx = CreateContextWithFeed(action, version: "2.0.0");

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldNotContain("{{");
        result.ScriptBody.ShouldNotContain("}}");
    }

    // === Feed Integration — Theory: Source Resolution ===

    [Theory]
    [InlineData(true, true, "squid-helm-repo/mychart")]
    [InlineData(true, false, ".")]
    [InlineData(false, true, ".")]
    [InlineData(false, false, "./custom")]
    public async Task PrepareAsync_ChartSourceResolution(bool hasFeedId, bool hasPackageId, string expectedChartPath)
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed());
        var props = new Dictionary<string, string> { ["Squid.Action.Script.Syntax"] = "Bash" };

        if (hasFeedId) props["Squid.Action.Package.FeedId"] = "1";
        if (hasPackageId) props["Squid.Action.Package.PackageId"] = "mychart";
        if (!hasFeedId && !hasPackageId) props["Squid.Action.Helm.ChartPath"] = "./custom";

        var action = CreateAction(properties: props);
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain(B64(expectedChartPath));
    }

    // === Feed Integration — Theory: Credential Combos ===

    [Theory]
    [InlineData("admin", "pass", true)]
    [InlineData("admin", "", false)]
    [InlineData("admin", null, false)]
    [InlineData("", "pass", false)]
    [InlineData(null, "pass", false)]
    [InlineData(null, null, false)]
    public async Task PrepareAsync_CredentialCombinations(string username, string password, bool expectAuth)
    {
        var handler = CreateHandlerWithFeed(CreateHelmFeed(username: username, password: password));
        var action = CreateAction(properties: new Dictionary<string, string>
        {
            ["Squid.Action.Script.Syntax"] = "Bash",
            ["Squid.Action.Package.FeedId"] = "1",
            ["Squid.Action.Package.PackageId"] = "openclaw"
        });
        var ctx = CreateContextWithFeed(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        if (expectAuth)
        {
            result.ScriptBody.ShouldContain("--username");
            result.ScriptBody.ShouldContain("--password");
        }
        else
        {
            result.ScriptBody.ShouldNotContain("--username");
            result.ScriptBody.ShouldNotContain("--password");
        }
    }
}
