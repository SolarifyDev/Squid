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

    private static IReceiveContext<T> Context<T>(T message) where T : class, IRequest
    {
        var context = new Mock<IReceiveContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }
}
