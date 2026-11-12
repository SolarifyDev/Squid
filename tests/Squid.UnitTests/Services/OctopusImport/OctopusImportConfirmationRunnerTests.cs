using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The runner reloads the session by the owner it is given, never by the scope's current user: a background job
/// runs in a scope whose current user would otherwise be the internal account, which owns no import session.
/// </summary>
public class OctopusImportConfirmationRunnerTests
{
    private const int SpaceId = 7;
    private const int OwnerUserId = 99;

    private readonly Mock<IOctopusImportSessionDataProvider> _sessionDataProvider = new();
    private readonly Mock<IOctopusImportSessionService> _sessionService = new();
    private readonly Mock<IAuthorizationService> _authorization = new();
    private readonly Mock<IOctopusImportPlanningPipeline> _planningPipeline = new();
    private readonly Mock<IOctopusImportConfirmationOrchestrator> _orchestrator = new();
    private readonly Mock<ISquidBackgroundJobClient> _backgroundJobClient = new();

    [Fact]
    public async Task Runner_LoadsSessionByOwner_AndPreflightsTheOwnersPermissions()
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(ValidatedSession(sessionId, uploadPath));
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        var outcome = await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        outcome.IsRejected.ShouldBeFalse();
        outcome.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        _sessionDataProvider.Verify(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>()), Times.Once);
        _authorization.Verify(a => a.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.UserId == OwnerUserId && r.SpaceId == SpaceId && r.Permission == Permission.ProjectCreate), It.IsAny<CancellationToken>()), Times.Once);
        _orchestrator.Verify(o => o.ConfirmAsync(It.Is<OctopusImportConfirmationRequest>(r => r.SessionId == sessionId && r.DestinationSpaceId == SpaceId && r.SessionDirectory == Path.GetDirectoryName(uploadPath)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_SessionNotOwnedByTheGivenOwner_IsNotFound_NothingRuns(bool enqueue)
    {
        var sessionId = Guid.NewGuid();
        var runner = CreateRunner();

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, 1, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(ValidatedSession(sessionId, CreateTempFile()));

        await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => enqueue ? runner.EnqueueAsync(sessionId, SpaceId, OwnerUserId) : runner.RunAsync(sessionId, SpaceId, OwnerUserId));

        _orchestrator.Verify(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _backgroundJobClient.Verify(c => c.Enqueue(It.IsAny<System.Linq.Expressions.Expression<Func<IOctopusImportConfirmationJob, Task>>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Runner_ArchiveNoLongerExtractable_IsRejected_SessionStaysValidated()
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(ValidatedSession(sessionId, uploadPath));
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<CancellationToken>())).ThrowsAsync(new OctopusArchiveExtractionException("Archive.Broken", "broken archive"));

        var outcome = await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        outcome.IsRejected.ShouldBeTrue();
        outcome.Rejection.Code.ShouldBe(OctopusImportConfirmationDiagnosticCodes.ConfirmationRequiresValidatedSession);
        outcome.Rejection.Message.ShouldBe("broken archive");
        outcome.Session.State.ShouldBe(OctopusImportSessionState.Validated);
        _orchestrator.Verify(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enqueue_QueuesTheJobForTheOwner_WithoutPlanningOrConfirming()
    {
        var sessionId = Guid.NewGuid();
        System.Linq.Expressions.Expression<Func<IOctopusImportConfirmationJob, Task>> queued = null;

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(ValidatedSession(sessionId, CreateTempFile()));
        _sessionService.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Validated });
        _backgroundJobClient
            .Setup(c => c.Enqueue(It.IsAny<System.Linq.Expressions.Expression<Func<IOctopusImportConfirmationJob, Task>>>(), It.IsAny<string>()))
            .Callback<System.Linq.Expressions.Expression<Func<IOctopusImportConfirmationJob, Task>>, string>((call, _) => queued = call)
            .Returns("job-1");

        var outcome = await CreateRunner().EnqueueAsync(sessionId, SpaceId, OwnerUserId);

        outcome.Session.State.ShouldBe(OctopusImportSessionState.Validated);
        _planningPipeline.VerifyNoOtherCalls();
        _orchestrator.VerifyNoOtherCalls();

        var job = new Mock<IOctopusImportConfirmationJob>();
        await queued.Compile()(job.Object);
        job.Verify(j => j.RunAsOwnerAsync(sessionId, SpaceId, OwnerUserId, CancellationToken.None), Times.Once);
    }

    private OctopusImportConfirmationRunner CreateRunner()
        => new(_sessionDataProvider.Object, _sessionService.Object, _authorization.Object, _planningPipeline.Object, _orchestrator.Object, _backgroundJobClient.Object);

    private static OctopusImportPreviewPlanDto Preview()
        => new() { GeneratedAt = DateTimeOffset.UtcNow, Resources = [new OctopusImportResourceResultDto { SourceId = "Projects-1", SourceType = "Project", SourceName = "Project", PreviewAction = OctopusImportPreviewAction.Create, OutcomeState = OctopusImportResourceOutcomeState.Pending }] };

    private static OctopusImportPlanningSnapshot Snapshot()
        => new(new OctopusResourceGraph([new OctopusResourceNode("Projects-1", "Project", OctopusResourceKind.Project, OctopusDocumentKind.Project, "project.json", null, null, false, new object())], [], [], []), new OctopusImportDependencyPlan([], [], [], []), new OctopusImportConflictDiscoveryResult([]), Preview());

    private static OctopusImportSession ValidatedSession(Guid sessionId, string uploadPath)
        => new()
        {
            SessionId = sessionId,
            OwnerUserId = OwnerUserId,
            DestinationSpaceId = SpaceId,
            State = OctopusImportSessionState.Validated.ToString(),
            TemporaryUploadPath = uploadPath,
            SourceSummaryJson = "{}",
            ValidatedPlanJson = JsonSerializer.Serialize(new OctopusImportValidatedPlanDto { PreviewPlan = Preview(), Validation = new OctopusImportValidationResultDto() }, JsonOptions),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
}
