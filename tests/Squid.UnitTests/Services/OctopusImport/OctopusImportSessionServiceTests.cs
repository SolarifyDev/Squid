using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportSessionServiceTests
{
    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IOctopusImportTemporaryUploadSettings> _temporaryUploadSettings = new();
    private readonly OctopusImportSessionService _service;

    public OctopusImportSessionServiceTests()
    {
        _currentUser.SetupGet(u => u.Id).Returns(42);
        _temporaryUploadSettings.SetupGet(s => s.FailedRetentionPeriod).Returns(TimeSpan.FromHours(6));
        _temporaryUploadSettings.SetupGet(s => s.DefaultRetentionPeriod).Returns(TimeSpan.FromHours(24));
        _temporaryUploadSettings.SetupGet(s => s.MaxSessionAge).Returns(TimeSpan.FromHours(168));
        _service = new OctopusImportSessionService(_dataProvider.Object, _currentUser.Object, _temporaryUploadSettings.Object);
    }

    [Fact]
    public async Task CreateSessionAsync_PersistsUploadedSessionForCurrentUserAndDestinationSpace()
    {
        OctopusImportSession inserted = null;

        _dataProvider
            .Setup(p => p.AddSessionAsync(It.IsAny<OctopusImportSession>(), true, It.IsAny<CancellationToken>()))
            .Callback<OctopusImportSession, bool, CancellationToken>((session, _, _) => inserted = session)
            .Returns(Task.CompletedTask);

        var summary = new OctopusImportSourceSummaryDto
        {
            FileName = "export.zip",
            SizeBytes = 123,
            DetectedFormat = "Zip"
        };
        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);

        var result = await _service.CreateSessionAsync(7, summary, expiresAt, CancellationToken.None);

        inserted.ShouldNotBeNull();
        inserted.SessionId.ShouldNotBe(Guid.Empty);
        inserted.OwnerUserId.ShouldBe(42);
        inserted.DestinationSpaceId.ShouldBe(7);
        inserted.State.ShouldBe(OctopusImportSessionState.Uploaded.ToString());
        inserted.SourceSummaryJson.ShouldContain("export.zip");
        inserted.ExpiresAt.ShouldBe(expiresAt);
        inserted.TemporaryUploadCleanupAfter.ShouldBe(expiresAt);

        result.SessionId.ShouldBe(inserted.SessionId);
        result.OwnerUserId.ShouldBe(42);
        result.DestinationSpaceId.ShouldBe(7);
        result.State.ShouldBe(OctopusImportSessionState.Uploaded);
        result.SourceSummary.FileName.ShouldBe("export.zip");
    }

    [Fact]
    public async Task CreateSessionAsync_CapsRequestedExpiryToConfiguredRetentionPeriod()
    {
        OctopusImportSession inserted = null;

        _dataProvider
            .Setup(p => p.AddSessionAsync(It.IsAny<OctopusImportSession>(), true, It.IsAny<CancellationToken>()))
            .Callback<OctopusImportSession, bool, CancellationToken>((session, _, _) => inserted = session)
            .Returns(Task.CompletedTask);

        var before = DateTimeOffset.UtcNow;

        await _service.CreateSessionAsync(
            7,
            new OctopusImportSourceSummaryDto { FileName = "export.zip" },
            before.AddDays(30),
            CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        inserted.ExpiresAt.ShouldBeGreaterThanOrEqualTo(before.AddHours(24));
        inserted.ExpiresAt.ShouldBeLessThanOrEqualTo(after.AddHours(24));
        inserted.TemporaryUploadCleanupAfter.ShouldBe(inserted.ExpiresAt);
    }

    [Fact]
    public async Task CreateSessionAsync_WithoutExplicitExpiry_UsesConfiguredRetentionPeriod()
    {
        OctopusImportSession inserted = null;

        _dataProvider
            .Setup(p => p.AddSessionAsync(It.IsAny<OctopusImportSession>(), true, It.IsAny<CancellationToken>()))
            .Callback<OctopusImportSession, bool, CancellationToken>((session, _, _) => inserted = session)
            .Returns(Task.CompletedTask);

        var before = DateTimeOffset.UtcNow;

        await _service.CreateSessionAsync(
            7,
            new OctopusImportSourceSummaryDto { FileName = "export.zip" },
            CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        inserted.ExpiresAt.ShouldBeGreaterThanOrEqualTo(before.AddHours(24));
        inserted.ExpiresAt.ShouldBeLessThanOrEqualTo(after.AddHours(24));
    }

    [Fact]
    public async Task GetSessionAsync_LooksUpBySessionOwnerAndDestinationSpace()
    {
        var sessionId = Guid.NewGuid();
        _dataProvider
            .Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewSession(sessionId, OctopusImportSessionState.Extracted));

        var result = await _service.GetSessionAsync(sessionId, 7, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Extracted);
        _dataProvider.Verify(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSessionAsync_WhenOwnedSessionIsMissing_ThrowsNotFound()
    {
        await Should.ThrowAsync<OctopusImportSessionNotFoundException>(
            () => _service.GetSessionAsync(Guid.NewGuid(), 7, CancellationToken.None));
    }

    [Fact]
    public async Task GetSessionAsync_RequiresAuthenticatedUserBeforeLookingUpSession()
    {
        _currentUser.SetupGet(u => u.Id).Returns((int?)null);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _service.GetSessionAsync(Guid.NewGuid(), 7, CancellationToken.None));

        _dataProvider.Verify(
            p => p.GetSessionNoTrackingAsync(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterTemporaryUploadAsync_AttachesUploadMetadataToOwnedUploadedSession()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Uploaded);

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        await _service.RegisterTemporaryUploadAsync(
            sessionId,
            7,
            new OctopusImportTemporaryUpload("/tmp/squid-octopus-import-uploads/upload.zip", 1234),
            CancellationToken.None);

        session.TemporaryUploadPath.ShouldBe("/tmp/squid-octopus-import-uploads/upload.zip");
        session.TemporaryUploadSizeBytes.ShouldBe(1234);
        session.TemporaryUploadCleanupAfter.ShouldBe(session.ExpiresAt);
        session.TemporaryUploadCleanedAt.ShouldBeNull();
        session.TemporaryUploadCleanupError.ShouldBeNull();
        _dataProvider.Verify(
            p => p.UpdateSessionAsync(session, session.DataVersion, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterTemporaryUploadAsync_RejectsAlreadyAdvancedSession()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Extracted);

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        await Should.ThrowAsync<OctopusImportSessionStateTransitionException>(
            () => _service.RegisterTemporaryUploadAsync(
                sessionId,
                7,
                new OctopusImportTemporaryUpload("/tmp/upload.zip", 1234),
                CancellationToken.None));
    }

    // A validation after the user changed what to import replaces (or, when blocked, clears) the plan confirmation imports, only while
    // the session is Validated: once importing or settled, the plan it confirmed is never swapped.
    [Theory]
    [InlineData("{\"previewPlan\":{}}")]
    [InlineData(null)]
    public async Task ReplaceValidatedPlanAsync_Validated_ReplacesOrClearsThePlan_StateKept(string json)
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Validated);
        session.ValidatedPlanJson = "{\"previewPlan\":{\"old\":true}}";
        _dataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var result = await _service.ReplaceValidatedPlanAsync(sessionId, 7, json);

        session.ValidatedPlanJson.ShouldBe(json);
        result.State.ShouldBe(OctopusImportSessionState.Validated);
        _dataProvider.Verify(p => p.UpdateSessionAsync(session, session.DataVersion, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Previewed)]
    [InlineData(OctopusImportSessionState.Importing)]
    [InlineData(OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Failed)]
    public async Task ReplaceValidatedPlanAsync_NotValidated_Throws_NothingWritten(OctopusImportSessionState state)
    {
        var sessionId = Guid.NewGuid();
        _dataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(NewSession(sessionId, state));

        await Should.ThrowAsync<OctopusImportSessionStateTransitionException>(() => _service.ReplaceValidatedPlanAsync(sessionId, 7, "{}"));

        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePayloadAndTransitionAsync_RejectsUnexpectedCurrentState()
    {
        var sessionId = Guid.NewGuid();
        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewSession(sessionId, OctopusImportSessionState.Uploaded));

        await Should.ThrowAsync<OctopusImportSessionStateTransitionException>(
            () => _service.UpdatePayloadAndTransitionAsync(
                sessionId,
                7,
                OctopusImportSessionState.Extracted,
                OctopusImportSessionState.Previewed,
                ct: CancellationToken.None));

        _dataProvider.Verify(
            p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePayloadAndTransitionAsync_StoresPayloadAndMovesState()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Uploaded);

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var result = await _service.UpdatePayloadAndTransitionAsync(
            sessionId,
            7,
            OctopusImportSessionState.Uploaded,
            OctopusImportSessionState.Extracted,
            redactedNormalizedDataJson: "{\"projects\":1}",
            ct: CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Extracted.ToString());
        session.RedactedNormalizedDataJson.ShouldBe("{\"projects\":1}");
        session.TemporaryUploadCleanupAfter.ShouldBeNull();
        result.State.ShouldBe(OctopusImportSessionState.Extracted);
        _dataProvider.Verify(
            p => p.UpdateSessionAsync(session, session.DataVersion, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePayloadAndTransitionAsync_WhenExpired_MakesTemporaryUploadImmediatelyEligibleForCleanup()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Uploaded);
        session.TemporaryUploadPath = "/tmp/upload.zip";

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        await _service.UpdatePayloadAndTransitionAsync(
            sessionId,
            7,
            OctopusImportSessionState.Uploaded,
            OctopusImportSessionState.Expired,
            ct: CancellationToken.None);

        session.CompletedAt.ShouldNotBeNull();
        session.TemporaryUploadCleanupAfter.ShouldBe(session.CompletedAt.Value);
    }

    [Fact]
    public async Task UpdatePayloadAndTransitionAsync_RedactsPersistedPayloadJson()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Uploaded);

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        await _service.UpdatePayloadAndTransitionAsync(
            sessionId,
            7,
            OctopusImportSessionState.Uploaded,
            OctopusImportSessionState.Extracted,
            redactedNormalizedDataJson: """
            {
              "variables": [{ "name": "ApiKey", "type": "Sensitive", "isSensitive": true, "value": "session-variable-secret" }],
              "account": { "credentials": { "secretKey": "session-account-secret" } },
              "machine": { "endpoint": { "uri": "https://worker.example", "bearerToken": "session-endpoint-token" } }
            }
            """,
            validatedPlanJson: """{"properties":{"Octopus.Action.Custom.Password":"session-property-secret"}}""",
            ct: CancellationToken.None);

        session.RedactedNormalizedDataJson.ShouldNotContain("session-variable-secret");
        session.RedactedNormalizedDataJson.ShouldNotContain("session-account-secret");
        session.RedactedNormalizedDataJson.ShouldNotContain("session-endpoint-token");
        session.RedactedNormalizedDataJson.ShouldContain("https://worker.example");
        session.ValidatedPlanJson.ShouldNotContain("session-property-secret");
    }

    [Fact]
    public async Task TryStartConfirmationAsync_UsesAtomicValidatedToImportingAdmission()
    {
        var sessionId = Guid.NewGuid();
        _dataProvider
            .Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewSession(sessionId, OctopusImportSessionState.Validated));
        _dataProvider
            .Setup(p => p.TryStartConfirmationAsync(It.Is<OctopusImportConfirmationAdmission>(a => a.SessionId == sessionId && a.OwnerUserId == 42 && a.DestinationSpaceId == 7), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var admitted = await _service.TryStartConfirmationAsync(sessionId, 7, ct: CancellationToken.None);

        admitted.ShouldBeTrue();
        _dataProvider.Verify(
            p => p.TryStartConfirmationAsync(It.Is<OctopusImportConfirmationAdmission>(a => a.SessionId == sessionId && a.OwnerUserId == 42 && a.DestinationSpaceId == 7 && a.AttemptId != Guid.Empty), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The caller's attempt id is the fencing token the admission records; nothing else may stand in for it.
    [Fact]
    public async Task TryStartConfirmationAsync_WithAttemptId_RecordsThatAttempt()
    {
        var sessionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(NewSession(sessionId, OctopusImportSessionState.Validated));
        _dataProvider.Setup(p => p.TryStartConfirmationAsync(It.IsAny<OctopusImportConfirmationAdmission>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _service.TryStartConfirmationAsync(sessionId, 7, attemptId, null, CancellationToken.None);

        _dataProvider.Verify(p => p.TryStartConfirmationAsync(It.Is<OctopusImportConfirmationAdmission>(a => a.AttemptId == attemptId), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The version the validated plan was read with wins over the current one: a plan validated again meanwhile is never imported instead.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryStartConfirmationAsync_ExpectsTheVersionThePlanWasReadWith_OrTheCurrentOne(bool planVersionGiven)
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Validated);
        session.DataVersion = [1, 1, 1];
        byte[] planVersion = [2, 2, 2];
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _dataProvider.Setup(p => p.TryStartConfirmationAsync(It.IsAny<OctopusImportConfirmationAdmission>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _service.TryStartConfirmationAsync(sessionId, 7, planVersionGiven ? planVersion : null, CancellationToken.None);

        var expected = planVersionGiven ? planVersion : session.DataVersion;
        _dataProvider.Verify(p => p.TryStartConfirmationAsync(It.Is<OctopusImportConfirmationAdmission>(a => a.ExpectedDataVersion == expected), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryStartConfirmationAsync_WhenSessionIsMissing_ThrowsNotFoundWithoutAdmission()
    {
        var sessionId = Guid.NewGuid();

        var exception = await Should.ThrowAsync<OctopusImportSessionNotFoundException>(
            () => _service.TryStartConfirmationAsync(sessionId, 7, ct: CancellationToken.None));

        exception.SessionId.ShouldBe(sessionId);
        _dataProvider.Verify(p => p.TryStartConfirmationAsync(It.IsAny<OctopusImportConfirmationAdmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Failed)]
    public async Task RecordResultAsync_AnyTerminalState_ClearsTheRetainedExportPassword(OctopusImportSessionState terminalState)
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Importing);
        session.ProtectedExportPassword = "v2:protected";
        string persistedPassword = "not-called";

        _dataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _dataProvider
            .Setup(p => p.UpdateSessionAsync(session, It.IsAny<byte[]>(), true, It.IsAny<CancellationToken>()))
            .Callback<OctopusImportSession, byte[], bool, CancellationToken>((s, _, _, _) => persistedPassword = s.ProtectedExportPassword)
            .Returns(Task.CompletedTask);

        await _service.RecordResultAsync(sessionId, 7, terminalState, new OctopusImportSessionResultDto(), CancellationToken.None);

        persistedPassword.ShouldBeNull();
    }

    [Fact]
    public async Task UpdatePayloadAndTransitionAsync_NonTerminalTransition_KeepsTheRetainedExportPassword()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Previewed);
        session.ProtectedExportPassword = "v2:protected";

        _dataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        await _service.UpdatePayloadAndTransitionAsync(sessionId, 7, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated, validatedPlanJson: "{}", ct: CancellationToken.None);

        session.ProtectedExportPassword.ShouldBe("v2:protected");
    }

    [Fact]
    public async Task RecordResultAsync_PersistsTerminalResult()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Importing);

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var result = await _service.RecordResultAsync(
            sessionId,
            7,
            OctopusImportSessionState.Succeeded,
            new OctopusImportSessionResultDto
            {
                Resources =
                [
                    new OctopusImportResourceResultDto
                    {
                        SourceId = "Projects-1",
                        SourceType = "Project",
                        OutcomeState = OctopusImportResourceOutcomeState.Created,
                        PreviewAction = OctopusImportPreviewAction.Create,
                        DestinationId = 100
                    }
                ]
            },
            CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Succeeded.ToString());
        session.ResultJson.ShouldContain("Projects-1");
        session.CompletedAt.ShouldNotBeNull();
        session.TemporaryUploadCleanupAfter.ShouldBe(session.CompletedAt.Value);
        result.Result.Succeeded.ShouldBeTrue();
        result.Result.Resources.Count.ShouldBe(1);
        _dataProvider.Verify(
            p => p.UpdateSessionAsync(session, session.DataVersion, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordResultAsync_WhenFailed_RetainsTemporaryUploadForConfiguredFailureWindow()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Importing);
        session.TemporaryUploadPath = "/tmp/upload.zip";

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        await _service.RecordResultAsync(
            sessionId,
            7,
            OctopusImportSessionState.Failed,
            new OctopusImportSessionResultDto(),
            CancellationToken.None);

        session.CompletedAt.ShouldNotBeNull();
        session.TemporaryUploadCleanupAfter.ShouldBe(session.CompletedAt.Value.Add(TimeSpan.FromHours(6)));
    }


    [Fact]
    public async Task RecordResultAsync_RedactsPersistedDiagnostics()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Importing);

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        await _service.RecordResultAsync(
            sessionId,
            7,
            OctopusImportSessionState.Failed,
            new OctopusImportSessionResultDto
            {
                Diagnostics =
                [
                    new OctopusImportDiagnosticDto
                    {
                        Severity = OctopusImportCompatibilitySeverity.Warning,
                        Code = OctopusImportRedactionDiagnosticCodes.SuspiciousPropertyValueRedacted,
                        Message = "Failed with token=result-token-secret.",
                        ResourceType = "DeploymentAction",
                        SourceId = "Actions-1",
                        ResourceName = "Rotate password secret"
                    }
                ]
            },
            CancellationToken.None);

        session.ResultJson.ShouldNotContain("result-token-secret");
        session.ResultJson.ShouldNotContain("Rotate password secret");
        session.ResultJson.ShouldContain(OctopusImportRedaction.RedactedValue);
    }

    [Fact]
    public async Task RecordResultAsync_RejectsNonTerminalState()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _service.RecordResultAsync(
                Guid.NewGuid(),
                7,
                OctopusImportSessionState.Importing,
                new OctopusImportSessionResultDto(),
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateSessionAsync_RequiresAuthenticatedUser()
    {
        _currentUser.SetupGet(u => u.Id).Returns((int?)null);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _service.CreateSessionAsync(
                7,
                new OctopusImportSourceSummaryDto(),
                DateTimeOffset.UtcNow.AddHours(1),
                CancellationToken.None));
    }

    private static OctopusImportSession NewSession(Guid sessionId, OctopusImportSessionState state)
    {
        return new OctopusImportSession
        {
            SessionId = sessionId,
            OwnerUserId = 42,
            DestinationSpaceId = 7,
            State = state.ToString(),
            SourceSummaryJson = "{\"fileName\":\"export.zip\"}",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow,
            CreatedDate = DateTimeOffset.UtcNow
        };
    }

    // ── Maximum session age ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_MaxAgeBelowRetention_ExpiresAtIsTheCap()
    {
        _temporaryUploadSettings.SetupGet(s => s.MaxSessionAge).Returns(TimeSpan.FromHours(2));
        OctopusImportSession inserted = null;
        _dataProvider.Setup(p => p.AddSessionAsync(It.IsAny<OctopusImportSession>(), true, It.IsAny<CancellationToken>())).Callback<OctopusImportSession, bool, CancellationToken>((session, _, _) => inserted = session).Returns(Task.CompletedTask);

        await _service.CreateSessionAsync(7, new OctopusImportSourceSummaryDto(), CancellationToken.None);

        inserted.ExpiresAt.ShouldBe(inserted.CreatedDate.AddHours(2));
        inserted.TemporaryUploadCleanupAfter.ShouldBe(inserted.ExpiresAt);
    }

    [Fact]
    public async Task CreateSession_SetsCreatedDateAndMaxExpiresAt()
    {
        var before = DateTimeOffset.UtcNow;
        OctopusImportSession inserted = null;
        _dataProvider.Setup(p => p.AddSessionAsync(It.IsAny<OctopusImportSession>(), true, It.IsAny<CancellationToken>())).Callback<OctopusImportSession, bool, CancellationToken>((session, _, _) => inserted = session).Returns(Task.CompletedTask);

        var dto = await _service.CreateSessionAsync(7, new OctopusImportSourceSummaryDto(), CancellationToken.None);

        inserted.CreatedDate.ShouldBeGreaterThanOrEqualTo(before);
        dto.MaxExpiresAt.ShouldBe(inserted.CreatedDate.AddHours(168));
    }

    [Fact]
    public async Task Map_MaxExpiresAt_IsCreatedPlusMaxAge()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Previewed);
        session.CreatedDate = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var dto = await _service.GetSessionAsync(sessionId, 7, CancellationToken.None);

        dto.MaxExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero));
    }

    public static TheoryData<string> GuardedMutators() => new() { "RegisterTemporaryUpload", "UpdatePayloadAndTransition", "ReplaceValidatedPlan", "UpdateProjectSelection", "TryStartConfirmation" };

    // Past its maximum age a session takes no more work, even before the sweep closes it: nothing is written, nothing admitted.
    [Theory]
    [MemberData(nameof(GuardedMutators))]
    public async Task Mutator_SessionOverAge_ThrowsSessionExpired_NothingWritten(string mutator)
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, mutator is "RegisterTemporaryUpload" ? OctopusImportSessionState.Uploaded : mutator is "UpdatePayloadAndTransition" ? OctopusImportSessionState.Previewed : OctopusImportSessionState.Validated);
        session.CreatedDate = DateTimeOffset.UtcNow.AddHours(-169);
        _dataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        Task act() => mutator switch
        {
            "RegisterTemporaryUpload" => _service.RegisterTemporaryUploadAsync(sessionId, 7, new OctopusImportTemporaryUpload("/tmp/upload.zip", 10), CancellationToken.None),
            "UpdatePayloadAndTransition" => _service.UpdatePayloadAndTransitionAsync(sessionId, 7, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated, validatedPlanJson: "{}", ct: CancellationToken.None),
            "ReplaceValidatedPlan" => _service.ReplaceValidatedPlanAsync(sessionId, 7, "{}", CancellationToken.None),
            "UpdateProjectSelection" => _service.UpdateProjectSelectionAsync(sessionId, 7, ["Projects-1"], session.DataVersion, CancellationToken.None),
            _ => _service.TryStartConfirmationAsync(sessionId, 7, ct: CancellationToken.None)
        };

        var exception = await Should.ThrowAsync<OctopusImportSessionExpiredException>(act);

        exception.SessionId.ShouldBe(sessionId);
        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _dataProvider.Verify(p => p.TryStartConfirmationAsync(It.IsAny<OctopusImportConfirmationAdmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The admission itself also refuses a session that reached the cap, atomically with the state check.
    [Fact]
    public async Task TryStartConfirmation_PassesTheMaxAgeBoundToTheAtomicAdmission()
    {
        var sessionId = Guid.NewGuid();
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(NewSession(sessionId, OctopusImportSessionState.Validated));
        DateTimeOffset createdAfter = default;
        _dataProvider.Setup(p => p.TryStartConfirmationAsync(It.IsAny<OctopusImportConfirmationAdmission>(), It.IsAny<CancellationToken>())).Callback<OctopusImportConfirmationAdmission, CancellationToken>((admission, _) => createdAfter = admission.CreatedAfter).ReturnsAsync(true);
        var expected = DateTimeOffset.UtcNow.AddHours(-168);

        await _service.TryStartConfirmationAsync(sessionId, 7, ct: CancellationToken.None);

        createdAfter.ShouldBe(expected, TimeSpan.FromSeconds(5));
    }

    // An import that runs past the cap must still be able to record how it ended.
    [Fact]
    public async Task RecordResult_OverAge_StillRecords()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Importing);
        session.CreatedDate = DateTimeOffset.UtcNow.AddHours(-500);
        _dataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        await _service.RecordResultAsync(sessionId, 7, OctopusImportSessionState.Succeeded, new OctopusImportSessionResultDto(), CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Succeeded.ToString());
    }

    [Fact]
    public async Task UpdatePayloadAndTransition_ToFailedWhenOverAge_Allowed()
    {
        var sessionId = Guid.NewGuid();
        var session = NewSession(sessionId, OctopusImportSessionState.Previewed);
        session.CreatedDate = DateTimeOffset.UtcNow.AddHours(-500);
        _dataProvider.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        await _service.UpdatePayloadAndTransitionAsync(sessionId, 7, OctopusImportSessionState.Previewed, OctopusImportSessionState.Failed, ct: CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Failed.ToString());
    }
}
