using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesYamlActionHandlerTests
{
    private static DeploymentActionDto CreateAction(string actionType = "Squid.KubernetesDeployContainers") => new()
    {
        ActionType = actionType,
        Properties = new List<DeploymentActionPropertyDto>()
    };

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new()
    {
        Action = action,
        Step = new DeploymentStepDto()
    };

    private static Mock<IActionYamlGenerator> CreateMockGenerator(bool canHandle, Dictionary<string, byte[]> yamlFiles = null)
    {
        var mock = new Mock<IActionYamlGenerator>();
        mock.Setup(g => g.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(canHandle);
        mock.Setup(g => g.GenerateAsync(
                It.IsAny<DeploymentStepDto>(),
                It.IsAny<DeploymentActionDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(yamlFiles ?? new Dictionary<string, byte[]>());
        return mock;
    }

    // === CanHandle Tests ===

    [Fact]
    public void CanHandle_GeneratorMatches_ReturnsTrue()
    {
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        handler.CanHandle(CreateAction()).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_NoGeneratorMatches_ReturnsFalse()
    {
        var generator = CreateMockGenerator(canHandle: false);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        handler.CanHandle(CreateAction()).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        var generator = CreateMockGenerator(canHandle: false);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        handler.CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_EmptyGenerators_ReturnsFalse()
    {
        var handler = new KubernetesYamlActionHandler(Enumerable.Empty<IActionYamlGenerator>());

        handler.CanHandle(CreateAction()).ShouldBeFalse();
    }

    // === PrepareAsync Tests ===

    [Fact]
    public async Task PrepareAsync_GeneratorFound_ReturnsDirectScriptResult()
    {
        var yamlFiles = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = System.Text.Encoding.UTF8.GetBytes("apiVersion: v1")
        };
        var generator = CreateMockGenerator(canHandle: true, yamlFiles: yamlFiles);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.CalamariCommand.ShouldBeNull();
        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        result.PayloadKind.ShouldBe(PayloadKind.None);
        result.Files.ShouldContainKey("deployment.yaml");
        result.ScriptBody.ShouldContain("kubectl apply");
        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_NoGeneratorFound_ReturnsNull()
    {
        var generator = CreateMockGenerator(canHandle: false);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_GeneratorReturnsNullFiles_ReturnsEmptyFiles()
    {
        var mock = new Mock<IActionYamlGenerator>();
        mock.Setup(g => g.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        mock.Setup(g => g.GenerateAsync(
                It.IsAny<DeploymentStepDto>(),
                It.IsAny<DeploymentActionDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dictionary<string, byte[]>)null);

        var handler = new KubernetesYamlActionHandler(new[] { mock.Object });

        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_MultipleGenerators_UsesFirstMatch()
    {
        var yamlFiles1 = new Dictionary<string, byte[]>
        {
            ["first.yaml"] = System.Text.Encoding.UTF8.GetBytes("first")
        };
        var yamlFiles2 = new Dictionary<string, byte[]>
        {
            ["second.yaml"] = System.Text.Encoding.UTF8.GetBytes("second")
        };

        var generator1 = CreateMockGenerator(canHandle: true, yamlFiles: yamlFiles1);
        var generator2 = CreateMockGenerator(canHandle: true, yamlFiles: yamlFiles2);

        var handler = new KubernetesYamlActionHandler(new[] { generator1.Object, generator2.Object });

        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("first.yaml");
        result.Files.ShouldNotContainKey("second.yaml");
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        var handler = new KubernetesYamlActionHandler(Enumerable.Empty<IActionYamlGenerator>());

        handler.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployContainers);
    }

    [Fact]
    public async Task PrepareAsync_CallsGenerateAsync_WithCorrectParameters()
    {
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var action = CreateAction();
        var step = new DeploymentStepDto { Id = 42 };
        var ctx = new ActionExecutionContext { Action = action, Step = step };

        await handler.PrepareAsync(ctx, CancellationToken.None);

        generator.Verify(g => g.GenerateAsync(step, action, It.IsAny<CancellationToken>()), Times.Once);
    }

    // === InjectDeploymentIdSuffix Tests ===

    [Fact]
    public async Task PrepareAsync_WithDeploymentIdVariable_InjectsSuffixProperty()
    {
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var action = CreateAction();
        action.Id = 1;
        var ctx = CreateContext(action);
        ctx.Variables = new List<VariableDto>
        {
            new() { Name = SpecialVariables.Deployment.Id, Value = "Deployments-42" }
        };

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var suffixProp = action.Properties.FirstOrDefault(p => p.PropertyName == "Squid.Internal.DeploymentIdSuffix");
        suffixProp.ShouldNotBeNull();
        suffixProp.PropertyValue.ShouldBe("deployments-42");
    }

    [Fact]
    public async Task PrepareAsync_NoDeploymentIdVariable_NothingInjected()
    {
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var action = CreateAction();
        var ctx = CreateContext(action);
        ctx.Variables = new List<VariableDto>();

        await handler.PrepareAsync(ctx, CancellationToken.None);

        action.Properties.ShouldNotContain(p => p.PropertyName == "Squid.Internal.DeploymentIdSuffix");
    }

    // === DirectScript Mode Tests ===

    [Fact]
    public async Task PrepareAsync_MultipleYamlFiles_AllApplied()
    {
        var yamlFiles = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = System.Text.Encoding.UTF8.GetBytes("kind: Deployment"),
            ["service.yaml"] = System.Text.Encoding.UTF8.GetBytes("kind: Service"),
            ["configmap.yaml"] = System.Text.Encoding.UTF8.GetBytes("kind: ConfigMap")
        };
        var generator = CreateMockGenerator(canHandle: true, yamlFiles: yamlFiles);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var result = await handler.PrepareAsync(CreateContext(CreateAction()), CancellationToken.None);

        result.ScriptBody.ShouldContain("./configmap.yaml");
        result.ScriptBody.ShouldContain("./deployment.yaml");
        result.ScriptBody.ShouldContain("./service.yaml");
    }

    [Fact]
    public async Task PrepareAsync_BlueGreen_ScriptBodyContainsShellCommands()
    {
        var yamlFiles = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = System.Text.Encoding.UTF8.GetBytes("kind: Deployment"),
            ["bluegreen-switch.sh"] = System.Text.Encoding.UTF8.GetBytes("kubectl patch service"),
            ["bluegreen-scaledown.sh"] = System.Text.Encoding.UTF8.GetBytes("kubectl scale deployment")
        };
        var generator = CreateMockGenerator(canHandle: true, yamlFiles: yamlFiles);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var result = await handler.PrepareAsync(CreateContext(CreateAction()), CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl patch service");
        result.ScriptBody.ShouldContain("kubectl scale deployment");
        result.Files.ShouldNotContainKey("bluegreen-switch.sh");
        result.Files.ShouldNotContainKey("bluegreen-scaledown.sh");
        result.Files.ShouldContainKey("deployment.yaml");
    }

    [Fact]
    public async Task PrepareAsync_ObjectStatusCheck_ScriptBodyContainsWaitCommands()
    {
        var yamlFiles = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = System.Text.Encoding.UTF8.GetBytes("apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: test-deploy")
        };
        var generator = CreateMockGenerator(canHandle: true, yamlFiles: yamlFiles);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object });

        var action = CreateAction();
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = "Squid.Action.KubernetesContainers.ObjectStatusCheck",
            PropertyValue = "True"
        });

        var result = await handler.PrepareAsync(CreateContext(action), CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl rollout status");
        result.ScriptBody.ShouldContain("test-deploy");
    }

    // === GenerateSecretYaml Escaping Tests ===

    [Fact]
    public void GenerateSecretYaml_SpecialCharsInName_Escaped()
    {
        var yaml = KubernetesYamlActionHandler.GenerateSecretYaml("my \"secret\"", "ns: test", "{\"auth\":{}}");

        yaml.ShouldContain("name: \"my \\\"secret\\\"\"");
        yaml.ShouldContain("namespace: \"ns: test\"");
    }

    [Fact]
    public void GenerateSecretYaml_NormalValues_Escaped()
    {
        var yaml = KubernetesYamlActionHandler.GenerateSecretYaml("my-secret", "default", "{\"auth\":{}}");

        yaml.ShouldContain("name: \"my-secret\"");
        yaml.ShouldContain("namespace: \"default\"");
    }
}
