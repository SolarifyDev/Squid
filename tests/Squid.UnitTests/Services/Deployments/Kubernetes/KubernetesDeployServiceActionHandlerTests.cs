using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesDeployServiceActionHandlerTests
{
    private readonly KubernetesDeployServiceActionHandler _handler = new();

    [Fact]
    public void ActionType_ReturnsExpected()
    {
        _handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployService);
    }

    [Fact]
    public async Task PrepareAsync_ValidService_ReturnsResult()
    {
        var action = CreateAction("my-service", """[{"name":"http","port":80,"targetPort":"8080"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ScriptBody.ShouldContain("kubectl apply");
        result.Files.ShouldContainKey("service.yaml");
    }

    [Fact]
    public async Task PrepareAsync_MissingServiceName_ReturnsNull()
    {
        var action = CreateAction(null, """[{"name":"http","port":80}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_NoPorts_ReturnsNull()
    {
        var action = CreateAction("my-service", "[]");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_GeneratedYaml_HasCorrectStructure()
    {
        var action = CreateAction("my-service", """[{"name":"http","port":80,"targetPort":"8080"}]""");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result.Files["service.yaml"]);
        yaml.ShouldContain("apiVersion: v1");
        yaml.ShouldContain("kind: Service");
        yaml.ShouldContain("name: \"my-service\"");
        yaml.ShouldContain("ports:");
    }

    [Fact]
    public async Task PrepareAsync_WithNamespace_YamlIncludesNamespace()
    {
        var action = CreateAction("my-service", """[{"name":"http","port":80}]""");
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "prod-ns");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result.Files["service.yaml"]);
        yaml.ShouldContain("namespace: \"prod-ns\"");
    }

    [Fact]
    public async Task PrepareAsync_ResultProperties_AreCorrect()
    {
        var action = CreateAction("my-service", """[{"name":"http","port":80}]""");
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
        var action = new DeploymentActionDto { ActionType = SpecialVariables.ActionTypes.KubernetesDeployService };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesRunScript" };
        ((IActionHandler)_handler).CanHandle(action).ShouldBeFalse();
    }

    // === Helpers ===

    private static DeploymentActionDto CreateAction(string serviceName, string servicePorts)
    {
        var action = new DeploymentActionDto
        {
            ActionType = SpecialVariables.ActionTypes.KubernetesDeployService,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (serviceName != null)
            Add(action, "Squid.Action.KubernetesContainers.ServiceName", serviceName);

        if (servicePorts != null)
            Add(action, "Squid.Action.KubernetesContainers.ServicePorts", servicePorts);

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new() { Action = action };

    private static void Add(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto { PropertyName = name, PropertyValue = value });
    }
}
