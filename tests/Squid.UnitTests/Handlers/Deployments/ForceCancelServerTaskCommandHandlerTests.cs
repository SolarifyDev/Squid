using Squid.Core.Handlers.CommandHandlers.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.ServerTask;

namespace Squid.UnitTests.Handlers.Deployments;

public class ForceCancelServerTaskCommandHandlerTests
{
    [Fact]
    public async Task Handle_DelegatesToControlService()
    {
        var controlService = new Mock<IServerTaskControlService>();
        var handler = new ForceCancelServerTaskCommandHandler(controlService.Object);

        var command = new ForceCancelServerTaskCommand { TaskId = 42 };
        var context = new Mock<IReceiveContext<ForceCancelServerTaskCommand>>();
        context.Setup(c => c.Message).Returns(command);

        var result = await handler.Handle(context.Object, CancellationToken.None);

        result.ShouldNotBeNull();
        controlService.Verify(s => s.ForceCancelStuckTaskAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }
}
