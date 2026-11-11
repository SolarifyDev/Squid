using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Mediator.Net.Contracts;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
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

    [Theory]
    [InlineData(OctopusExportPasswordValidationStatus.Valid, true)]
    [InlineData(OctopusExportPasswordValidationStatus.NotVerifiable, false)]
    public async Task Handle_RetainsTheExportPasswordOnlyWhenItWasProvenAgainstTheArchive(OctopusExportPasswordValidationStatus status, bool retained)
    {
        var sessionId = Guid.NewGuid();
        var sessionService = new Mock<IOctopusImportSessionService>();
        var uploadStore = new Mock<IOctopusImportTemporaryUploadStore>();
        var passwordValidator = new Mock<IOctopusExportPasswordValidator>();
        var secretStore = new Mock<IOctopusImportExportSecretStore>();

        sessionService.Setup(s => s.CreateSessionAsync(7, It.IsAny<OctopusImportSourceSummaryDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, DestinationSpaceId = 7 });
        sessionService.Setup(s => s.RegisterTemporaryUploadAsync(sessionId, 7, It.IsAny<OctopusImportTemporaryUpload>(), It.IsAny<OctopusImportSourceSummaryDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, DestinationSpaceId = 7 });
        uploadStore.Setup(s => s.SaveAsync(sessionId, "export.zip", It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportTemporaryUpload("/tmp/export.zip", 3, "abc"));
        passwordValidator.Setup(v => v.ValidateAsync(It.IsAny<Stream>(), "export.zip", "octopus-password", It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusExportPasswordValidationResult(status));

        var sut = new UploadOctopusImportCommandHandler(sessionService.Object, uploadStore.Object, passwordValidator.Object, null, secretStore.Object);

        var response = await sut.Handle(Context(new UploadOctopusImportCommand { SpaceId = 7, Password = "octopus-password", FileName = "export.zip", SizeBytes = 3, Content = new MemoryStream(Encoding.UTF8.GetBytes("zip")) }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        secretStore.Verify(s => s.StoreAsync(sessionId, 7, "octopus-password", It.IsAny<CancellationToken>()), retained ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("concurrency")]
    [InlineData("database")]
    [InlineData("cancelled")]
    public async Task Handle_RetainingThePasswordFails_FailsTheSessionInsteadOfLeavingAnImportableOrphan(string failure)
    {
        var sessionId = Guid.NewGuid();
        var (sessionService, sut, secretStore) = RetentionScenario(sessionId);
        using var cts = new CancellationTokenSource();
        Exception thrown = failure switch
        {
            "concurrency" => new OctopusImportSessionConcurrencyException(sessionId),
            "database" => new InvalidOperationException("connection reset"),
            _ => new OperationCanceledException(cts.Token)
        };
        secretStore.Setup(s => s.StoreAsync(sessionId, 7, "octopus-password", It.IsAny<CancellationToken>())).Callback(() => cts.Cancel()).ThrowsAsync(thrown);

        var actual = await Should.ThrowAsync<Exception>(() => sut.Handle(Context(UploadCommand()), cts.Token));

        actual.ShouldBeAssignableTo(thrown.GetType(), "the original failure must surface, not the compensation");
        sessionService.Verify(s => s.RecordResultAsync(sessionId, 7, OctopusImportSessionState.Failed, It.Is<OctopusImportSessionResultDto>(r => r.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.UploadAbandoned)), It.Is<CancellationToken>(t => !t.IsCancellationRequested)), Times.Once);
    }

    [Fact]
    public async Task Handle_RetainingThePasswordFailsAndSoDoesTheCompensation_SurfacesTheOriginalFailure()
    {
        var sessionId = Guid.NewGuid();
        var (sessionService, sut, secretStore) = RetentionScenario(sessionId);
        var original = new InvalidOperationException("store failed");
        secretStore.Setup(s => s.StoreAsync(sessionId, 7, "octopus-password", It.IsAny<CancellationToken>())).ThrowsAsync(original);
        sessionService.Setup(s => s.RecordResultAsync(sessionId, 7, OctopusImportSessionState.Failed, It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        var actual = await Should.ThrowAsync<InvalidOperationException>(() => sut.Handle(Context(UploadCommand()), CancellationToken.None));

        actual.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task Handle_PasswordRetained_NeverFailsTheSession()
    {
        var sessionId = Guid.NewGuid();
        var (sessionService, sut, _) = RetentionScenario(sessionId);

        var response = await sut.Handle(Context(UploadCommand()), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        sessionService.Verify(s => s.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OctopusImportSessionState>(), It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (Mock<IOctopusImportSessionService> SessionService, UploadOctopusImportCommandHandler Sut, Mock<IOctopusImportExportSecretStore> SecretStore) RetentionScenario(Guid sessionId)
    {
        var sessionService = new Mock<IOctopusImportSessionService>();
        var uploadStore = new Mock<IOctopusImportTemporaryUploadStore>();
        var passwordValidator = new Mock<IOctopusExportPasswordValidator>();
        var secretStore = new Mock<IOctopusImportExportSecretStore>();

        sessionService.Setup(s => s.CreateSessionAsync(7, It.IsAny<OctopusImportSourceSummaryDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, DestinationSpaceId = 7 });
        sessionService.Setup(s => s.RegisterTemporaryUploadAsync(sessionId, 7, It.IsAny<OctopusImportTemporaryUpload>(), It.IsAny<OctopusImportSourceSummaryDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, DestinationSpaceId = 7 });
        uploadStore.Setup(s => s.SaveAsync(sessionId, "export.zip", It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportTemporaryUpload("/tmp/export.zip", 3, "abc"));
        passwordValidator.Setup(v => v.ValidateAsync(It.IsAny<Stream>(), "export.zip", "octopus-password", It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.Valid));

        return (sessionService, new UploadOctopusImportCommandHandler(sessionService.Object, uploadStore.Object, passwordValidator.Object, null, secretStore.Object), secretStore);
    }

    private static UploadOctopusImportCommand UploadCommand()
        => new() { SpaceId = 7, Password = "octopus-password", FileName = "export.zip", SizeBytes = 3, Content = new MemoryStream(Encoding.UTF8.GetBytes("zip")) };

    [Fact]
    public async Task Handle_WrongPassword_NeverRetainsIt()
    {
        var passwordValidator = new Mock<IOctopusExportPasswordValidator>();
        var secretStore = new Mock<IOctopusImportExportSecretStore>();
        passwordValidator.Setup(v => v.ValidateAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.Invalid));

        var sut = new UploadOctopusImportCommandHandler(Mock.Of<IOctopusImportSessionService>(), Mock.Of<IOctopusImportTemporaryUploadStore>(), passwordValidator.Object, null, secretStore.Object);

        var response = await sut.Handle(Context(new UploadOctopusImportCommand { SpaceId = 7, Password = "wrong", FileName = "export.zip", SizeBytes = 3, Content = new MemoryStream(Encoding.UTF8.GetBytes("zip")) }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        secretStore.VerifyNoOtherCalls();
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
    public async Task Handle_WhenConfiguredUploadLimitIsExceeded_RejectsBeforeValidationOrPersistence()
    {
        var sessionService = new Mock<IOctopusImportSessionService>();
        var uploadStore = new Mock<IOctopusImportTemporaryUploadStore>();
        var passwordValidator = new Mock<IOctopusExportPasswordValidator>();
        var sut = new UploadOctopusImportCommandHandler(
            sessionService.Object,
            uploadStore.Object,
            passwordValidator.Object,
            new OctopusArchiveExtractionOptions { MaxUploadSizeBytes = 2 });

        var response = await sut.Handle(Context(new UploadOctopusImportCommand
        {
            SpaceId = 7,
            Password = "octopus-password",
            FileName = "export.zip",
            SizeBytes = 3,
            Content = new MemoryStream(Encoding.UTF8.GetBytes("zip"))
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Msg.ShouldContain("2 bytes");
        passwordValidator.Verify(v => v.ValidateAsync(
            It.IsAny<Stream>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        sessionService.VerifyNoOtherCalls();
        uploadStore.VerifyNoOtherCalls();
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
