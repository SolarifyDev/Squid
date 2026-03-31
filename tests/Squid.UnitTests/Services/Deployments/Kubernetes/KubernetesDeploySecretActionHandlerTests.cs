using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesDeploySecretActionHandlerTests
{
    private readonly KubernetesDeploySecretActionHandler _handler = new();

    [Fact]
    public void ActionType_ReturnsExpected()
    {
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeploySecret);
    }

    [Fact]
    public async Task PrepareAsync_ValidSecret_ReturnsResult()
    {
        var action = CreateAction("my-secret", """[{"Key":"DB_PASS","Value":"s3cret"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ScriptBody.ShouldContain("kubectl apply");
        result.Files.ShouldContainKey("secret.yaml");
    }

    [Fact]
    public async Task PrepareAsync_MissingSecretName_ReturnsNull()
    {
        var action = CreateAction(null, """[{"Key":"DB_PASS","Value":"s3cret"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_EmptySecretValues_ReturnsNull()
    {
        var action = CreateAction("my-secret", "[]");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_GeneratedYaml_HasCorrectStructure()
    {
        var action = CreateAction("my-secret", """[{"Key":"TOKEN","Value":"abc123"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result.Files["secret.yaml"]);
        yaml.ShouldContain("apiVersion: v1");
        yaml.ShouldContain("kind: Secret");
        yaml.ShouldContain("name: \"my-secret\"");
        yaml.ShouldContain("stringData:");
    }

    [Fact]
    public async Task PrepareAsync_WithNamespace_YamlIncludesNamespace()
    {
        var action = CreateAction("my-secret", """[{"Key":"K","Value":"V"}]""");
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "prod-ns");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result.Files["secret.yaml"]);
        yaml.ShouldContain("namespace: \"prod-ns\"");
    }

    [Fact]
    public async Task PrepareAsync_ResultProperties_AreCorrect()
    {
        var action = CreateAction("my-secret", """[{"Key":"K","Value":"V"}]""");
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
        var action = new DeploymentActionDto { ActionType = SpecialVariables.ActionTypes.KubernetesDeploySecret };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.Script" };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    // === Helpers ===

    private static DeploymentActionDto CreateAction(string secretName, string secretValues)
    {
        var action = new DeploymentActionDto
        {
            ActionType = SpecialVariables.ActionTypes.KubernetesDeploySecret,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (secretName != null)
            Add(action, "Squid.Action.KubernetesContainers.SecretName", secretName);

        if (secretValues != null)
            Add(action, "Squid.Action.KubernetesContainers.SecretValues", secretValues);

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new() { Action = action };

    private static void Add(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto { PropertyName = name, PropertyValue = value });
    }
}
