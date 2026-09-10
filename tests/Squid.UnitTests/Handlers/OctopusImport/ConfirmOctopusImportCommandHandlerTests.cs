using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public class ConfirmOctopusImportCommandHandlerTests
{
    [Fact]
    public async Task Handle_WhenSessionIsValidated_ConfirmsAndReturnsSucceededSession()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var preview = Preview();
        var validatedPlan = new OctopusImportValidatedPlanDto
        {
            PreviewPlan = preview,
            Validation = new OctopusImportValidationResultDto()
        };

        harness.SessionDataProvider
            .Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, uploadPath, OctopusImportSessionState.Validated, validatedPlan));
        harness.PlanningPipeline
            .Setup(p => p.BuildPreviewAsync(uploadPath, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(preview));
        harness.ConfirmationOrchestrator
            .Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusImportSessionDto
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                State = OctopusImportSessionState.Succeeded,
                Result = new OctopusImportSessionResultDto
                {
                    Succeeded = true
                }
            });

        var response = await harness.Sut.Handle(Context(new ConfirmOctopusImportCommand
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.ConfirmationOrchestrator.Verify(o => o.ConfirmAsync(
            It.Is<OctopusImportConfirmationRequest>(request =>
                request.SessionId == sessionId &&
                request.DestinationSpaceId == 7 &&
                request.PreviewPlan.Resources.Single().SourceId == "Projects-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenSessionIsAlreadyTerminal_ReturnsCurrentSessionWithoutConfirmingAgain()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();

        harness.SessionDataProvider
            .Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, CreateTempFile(), OctopusImportSessionState.Succeeded));
        harness.SessionService
            .Setup(s => s.GetSessionAsync(sessionId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusImportSessionDto
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                State = OctopusImportSessionState.Succeeded
            });

        var response = await harness.Sut.Handle(Context(new ConfirmOctopusImportCommand
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.ConfirmationOrchestrator.Verify(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenOwnedSessionHasNoTemporaryUpload_ReturnsBadRequestWithoutConfirming()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();

        harness.SessionDataProvider
            .Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, null, OctopusImportSessionState.Validated));

        var response = await harness.Sut.Handle(Context(new ConfirmOctopusImportCommand
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
        harness.PlanningPipeline.Verify(
            p => p.BuildPreviewAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.ConfirmationOrchestrator.Verify(
            o => o.ConfirmAsync(
                It.IsAny<OctopusImportConfirmationRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static OctopusImportPreviewPlanDto Preview()
        => new()
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources =
            [
                new OctopusImportResourceResultDto
                {
                    SourceId = "Projects-1",
                    SourceType = "Project",
                    SourceName = "Project",
                    PreviewAction = OctopusImportPreviewAction.Create,
                    OutcomeState = OctopusImportResourceOutcomeState.Pending
                }
            ]
        };

    private static OctopusImportPlanningSnapshot Snapshot(OctopusImportPreviewPlanDto preview)
        => new(
            new OctopusResourceGraph([], [], [], []),
            new OctopusImportDependencyPlan([], [], [], []),
            new OctopusImportConflictDiscoveryResult([]),
            preview);

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static OctopusImportSession Session(
        Guid sessionId,
        string uploadPath,
        OctopusImportSessionState state,
        OctopusImportValidatedPlanDto validatedPlan = null)
        => new()
        {
            SessionId = sessionId,
            OwnerUserId = 42,
            DestinationSpaceId = 7,
            State = state.ToString(),
            TemporaryUploadPath = uploadPath,
            SourceSummaryJson = "{}",
            ValidatedPlanJson = validatedPlan == null ? null : JsonSerializer.Serialize(validatedPlan, JsonOptions),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
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
            Sut = new ConfirmOctopusImportCommandHandler(
                SessionDataProvider.Object,
                SessionService.Object,
                CurrentUser.Object,
                PlanningPipeline.Object,
                ConfirmationOrchestrator.Object);
        }

        public Mock<IOctopusImportSessionDataProvider> SessionDataProvider { get; } = new();

        public Mock<IOctopusImportSessionService> SessionService { get; } = new();

        public Mock<ICurrentUser> CurrentUser { get; } = new();

        public Mock<IOctopusImportPlanningPipeline> PlanningPipeline { get; } = new();

        public Mock<IOctopusImportConfirmationOrchestrator> ConfirmationOrchestrator { get; } = new();

        public ConfirmOctopusImportCommandHandler Sut { get; }
    }
}
