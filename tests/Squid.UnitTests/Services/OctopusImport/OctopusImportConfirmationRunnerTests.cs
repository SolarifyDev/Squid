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

using Squid.UnitTests.Services.OctopusImport.SessionFiles;

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
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        var outcome = await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        outcome.IsRejected.ShouldBeFalse();
        outcome.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        _sessionDataProvider.Verify(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>()), Times.Once);
        _authorization.Verify(a => a.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.UserId == OwnerUserId && r.SpaceId == SpaceId && r.Permission == Permission.ProjectCreate), It.IsAny<CancellationToken>()), Times.Once);
        _orchestrator.Verify(o => o.ConfirmAsync(It.Is<OctopusImportConfirmationRequest>(r => r.SessionId == sessionId && r.DestinationSpaceId == SpaceId), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Confirmation plans again with the session's own project selection (the one the plan was validated with), never a request's.
    [Theory]
    [InlineData("{\"selectedProjectIds\":[\"Projects-2\"]}", new[] { "Projects-2" })]
    [InlineData("{}", null)]
    public async Task Runner_PlansWithTheSessionsProjectSelection(string summaryJson, string[] expected)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var session = ValidatedSession(sessionId, uploadPath);
        session.SourceSummaryJson = summaryJson;
        session.ValidatedPlanJson = JsonSerializer.Serialize(new OctopusImportValidatedPlanDto { PreviewPlan = Preview(), Validation = new OctopusImportValidationResultDto(), SelectedProjectIds = expected?.ToList() }, JsonOptions);
        IReadOnlyCollection<string> planned = ["not called"];

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).Callback<string, int, OctopusImportChoiceSelections, IReadOnlyCollection<string>, CancellationToken>((_, _, _, chosen, _) => planned = chosen).ReturnsAsync(Snapshot());
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        planned.ShouldBe(expected);
    }

    // A validation that read one selection can store its plan after the selection changed (it re-reads the session to store): the plan
    // remembers the selection it was validated with, and confirmation refuses it when the session's selection is now another.
    [Theory]
    [InlineData("[\"Projects-1\",\"Projects-2\"]", "{\"selectedProjectIds\":[\"Projects-1\"]}", true)]
    [InlineData("[\"Projects-1\"]", "{}", true)]
    [InlineData(null, "{\"selectedProjectIds\":[\"Projects-1\"]}", true)]
    [InlineData("[\"projects-2\",\"Projects-1\"]", "{\"selectedProjectIds\":[\"Projects-1\",\"Projects-2\"]}", false)]
    [InlineData(null, "{}", false)]
    public async Task Runner_PlanValidatedForAnotherSelection_Rejected_NothingImported(string planSelectionJson, string summaryJson, bool rejected)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var session = ValidatedSession(sessionId, uploadPath);
        session.SourceSummaryJson = summaryJson;
        session.ValidatedPlanJson = JsonSerializer.Serialize(new OctopusImportValidatedPlanDto { PreviewPlan = Preview(), Validation = new OctopusImportValidationResultDto(), SelectedProjectIds = planSelectionJson == null ? null : JsonSerializer.Deserialize<List<string>>(planSelectionJson) }, JsonOptions);

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        var outcome = await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        outcome.IsRejected.ShouldBe(rejected);
        _orchestrator.Verify(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>()), rejected ? Times.Never() : Times.Once());
        if (rejected) outcome.Rejection.Message.ShouldBe(OctopusImportConfirmationRunner.SelectionChangedMessage);
        if (rejected) outcome.Rejection.Code.ShouldBe(OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded);
    }

    // The plan is imported only while the session still holds the version it was read with; a plan validated again meanwhile (the
    // confirmation finds the session still Validated) is reported, never silently left unimported.
    [Theory]
    [InlineData(OctopusImportSessionState.Succeeded, false)]
    [InlineData(OctopusImportSessionState.Validated, true)]
    public async Task Runner_ConfirmsTheVersionThePlanWasReadWith_RejectsWhenValidatedAgain(OctopusImportSessionState stateAfterConfirm, bool rejected)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var session = ValidatedSession(sessionId, uploadPath);
        session.DataVersion = [4, 2];

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = stateAfterConfirm });

        var outcome = await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        _orchestrator.Verify(o => o.ConfirmAsync(It.Is<OctopusImportConfirmationRequest>(r => r.ValidatedDataVersion.SequenceEqual(new byte[] { 4, 2 })), It.IsAny<CancellationToken>()), Times.Once);
        outcome.IsRejected.ShouldBe(rejected);
        if (rejected) outcome.Rejection.Message.ShouldContain("validated again");
        if (rejected) outcome.Rejection.Code.ShouldBe(OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded);
    }

    // A queued run imports only the plan that was Validated when Import was pressed: a job requeued after its server died, run after
    // the session was recovered and validated again, finds another data version and imports nothing (the newer plan was never confirmed).
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Runner_QueuedRun_ImportsOnlyThePlanAdmittedAtEnqueue(bool sameVersion, bool superseded)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var session = ValidatedSession(sessionId, uploadPath);
        session.DataVersion = [1, 2, 3];

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        var outcome = await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId, sameVersion ? [1, 2, 3] : [9, 9, 9], CancellationToken.None);

        outcome.IsRejected.ShouldBe(superseded);
        if (superseded) outcome.Rejection.Code.ShouldBe(OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded);
        _orchestrator.Verify(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>()), superseded ? Times.Never() : Times.Once());
    }

    // Validated again with THIS attempt's failure: the import rolled back and can be imported again (retryable). Validated with another
    // attempt's failure, or none, means the plan read here was replaced before it was admitted (superseded).
    [Theory]
    [InlineData(true, OctopusImportConfirmationDiagnosticCodes.ConfirmationRetryable)]
    [InlineData(false, OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded)]
    public async Task Runner_ValidatedAfterConfirm_RetryableOnlyWithItsOwnAttemptFailure(bool ownAttempt, string expectedCode)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        OctopusImportConfirmationRequest sent = null;

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(ValidatedSession(sessionId, uploadPath));
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).Callback<OctopusImportConfirmationRequest, CancellationToken>((r, _) => sent = r).ReturnsAsync((OctopusImportConfirmationRequest r, CancellationToken _) => new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Validated, LastConfirmationFailure = new OctopusImportConfirmationFailureDto { AttemptId = ownAttempt ? r.AttemptId : Guid.NewGuid(), Reason = OctopusImportConfirmationFailureReason.RolledBack, Diagnostics = [new OctopusImportDiagnosticDto { Code = "x", Message = "the root cause" }] } });

        var outcome = await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        sent.AttemptId.ShouldNotBe(Guid.Empty);
        outcome.Rejection.Code.ShouldBe(expectedCode);
        if (ownAttempt) outcome.Rejection.Message.ShouldContain("the root cause");
        outcome.Session.LastConfirmationFailure.ShouldNotBeNull("the page reads why it can import again from the session the confirmation left");
    }


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
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(preview, channel));
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        var checkedPermissions = _authorization.Invocations.Select(i => ((PermissionCheckRequest)i.Arguments[0]).Permission).ToList();
        checkedPermissions.ShouldContain(expected);
        checkedPermissions.ShouldNotContain(expected == Permission.ChannelEdit ? Permission.ChannelCreate : Permission.ChannelEdit);
    }

    // A fill writes one secret into a variable set or feed imported before: the owner needs the edit permission a user setting it
    // in Squid needs — not a create permission (nothing is created) and not nothing (a secret is written).
    [Theory]
    [InlineData(OctopusResourceKind.Variable, "Variable", Permission.VariableEdit)]
    [InlineData(OctopusResourceKind.Feed, "Feed", Permission.FeedEdit)]
    public async Task Runner_SecretFill_RequiresTheEditPermissionOfWhatItWrites(OctopusResourceKind kind, string sourceType, Permission expected)
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = CreateTempFile();
        var preview = Preview(OctopusImportPreviewAction.AlreadyImported, new OctopusImportResourceResultDto { SourceId = "secret-1", SourceType = sourceType, SourceName = "Secret", PreviewAction = OctopusImportPreviewAction.FillMissingSecret, OutcomeState = OctopusImportResourceOutcomeState.Pending });
        var resource = new OctopusResourceNode("secret-1", "Secret", kind, OctopusDocumentKind.VariableSet, "secret.json", "Projects-1", null, false, new object());

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(ValidatedSession(sessionId, uploadPath, preview));
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(preview, resource));
        _orchestrator.Setup(o => o.ConfirmAsync(It.IsAny<OctopusImportConfirmationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Succeeded });

        await CreateRunner().RunAsync(sessionId, SpaceId, OwnerUserId);

        _authorization.Invocations.Select(i => ((PermissionCheckRequest)i.Arguments[0]).Permission).ShouldBe([expected]);
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
        _planningPipeline.Setup(p => p.BuildPreviewAsync(uploadPath, SpaceId, It.IsAny<OctopusImportChoiceSelections>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OctopusArchiveExtractionException("Archive.Broken", "broken archive"));

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

        var session = ValidatedSession(sessionId, CreateTempFile());
        session.DataVersion = [4, 5, 6];
        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
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
        job.Verify(j => j.RunAsOwnerAsync(sessionId, SpaceId, OwnerUserId, It.Is<byte[]>(v => v.SequenceEqual(new byte[] { 4, 5, 6 })), CancellationToken.None), Times.Once);
    }

    // Past its maximum age the session takes no more work: both confirmation paths refuse with SessionExpired (409) before planning,
    // confirming or queueing, so a background confirmation never ends an over-age session as a generic failure.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_SessionOverMaxAge_ThrowsSessionExpired_NothingRunsOrIsQueued(bool enqueue)
    {
        var sessionId = Guid.NewGuid();
        var session = ValidatedSession(sessionId, CreateTempFile());
        session.CreatedDate = DateTimeOffset.UtcNow.AddHours(-169);
        var runner = CreateRunner();

        _sessionDataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, OwnerUserId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var exception = await Should.ThrowAsync<OctopusImportSessionExpiredException>(() => enqueue ? runner.EnqueueAsync(sessionId, SpaceId, OwnerUserId) : runner.RunAsync(sessionId, SpaceId, OwnerUserId));

        exception.Deadline.ShouldBe(session.CreatedDate.AddHours(168));
        _planningPipeline.VerifyNoOtherCalls();
        _orchestrator.VerifyNoOtherCalls();
        _backgroundJobClient.Verify(c => c.Enqueue(It.IsAny<System.Linq.Expressions.Expression<Func<IOctopusImportConfirmationJob, Task>>>(), It.IsAny<string>()), Times.Never);
    }

    private OctopusImportConfirmationRunner CreateRunner()
        => new(_sessionDataProvider.Object, _sessionService.Object, _authorization.Object, _planningPipeline.Object, _orchestrator.Object, _backgroundJobClient.Object, Settings(), TestUploadStores.LocalOnly());

    private static IOctopusImportTemporaryUploadSettings Settings()
    {
        var settings = new Mock<IOctopusImportTemporaryUploadSettings>();
        settings.SetupGet(s => s.MaxSessionAge).Returns(TimeSpan.FromHours(168));

        return settings.Object;
    }

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
            CreatedDate = DateTimeOffset.UtcNow,
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
