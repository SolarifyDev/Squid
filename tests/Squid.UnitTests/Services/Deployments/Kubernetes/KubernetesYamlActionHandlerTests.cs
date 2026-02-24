using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

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
    public async Task PrepareAsync_GeneratorFound_ReturnsCalamariResult()
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
        result.CalamariCommand.ShouldBe("calamari-kubernetes-deploy");
        result.ExecutionMode.ShouldBe(ExecutionMode.PackagedPayload);
        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Unspecified);
        result.ResolveContextPreparationPolicy().ShouldBe(ContextPreparationPolicy.Skip);
        result.PayloadKind.ShouldBe(PayloadKind.YamlBundle);
        result.Files.ShouldContainKey("deployment.yaml");
        result.Syntax.ShouldBe(ScriptSyntax.PowerShell);
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

        handler.ActionType.ShouldBe(DeploymentActionType.KubernetesDeployContainers);
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
}
