using System.Net;
using Mediator.Net.Contracts;
using Squid.Core.Handlers.RequestHandlers.OctopusImport;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.UnitTests.Handlers.OctopusImport;

public class GetOctopusImportStatusRequestHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsCurrentSessionSnapshot()
    {
        var sessionId = Guid.NewGuid();
        var session = new OctopusImportSessionDto
        {
            SessionId = sessionId,
            DestinationSpaceId = 7,
            State = OctopusImportSessionState.Validated
        };
        var sessionService = new Mock<IOctopusImportSessionService>();
        sessionService
            .Setup(s => s.GetSessionAsync(sessionId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var sut = new GetOctopusImportStatusRequestHandler(sessionService.Object);
        var response = await sut.Handle(Context(new GetOctopusImportStatusRequest
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
    }

    [Fact]
    public async Task Handle_ReturnsTerminalResultForIdempotentStatusReads()
    {
        var sessionId = Guid.NewGuid();
        var session = new OctopusImportSessionDto
        {
            SessionId = sessionId,
            DestinationSpaceId = 7,
            State = OctopusImportSessionState.Succeeded,
            Result = new OctopusImportSessionResultDto { Succeeded = true }
        };
        var sessionService = new Mock<IOctopusImportSessionService>();
        sessionService
            .Setup(s => s.GetSessionAsync(sessionId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var sut = new GetOctopusImportStatusRequestHandler(sessionService.Object);
        var response = await sut.Handle(Context(new GetOctopusImportStatusRequest
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.ShouldBeSameAs(session);
        response.Data.Session.Result.Succeeded.ShouldBeTrue();
        sessionService.Verify(s => s.GetSessionAsync(sessionId, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithoutDestinationSpaceContext_RejectsBeforeSessionLookup()
    {
        var sessionService = new Mock<IOctopusImportSessionService>();
        var sut = new GetOctopusImportStatusRequestHandler(sessionService.Object);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => sut.Handle(Context(new GetOctopusImportStatusRequest
            {
                SessionId = Guid.NewGuid()
            }), CancellationToken.None));

        sessionService.Verify(
            s => s.GetSessionAsync(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static IReceiveContext<T> Context<T>(T message) where T : class, IRequest
    {
        var context = new Mock<IReceiveContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }
}
