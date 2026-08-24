using System.IO;
using System.Net;
using System.Text;
using Mediator.Net.Contracts;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Handlers.OctopusImport;

public class ExtractOctopusImportCommandHandlerTests
{
    [Fact]
    public async Task Handle_WhenExtractionHasNoBlockers_TransitionsSessionToExtracted()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile("{\"Entries\":[]}");
        harness.SessionDataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, uploadPath));
        harness.InputExtractor
            .Setup(e => e.ExtractStandaloneJsonAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<OctopusArchiveExtractionOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusInputExtractionResult([], []));
        harness.InventoryBuilder
            .Setup(b => b.Build(It.IsAny<OctopusInputExtractionResult>()))
            .Returns(new OctopusManifestInventoryResult(new OctopusExportManifestDto(), [], [], []));
        harness.GraphBuilder
            .Setup(b => b.Build(It.IsAny<OctopusManifestInventoryResult>()))
            .Returns(new OctopusResourceGraph([], [], [], []));
        harness.SessionService
            .Setup(s => s.UpdatePayloadAndTransitionAsync(
                sessionId,
                7,
                OctopusImportSessionState.Uploaded,
                OctopusImportSessionState.Extracted,
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusImportSessionDto
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                State = OctopusImportSessionState.Extracted
            });

        var response = await harness.Sut.Handle(Context(new ExtractOctopusImportCommand
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.State.ShouldBe(OctopusImportSessionState.Extracted);
        harness.SessionService.Verify(s => s.UpdatePayloadAndTransitionAsync(
            sessionId,
            7,
            OctopusImportSessionState.Uploaded,
            OctopusImportSessionState.Extracted,
            It.IsAny<string>(),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenExtractionHasBlockers_ReturnsBadRequestWithoutTransition()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile("{bad json");
        harness.SessionDataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, uploadPath));
        harness.InputExtractor
            .Setup(e => e.ExtractStandaloneJsonAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<OctopusArchiveExtractionOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusInputExtractionResult([], [
                new OctopusInputExtractionDiagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    "OctopusImport.Extraction.MalformedJson",
                    "Malformed JSON.",
                    "export.json")
            ]));
        harness.InventoryBuilder
            .Setup(b => b.Build(It.IsAny<OctopusInputExtractionResult>()))
            .Returns((OctopusInputExtractionResult result) => new OctopusManifestInventoryResult(null, [], [], result.Diagnostics));
        harness.GraphBuilder
            .Setup(b => b.Build(It.IsAny<OctopusManifestInventoryResult>()))
            .Returns((OctopusManifestInventoryResult result) => new OctopusResourceGraph([], [], [], result.Diagnostics));

        var response = await harness.Sut.Handle(Context(new ExtractOctopusImportCommand
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Data.Extraction.HasBlockers.ShouldBeTrue();
        harness.SessionService.Verify(s => s.UpdatePayloadAndTransitionAsync(
            It.IsAny<Guid>(),
            It.IsAny<int>(),
            It.IsAny<OctopusImportSessionState>(),
            It.IsAny<OctopusImportSessionState>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static OctopusImportSession Session(Guid sessionId, string uploadPath)
        => new()
        {
            SessionId = sessionId,
            OwnerUserId = 42,
            DestinationSpaceId = 7,
            State = OctopusImportSessionState.Uploaded.ToString(),
            TemporaryUploadPath = uploadPath,
            SourceSummaryJson = "{}",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };

    private static IReceiveContext<T> Context<T>(T message) where T : class, ICommand
    {
        var context = new Mock<IReceiveContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }

    private sealed class Harness
    {
        public Harness()
        {
            CurrentUser.SetupGet(u => u.Id).Returns(42);
            Sut = new ExtractOctopusImportCommandHandler(
                SessionDataProvider.Object,
                SessionService.Object,
                CurrentUser.Object,
                ArchiveExtractor.Object,
                InputExtractor.Object,
                InventoryBuilder.Object,
                GraphBuilder.Object);
        }

        public Mock<IOctopusImportSessionDataProvider> SessionDataProvider { get; } = new();

        public Mock<IOctopusImportSessionService> SessionService { get; } = new();

        public Mock<ICurrentUser> CurrentUser { get; } = new();

        public Mock<IOctopusArchiveExtractor> ArchiveExtractor { get; } = new();

        public Mock<IOctopusInputExtractor> InputExtractor { get; } = new();

        public Mock<IOctopusManifestInventoryBuilder> InventoryBuilder { get; } = new();

        public Mock<IOctopusResourceGraphBuilder> GraphBuilder { get; } = new();

        public ExtractOctopusImportCommandHandler Sut { get; }
    }
}
