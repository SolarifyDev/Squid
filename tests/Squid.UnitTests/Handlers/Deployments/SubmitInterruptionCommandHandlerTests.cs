using System.Collections.Generic;
using Squid.Core.Handlers.CommandHandlers.Deployments.Interruption;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.Interruption;

namespace Squid.UnitTests.Handlers.Deployments;

public class SubmitInterruptionCommandHandlerTests
{
    private readonly Mock<IDeploymentInterruptionService> _interruptionService = new();
    private readonly Mock<IServerTaskControlService> _controlService = new();
    private readonly SubmitInterruptionCommandHandler _sut;

    public SubmitInterruptionCommandHandlerTests()
    {
        _sut = new SubmitInterruptionCommandHandler(_interruptionService.Object, _controlService.Object);
    }

    [Fact]
    public async Task Handle_SubmitsAndTriggersAutoResume()
    {
        var interruption = new DeploymentInterruption { Id = 5, ServerTaskId = 10 };
        _interruptionService.Setup(s => s.GetInterruptionByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(interruption);

        var command = new SubmitInterruptionCommand { InterruptionId = 5, Values = new Dictionary<string, string> { ["Guidance"] = "Proceed" } };
        var context = new Mock<IReceiveContext<SubmitInterruptionCommand>>();
        context.Setup(c => c.Message).Returns(command);

        var result = await _sut.Handle(context.Object, CancellationToken.None);

        result.ShouldNotBeNull();
        _interruptionService.Verify(s => s.SubmitInterruptionAsync(5, command.Values, It.IsAny<CancellationToken>()), Times.Once);
        _controlService.Verify(s => s.TryAutoResumeAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_InterruptionNotFound_SkipsAutoResume()
    {
        _interruptionService.Setup(s => s.GetInterruptionByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeploymentInterruption)null);

        var command = new SubmitInterruptionCommand { InterruptionId = 99, Values = new Dictionary<string, string>() };
        var context = new Mock<IReceiveContext<SubmitInterruptionCommand>>();
        context.Setup(c => c.Message).Returns(command);

        await _sut.Handle(context.Object, CancellationToken.None);

        _interruptionService.Verify(s => s.SubmitInterruptionAsync(99, It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _controlService.Verify(s => s.TryAutoResumeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
