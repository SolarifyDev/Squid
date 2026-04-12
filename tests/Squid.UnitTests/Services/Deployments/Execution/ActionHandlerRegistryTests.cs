using System.Linq;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Execution;

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
        mock.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>()))
            .Returns<DeploymentActionDto>(a => string.Equals(a?.ActionType, actionType, StringComparison.OrdinalIgnoreCase));
        return mock.Object;
    }

    [Fact]
    public void Resolve_MatchingHandler_ReturnsHandler()
    {
        var handler = CreateMockHandler("Squid.Script");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("Squid.Script"));

        result.ShouldBe(handler);
    }

    [Fact]
    public void Resolve_NoMatchingHandler_ReturnsNull()
    {
        var handler = CreateMockHandler("Squid.Script");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("Squid.UnknownAction"));

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_MultipleHandlers_ReturnsMatchingHandler()
    {
        var handler1 = CreateMockHandler("Squid.Script");
        var handler2 = CreateMockHandler("Squid.HelmChartUpgrade");
        var registry = new ActionHandlerRegistry(new[] { handler1, handler2 });

        var result = registry.Resolve(CreateAction("Squid.HelmChartUpgrade"));

        result.ShouldBe(handler2);
    }

    [Fact]
    public void Resolve_EmptyHandlers_ReturnsNull()
    {
        var registry = new ActionHandlerRegistry(Enumerable.Empty<IActionHandler>());

        var result = registry.Resolve(CreateAction("Squid.Script"));

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_NullAction_ReturnsNull()
    {
        var handler = CreateMockHandler("Squid.Script");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(null);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ActionWithNullActionType_ReturnsNull()
    {
        var handler = CreateMockHandler("Squid.Script");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction(null));

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ActionTypeCaseInsensitive_ReturnsMatchingHandler()
    {
        var handler = CreateMockHandler("Squid.Script");
        var registry = new ActionHandlerRegistry(new[] { handler });

        var result = registry.Resolve(CreateAction("squid.script"));

        result.ShouldBe(handler);
    }

    [Fact]
    public void Constructor_DuplicateActionType_Throws()
    {
        var handler1 = CreateMockHandler("Squid.Script");
        var handler2 = CreateMockHandler("Squid.Script");

        Should.Throw<ArgumentException>(() => new ActionHandlerRegistry(new[] { handler1, handler2 }));
    }

    [Fact]
    public void Resolve_HandlerCanHandleReturnsFalse_ReturnsNull()
    {
        var mock = new Mock<IActionHandler>();
        mock.Setup(h => h.ActionType).Returns("Squid.Script");
        mock.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(false);
        var registry = new ActionHandlerRegistry(new[] { mock.Object });

        var result = registry.Resolve(CreateAction("Squid.Script"));

        result.ShouldBeNull();
    }

    [Fact]
    public void ResolveScope_KnownHandler_ReturnsHandlerScope()
    {
        var mock = new Mock<IActionHandler>();
        mock.Setup(h => h.ActionType).Returns("Squid.Manual");
        mock.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        mock.Setup(h => h.ExecutionScope).Returns(ExecutionScope.StepLevel);
        var registry = new ActionHandlerRegistry(new[] { mock.Object });

        registry.ResolveScope(CreateAction("Squid.Manual")).ShouldBe(ExecutionScope.StepLevel);
    }

    [Fact]
    public void ResolveScope_UnknownAction_DefaultsToStepLevel()
    {
        var registry = new ActionHandlerRegistry(Enumerable.Empty<IActionHandler>());

        registry.ResolveScope(CreateAction("Squid.TentaclePackage")).ShouldBe(ExecutionScope.StepLevel);
    }
}
