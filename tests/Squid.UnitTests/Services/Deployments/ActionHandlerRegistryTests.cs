using System;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments;

public class ActionHandlerRegistryTests
{
    private static DeploymentActionDto CreateAction(string actionType) => new()
    {
        ActionType = actionType
    };

    private static IActionHandler CreateMockHandler(string actionType)
    {
        var mock = new Mock<IActionHandler>();
        mock.Setup(h => h.ActionType).Returns(actionType);
        mock.Setup(h => h.CanHandle(It.Is<DeploymentActionDto>(a =>
            string.Equals(a.ActionType, actionType, StringComparison.OrdinalIgnoreCase))))
            .Returns(true);
        return mock.Object;
    }

    [Fact]
    public void Resolve_MatchingHandler_ReturnsHandler()
    {
        var handler = CreateMockHandler("Squid.KubernetesRunScript");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("Squid.KubernetesRunScript"));

        result.ShouldBe(handler);
    }

    [Fact]
    public void Resolve_NoMatchingHandler_ReturnsNull()
    {
        var handler = CreateMockHandler("Squid.KubernetesRunScript");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("Squid.UnknownAction"));

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_MultipleHandlers_ReturnsFirstMatch()
    {
        var handler1 = CreateMockHandler("Squid.KubernetesRunScript");
        var handler2 = CreateMockHandler("Squid.HelmChartUpgrade");
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
        var handler = CreateMockHandler("Squid.KubernetesRunScript");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(null);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ActionWithNullActionType_ReturnsNull()
    {
        var handler = CreateMockHandler("Squid.KubernetesRunScript");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction(null));

        result.ShouldBeNull();
    }
}
