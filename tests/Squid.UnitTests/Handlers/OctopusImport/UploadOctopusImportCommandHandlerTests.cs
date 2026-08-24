using System.IO;
using System.Net;
using System.Text;
using Mediator.Net.Contracts;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Handlers.OctopusImport;

public class UploadOctopusImportCommandHandlerTests
{
    [Fact]
    public async Task Handle_CreatesSessionStoresUploadAndReturnsCompletedSourceSummary()
    {
        var sessionService = new Mock<IOctopusImportSessionService>();
        var uploadStore = new Mock<IOctopusImportTemporaryUploadStore>();
        var sessionId = Guid.NewGuid();
        var preliminarySession = new OctopusImportSessionDto
        {
            SessionId = sessionId,
            DestinationSpaceId = 7,
            State = OctopusImportSessionState.Uploaded
        };
        var completedSession = new OctopusImportSessionDto
        {
            SessionId = sessionId,
            DestinationSpaceId = 7,
            State = OctopusImportSessionState.Uploaded,
            SourceSummary = new OctopusImportSourceSummaryDto
            {
                FileName = "export.zip",
                SizeBytes = 123,
                DetectedFormat = "Zip",
                Sha256 = "abc123"
            }
        };

        sessionService
            .Setup(s => s.CreateSessionAsync(7, It.IsAny<OctopusImportSourceSummaryDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(preliminarySession);
        uploadStore
            .Setup(s => s.SaveAsync(sessionId, "export.zip", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusImportTemporaryUpload("/tmp/export.zip", 123, "abc123"));
        sessionService
            .Setup(s => s.RegisterTemporaryUploadAsync(
                sessionId,
                7,
                It.IsAny<OctopusImportTemporaryUpload>(),
                It.IsAny<OctopusImportSourceSummaryDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedSession);

        var sut = new UploadOctopusImportCommandHandler(sessionService.Object, uploadStore.Object);
        var command = new UploadOctopusImportCommand
        {
            SpaceId = 7,
            FileName = "export.zip",
            ContentType = "application/zip",
            SizeBytes = 123,
            Content = new MemoryStream(Encoding.UTF8.GetBytes("zip"))
        };

        var response = await sut.Handle(Context(command), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.SourceSummary.Sha256.ShouldBe("abc123");
        sessionService.Verify(s => s.RegisterTemporaryUploadAsync(
            sessionId,
            7,
            It.IsAny<OctopusImportTemporaryUpload>(),
            It.Is<OctopusImportSourceSummaryDto>(summary =>
                summary.FileName == "export.zip" &&
                summary.SizeBytes == 123 &&
                summary.DetectedFormat == "Zip" &&
                summary.Sha256 == "abc123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenFileStreamIsMissing_ReturnsBadRequest()
    {
        var sut = new UploadOctopusImportCommandHandler(
            Mock.Of<IOctopusImportSessionService>(),
            Mock.Of<IOctopusImportTemporaryUploadStore>());

        var response = await sut.Handle(Context(new UploadOctopusImportCommand
        {
            SpaceId = 7,
            FileName = "export.zip",
            SizeBytes = 123
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static IReceiveContext<T> Context<T>(T message) where T : class, ICommand
    {
        var context = new Mock<IReceiveContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }
}
