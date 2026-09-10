using System.IO;
using System.Linq;
using System.Net;
using Mediator.Net.Contracts;
using Squid.Core.Handlers.RequestHandlers.OctopusImport;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.UnitTests.Handlers.OctopusImport;

public class GetOctopusImportPreviewRequestHandlerTests
{
    [Fact]
    public async Task Handle_WhenSessionIsExtracted_BuildsPreviewAndTransitionsToPreviewed()
    {
        var harness = new Harness();
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var preview = new OctopusImportPreviewPlanDto
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

        harness.SessionDataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session(sessionId, uploadPath, OctopusImportSessionState.Extracted));
        harness.PlanningPipeline
            .Setup(p => p.BuildPreviewAsync(uploadPath, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(preview));
        harness.SessionService
            .Setup(s => s.UpdatePayloadAndTransitionAsync(
                sessionId,
                7,
                OctopusImportSessionState.Extracted,
                OctopusImportSessionState.Previewed,
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusImportSessionDto
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                State = OctopusImportSessionState.Previewed
            });

        var response = await harness.Sut.Handle(Context(new GetOctopusImportPreviewRequest
        {
            SessionId = sessionId,
            SpaceId = 7
        }), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Session.State.ShouldBe(OctopusImportSessionState.Previewed);
        response.Data.PreviewPlan.Resources.Single().SourceId.ShouldBe("Projects-1");
        harness.SessionService.Verify(s => s.UpdatePayloadAndTransitionAsync(
            sessionId,
            7,
            OctopusImportSessionState.Extracted,
            OctopusImportSessionState.Previewed,
            It.IsAny<string>(),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

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

    private static IReceiveContext<T> Context<T>(T message) where T : class, IRequest
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
            Sut = new GetOctopusImportPreviewRequestHandler(
                SessionDataProvider.Object,
                SessionService.Object,
                CurrentUser.Object,
                PlanningPipeline.Object);
        }

        public Mock<IOctopusImportSessionDataProvider> SessionDataProvider { get; } = new();

        public Mock<IOctopusImportSessionService> SessionService { get; } = new();

        public Mock<ICurrentUser> CurrentUser { get; } = new();

        public Mock<IOctopusImportPlanningPipeline> PlanningPipeline { get; } = new();

        public GetOctopusImportPreviewRequestHandler Sut { get; }
    }
}
