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

        var passwordValidator = new Mock<IOctopusExportPasswordValidator>();
        passwordValidator
            .Setup(v => v.ValidateAsync(It.IsAny<Stream>(), "export.zip", "octopus-password", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.Valid));

        var sut = new UploadOctopusImportCommandHandler(sessionService.Object, uploadStore.Object, passwordValidator.Object);
        var command = new UploadOctopusImportCommand
        {
            SpaceId = 7,
            Password = "octopus-password",
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
            Mock.Of<IOctopusImportTemporaryUploadStore>(),
            Mock.Of<IOctopusExportPasswordValidator>());

        var response = await sut.Handle(Context(new UploadOctopusImportCommand
        {
            SpaceId = 7,
            FileName = "export.zip",
            SizeBytes = 123
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Handle_WhenPasswordIsMissing_ReturnsBadRequest()
    {
        var sut = new UploadOctopusImportCommandHandler(
            Mock.Of<IOctopusImportSessionService>(),
            Mock.Of<IOctopusImportTemporaryUploadStore>(),
            Mock.Of<IOctopusExportPasswordValidator>());

        var response = await sut.Handle(Context(new UploadOctopusImportCommand
        {
            SpaceId = 7,
            FileName = "export.zip",
            SizeBytes = 123,
            Content = new MemoryStream(Encoding.UTF8.GetBytes("zip"))
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Msg.ShouldBe("Octopus import upload requires a password.");
    }

    [Fact]
    public async Task Handle_WhenPasswordCannotDecryptExport_ReturnsBadRequestBeforeCreatingSession()
    {
        var sessionService = new Mock<IOctopusImportSessionService>();
        var uploadStore = new Mock<IOctopusImportTemporaryUploadStore>();
        var passwordValidator = new Mock<IOctopusExportPasswordValidator>();
        passwordValidator
            .Setup(v => v.ValidateAsync(It.IsAny<Stream>(), "export.zip", "wrong-password", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.Invalid));

        var sut = new UploadOctopusImportCommandHandler(sessionService.Object, uploadStore.Object, passwordValidator.Object);
        var response = await sut.Handle(Context(new UploadOctopusImportCommand
        {
            SpaceId = 7,
            Password = "wrong-password",
            FileName = "export.zip",
            SizeBytes = 123,
            Content = new MemoryStream(Encoding.UTF8.GetBytes("zip"))
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Msg.ShouldBe("The password provided was incorrect, and did not match the password used\nwhen the data was exported.");
        sessionService.Verify(s => s.CreateSessionAsync(
            It.IsAny<int>(),
            It.IsAny<OctopusImportSourceSummaryDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        uploadStore.Verify(s => s.SaveAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<Stream>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IReceiveContext<T> Context<T>(T message) where T : class, ICommand
    {
        var context = new Mock<IReceiveContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }
}
