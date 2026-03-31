using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesDeployConfigMapActionHandlerTests
{
    private readonly KubernetesDeployConfigMapActionHandler _handler = new();

    [Fact]
    public void ActionType_ReturnsExpected()
    {
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployConfigMap);
    }

    [Fact]
    public async Task PrepareAsync_ValidConfigMap_ReturnsResult()
    {
        var action = CreateAction("my-config", """[{"Key":"APP_ENV","Value":"production"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ScriptBody.ShouldContain("kubectl apply");
        result.Files.ShouldContainKey("configmap.yaml");
    }

    [Fact]
    public async Task PrepareAsync_MissingConfigMapName_ReturnsNull()
    {
        var action = CreateAction(null, """[{"Key":"APP_ENV","Value":"production"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_EmptyConfigMapValues_ReturnsNull()
    {
        var action = CreateAction("my-config", "[]");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_GeneratedYaml_HasCorrectStructure()
    {
        var action = CreateAction("my-config", """[{"Key":"DB_HOST","Value":"localhost"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result.Files["configmap.yaml"]);
        yaml.ShouldContain("apiVersion: v1");
        yaml.ShouldContain("kind: ConfigMap");
        yaml.ShouldContain("name: \"my-config\"");
        yaml.ShouldContain("data:");
    }

    [Fact]
    public async Task PrepareAsync_WithNamespace_YamlIncludesNamespace()
    {
        var action = CreateAction("my-config", """[{"Key":"K","Value":"V"}]""");
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "prod-ns");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result.Files["configmap.yaml"]);
        yaml.ShouldContain("namespace: \"prod-ns\"");
    }

    [Fact]
    public async Task PrepareAsync_ResultProperties_AreCorrect()
    {
        var action = CreateAction("my-config", """[{"Key":"K","Value":"V"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        result.PayloadKind.ShouldBe(PayloadKind.None);
        result.Syntax.ShouldBe(ScriptSyntax.Bash);
        result.CalamariCommand.ShouldBeNull();
    }

    // === CanHandle ===

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = new DeploymentActionDto { ActionType = SpecialVariables.ActionTypes.KubernetesDeployConfigMap };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.Script" };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    // === Helpers ===

    private static DeploymentActionDto CreateAction(string configMapName, string configMapValues)
    {
        var action = new DeploymentActionDto
        {
            ActionType = SpecialVariables.ActionTypes.KubernetesDeployConfigMap,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (configMapName != null)
            Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", configMapName);

        if (configMapValues != null)
            Add(action, "Squid.Action.KubernetesContainers.ConfigMapValues", configMapValues);

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new() { Action = action };

    private static void Add(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto { PropertyName = name, PropertyValue = value });
    }
}
