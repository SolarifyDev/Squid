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
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewSession(sessionId, OctopusImportSessionState.Extracted));

        var result = await _service.GetSessionAsync(sessionId, 7, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Extracted);
        _dataProvider.Verify(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSessionAsync_WhenOwnedSessionIsMissing_ThrowsNotFound()
    {
        await Should.ThrowAsync<OctopusImportSessionNotFoundException>(
            () => _service.GetSessionAsync(Guid.NewGuid(), 7, CancellationToken.None));
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
        _dataProvider.Verify(p => p.UpdateSessionAsync(session, true, It.IsAny<CancellationToken>()), Times.Once);
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

        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), true, It.IsAny<CancellationToken>()), Times.Never);
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
        _dataProvider.Verify(p => p.UpdateSessionAsync(session, true, It.IsAny<CancellationToken>()), Times.Once);
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
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewSession(sessionId, OctopusImportSessionState.Validated));
        _dataProvider
            .Setup(p => p.TryStartConfirmationAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var admitted = await _service.TryStartConfirmationAsync(sessionId, 7, CancellationToken.None);

        admitted.ShouldBeTrue();
        _dataProvider.Verify(p => p.TryStartConfirmationAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()), Times.Once);
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
        _dataProvider.Verify(p => p.UpdateSessionAsync(session, true, It.IsAny<CancellationToken>()), Times.Once);
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
            LastStateChangedAt = DateTimeOffset.UtcNow
        };
    }
}
