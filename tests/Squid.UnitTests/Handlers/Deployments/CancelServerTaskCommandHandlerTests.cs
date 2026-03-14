using Mediator.Net.Context;
using Squid.Core.Handlers.CommandHandlers.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.ServerTask;

namespace Squid.UnitTests.Handlers.Deployments;

public class CancelServerTaskCommandHandlerTests
{
    [Fact]
    public async Task Handle_DelegatesToControlService()
    {
        var controlService = new Mock<IServerTaskControlService>();
        var handler = new CancelServerTaskCommandHandler(controlService.Object);

        var command = new CancelServerTaskCommand { TaskId = 42 };
        var context = new Mock<IReceiveContext<CancelServerTaskCommand>>();
        context.Setup(c => c.Message).Returns(command);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.ShouldNotBeNull();
        controlService.Verify(s => s.CancelTaskAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }
}
