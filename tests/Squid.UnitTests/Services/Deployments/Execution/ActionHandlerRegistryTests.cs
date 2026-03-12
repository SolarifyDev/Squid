using System;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ActionHandlerRegistryTests
{
    private static DeploymentActionDto CreateAction(string actionType) => new()
    {
        ActionType = actionType
    };

    private static IActionHandler CreateMockHandler(DeploymentActionType actionType)
    {
        var mock = new Mock<IActionHandler>();
        mock.Setup(h => h.ActionType).Returns(actionType);
        mock.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        mock.Setup(h => h.PrepareAsync(It.IsAny<ActionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionExecutionResult());
        return mock.Object;
    }

    [Fact]
    public void Resolve_MatchingHandler_ReturnsHandler()
    {
        var handler = CreateMockHandler(DeploymentActionType.KubernetesRunScript);
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("Squid.KubernetesRunScript"));

        result.ShouldBe(handler);
    }

    [Fact]
    public void Resolve_NoMatchingHandler_ReturnsNull()
    {
        var handler = CreateMockHandler(DeploymentActionType.KubernetesRunScript);
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("Squid.UnknownAction"));

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_MultipleHandlers_ReturnsMatchingHandler()
    {
        var handler1 = CreateMockHandler(DeploymentActionType.KubernetesRunScript);
        var handler2 = CreateMockHandler(DeploymentActionType.HelmChartUpgrade);
        var registry = new ActionHandlerRegistry(new[] { handler1, handler2 });

        var result = registry.Resolve(CreateAction("Squid.HelmChartUpgrade"));

        result.ShouldBe(handler2);
    }

    [Fact]
    public void Resolve_EmptyHandlers_ReturnsNull()
    {
        var registry = new ActionHandlerRegistry(Enumerable.Empty<IActionHandler>());

        var result = registry.Resolve(CreateAction("Squid.KubernetesRunScript"));

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_NullAction_ReturnsNull()
    {
        var handler = CreateMockHandler(DeploymentActionType.KubernetesRunScript);
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(null);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ActionWithNullActionType_ReturnsNull()
    {
        var handler = CreateMockHandler(DeploymentActionType.KubernetesRunScript);
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction(null));

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ActionTypeCaseInsensitive_ReturnsMatchingHandler()
    {
        var handler = CreateMockHandler(DeploymentActionType.KubernetesRunScript);
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("squid.kubernetesrunscript"));

        result.ShouldBe(handler);
    }

    [Fact]
    public void Constructor_DuplicateActionType_Throws()
    {
        var handler1 = CreateMockHandler(DeploymentActionType.KubernetesRunScript);
        var handler2 = CreateMockHandler(DeploymentActionType.KubernetesRunScript);

        Should.Throw<ArgumentException>(() => new ActionHandlerRegistry(new[] { handler1, handler2 }));
    }
}
