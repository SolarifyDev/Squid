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

    // An Octopus default channel takes over the default channel of a project created by this import (an edit); under a
    // project imported before it is created as a regular channel, so the owner needs the create permission.
    [Theory]
    [InlineData(OctopusImportPreviewAction.Create, Permission.ChannelEdit)]
    [InlineData(OctopusImportPreviewAction.AlreadyImported, Permission.ChannelCreate)]
    public async Task Runner_DefaultChannel_RequiresThePermissionOfWhatConfirmationDoes(OctopusImportPreviewAction projectAction, Permission expected)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var preview = Preview(projectAction, new OctopusImportResourceResultDto { SourceId = "Channels-1", SourceType = "Channel", SourceName = "Default", OwnerProjectSourceId = "Projects-1", PreviewAction = OctopusImportPreviewAction.Create, OutcomeState = OctopusImportResourceOutcomeState.Pending });
        var channel = new OctopusResourceNode("Channels-1", "Default", OctopusResourceKind.Channel, OctopusDocumentKind.Channel, "channel.json", null, "Projects-1", false, new OctopusChannelDto { Id = "Channels-1", ProjectId = "Projects-1", IsDefault = true });

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(ValidatedSession(sessionId, uploadPath, preview));
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(preview, channel));
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        var checkedPermissions = _authorization.Invocations.Select(i => ((PermissionCheckRequest)i.Arguments[0]).Permission).ToList();
        checkedPermissions.ShouldContain(expected);
        checkedPermissions.ShouldNotContain(expected == Permission.ChannelEdit ? Permission.ChannelCreate : Permission.ChannelEdit);
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

    private static OctopusImportPreviewPlanDto Preview(OctopusImportPreviewAction projectAction = OctopusImportPreviewAction.Create, params OctopusImportResourceResultDto[] others)
        => new() { GeneratedAt = DateTimeOffset.UtcNow, Resources = [new OctopusImportResourceResultDto { SourceId = "Projects-1", SourceType = "Project", SourceName = "Project", PreviewAction = projectAction, OutcomeState = OctopusImportResourceOutcomeState.Pending }, .. others] };

    private static OctopusImportPlanningSnapshot Snapshot(OctopusImportPreviewPlanDto preview = null, params OctopusResourceNode[] others)
        => new(new OctopusResourceGraph([new OctopusResourceNode("Projects-1", "Project", OctopusResourceKind.Project, OctopusDocumentKind.Project, "project.json", null, null, false, new object()), .. others], [], [], []), new OctopusImportDependencyPlan([], [], [], []), new OctopusImportConflictDiscoveryResult([]), preview ?? Preview());

    private static OctopusImportSession ValidatedSession(Guid sessionId, string uploadPath, OctopusImportPreviewPlanDto preview = null)
        => new()
        {
            SessionId = sessionId,
            OwnerUserId = OwnerUserId,
            DestinationSpaceId = SpaceId,
            State = OctopusImportSessionState.Validated.ToString(),
            TemporaryUploadPath = uploadPath,
            SourceSummaryJson = "{}",
            ValidatedPlanJson = JsonSerializer.Serialize(new OctopusImportValidatedPlanDto { PreviewPlan = preview ?? Preview(), Validation = new OctopusImportValidationResultDto() }, JsonOptions),
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
