using System.Linq;
using System.IO;
using System.Net;
using Mediator.Net.Contracts;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Handlers.OctopusImport;

public class ValidateOctopusImportCommandHandlerTests
{
    [Fact]
    public async Task Handle_LoadsTheSessionExportSecretsBeforePlanningAndValidating()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var preview = Preview();
        var secretsAvailableWhenPlanning = false;
        var secretsAvailableWhenValidating = false;

        harness.SecretStore.Setup(s => s.LoadAsync(sessionId, 7, It.IsAny<CancellationToken>())).ReturnsAsync(OctopusExportSecrets.FromPassword("pw"));
        harness.SessionDataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, uploadPath, OctopusImportSessionState.Previewed));
        harness.PlanningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, 7, It.IsAny<CancellationToken>())).Callback(() => secretsAvailableWhenPlanning = harness.SecretsAccessor.Secrets.IsAvailable).ReturnsAsync(Snapshot(preview));
        harness.PreviewValidator.Setup(v => v.Validate(It.IsAny<OctopusResourceGraph>(), It.IsAny<OctopusImportDependencyPlan>(), It.IsAny<OctopusImportConflictDiscoveryResult>(), It.IsAny<OctopusImportPreviewPlanDto>())).Callback(() => secretsAvailableWhenValidating = harness.SecretsAccessor.Secrets.IsAvailable).Returns(new OctopusImportValidationResultDto());
        harness.SessionService.Setup(s => s.UpdatePayloadAndTransitionAsync(sessionId, 7, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated, null, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, DestinationSpaceId = 7, State = OctopusImportSessionState.Validated });

        await harness.Sut.Handle(Context(new ValidateOctopusImportCommand { SessionId = sessionId, SpaceId = 7 }), CancellationToken.None);

        secretsAvailableWhenPlanning.ShouldBeTrue();
        secretsAvailableWhenValidating.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_WhenValidationHasNoBlockers_TransitionsSessionToValidated()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var preview = Preview();

        harness.SessionDataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, uploadPath, OctopusImportSessionState.Previewed));
        harness.PlanningPipeline
            .Setup(p => p.BuildPreviewAsync(uploadPath, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(preview));
        harness.PreviewValidator
            .Setup(v => v.Validate(
                It.IsAny<OctopusResourceGraph>(),
                It.IsAny<OctopusImportDependencyPlan>(),
                It.IsAny<OctopusImportConflictDiscoveryResult>(),
                It.IsAny<OctopusImportPreviewPlanDto>()))
            .Returns(new OctopusImportValidationResultDto());
        harness.SessionService
            .Setup(s => s.UpdatePayloadAndTransitionAsync(
                sessionId,
                7,
                OctopusImportSessionState.Previewed,
                OctopusImportSessionState.Validated,
                null,
                It.Is<string>(json => json.Contains("previewPlan")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusImportSessionDto
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                State = OctopusImportSessionState.Validated
            });

        var response = await harness.Sut.Handle(Context(new ValidateOctopusImportCommand
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
        response.Data.Validation.HasBlockers.ShouldBeFalse();
        harness.SessionService.Verify(s => s.UpdatePayloadAndTransitionAsync(
            sessionId,
            7,
            OctopusImportSessionState.Previewed,
            OctopusImportSessionState.Validated,
            null,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenValidationHasBlockers_ReturnsBadRequestWithoutTransition()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var preview = Preview();
        var validation = new OctopusImportValidationResultDto
        {
            Diagnostics =
            [
                new OctopusImportDiagnosticDto
                {
                    Severity = OctopusImportCompatibilitySeverity.Blocker,
                    Code = OctopusImportPreviewDiagnosticCodes.MissingTargetRole,
                    Message = "Missing target role."
                }
            ]
        };

        harness.SessionDataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, uploadPath, OctopusImportSessionState.Previewed));
        harness.PlanningPipeline
            .Setup(p => p.BuildPreviewAsync(uploadPath, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(preview));
        harness.PreviewValidator
            .Setup(v => v.Validate(
                It.IsAny<OctopusResourceGraph>(),
                It.IsAny<OctopusImportDependencyPlan>(),
                It.IsAny<OctopusImportConflictDiscoveryResult>(),
                It.IsAny<OctopusImportPreviewPlanDto>()))
            .Returns(validation);

        var response = await harness.Sut.Handle(Context(new ValidateOctopusImportCommand
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Data.Validation.HasBlockers.ShouldBeTrue();
        harness.SessionService.Verify(s => s.UpdatePayloadAndTransitionAsync(
            It.IsAny<Guid>(),
            It.IsAny<int>(),
            It.IsAny<OctopusImportSessionState>(),
            It.IsAny<OctopusImportSessionState>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClientHistoryRows_ReplacedByServerRows()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var serverPlan = Preview();
        serverPlan.Resources.Add(Row("Deployments-1", "Deployment", OctopusImportPreviewAction.Create, null));
        serverPlan.Resources.Add(Row("Releases-1", "Release", OctopusImportPreviewAction.Update, 21));
        var clientPlan = Preview();
        clientPlan.Resources[0].PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        clientPlan.Resources.Add(Row("Deployments-1", "Deployment", OctopusImportPreviewAction.AlreadyImported, 999));
        clientPlan.Resources.Add(Row("ServerTasks-forged", "ServerTask", OctopusImportPreviewAction.AlreadyImported, 998));
        OctopusImportPreviewPlanDto validatedPlan = null;

        harness.SessionDataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, uploadPath, OctopusImportSessionState.Previewed));
        harness.PlanningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, 7, It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(serverPlan));
        harness.PreviewValidator.Setup(v => v.Validate(It.IsAny<OctopusResourceGraph>(), It.IsAny<OctopusImportDependencyPlan>(), It.IsAny<OctopusImportConflictDiscoveryResult>(), It.IsAny<OctopusImportPreviewPlanDto>())).Callback<OctopusResourceGraph, OctopusImportDependencyPlan, OctopusImportConflictDiscoveryResult, OctopusImportPreviewPlanDto>((_, _, _, plan) => validatedPlan = plan).Returns(new OctopusImportValidationResultDto());
        harness.SessionService.Setup(s => s.UpdatePayloadAndTransitionAsync(sessionId, 7, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated, null, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, DestinationSpaceId = 7, State = OctopusImportSessionState.Validated });

        var response = await harness.Sut.Handle(Context(new ValidateOctopusImportCommand { SessionId = sessionId, SpaceId = 7, PreviewPlan = clientPlan }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        validatedPlan.Resources.Select(r => $"{r.SourceType}|{r.SourceId}|{r.PreviewAction}|{r.DestinationId}").ShouldBe(["Project|Projects-1|ReuseExisting|", "Deployment|Deployments-1|Create|", "Release|Releases-1|Update|21"], customMessage: "configuration rows stay the client's; every history row is the server's and forged history rows disappear");
        response.Data.PreviewPlan.Resources.ShouldNotContain(r => r.SourceId == "ServerTasks-forged");
    }

    private static OctopusImportResourceResultDto Row(string sourceId, string sourceType, OctopusImportPreviewAction action, int? destinationId)
        => new() { SourceId = sourceId, SourceType = sourceType, SourceName = sourceId, PreviewAction = action, DestinationId = destinationId, OutcomeState = OctopusImportResourceOutcomeState.Pending };

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

    private static OctopusImportSession Session(Guid sessionId, string uploadPath, OctopusImportSessionState state)
        => new()
        {
            SessionId = sessionId,
            OwnerUserId = 42,
            DestinationSpaceId = 7,
            State = state.ToString(),
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
            Sut = new ValidateOctopusImportCommandHandler(
                SessionDataProvider.Object,
                SessionService.Object,
                CurrentUser.Object,
                PlanningPipeline.Object,
                PreviewValidator.Object,
                SecretStore.Object,
                SecretsAccessor);
        }

        public Mock<IOctopusImportExportSecretStore> SecretStore { get; } = new();

        public OctopusExportSecretsAccessor SecretsAccessor { get; } = new();

        public Mock<IOctopusImportSessionDataProvider> SessionDataProvider { get; } = new();

        public Mock<IOctopusImportSessionService> SessionService { get; } = new();

        public Mock<ICurrentUser> CurrentUser { get; } = new();

        public Mock<IOctopusImportPlanningPipeline> PlanningPipeline { get; } = new();

        public Mock<IOctopusImportPreviewValidator> PreviewValidator { get; } = new();

        public ValidateOctopusImportCommandHandler Sut { get; }
    }
}
