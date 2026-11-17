using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>The session-service seam of a confirmation attempt: fenced settle, return to Validated, and the failure the page reads.</summary>
public class OctopusImportSessionServiceConfirmationTests
{
    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IOctopusImportTemporaryUploadSettings> _settings = new();
    private readonly OctopusImportSessionService _service;

    public OctopusImportSessionServiceConfirmationTests()
    {
        _currentUser.SetupGet(u => u.Id).Returns(42);
        _settings.SetupGet(s => s.FailedRetentionPeriod).Returns(TimeSpan.FromHours(6));
        _settings.SetupGet(s => s.DefaultRetentionPeriod).Returns(TimeSpan.FromHours(24));
        _settings.SetupGet(s => s.MaxSessionAge).Returns(TimeSpan.FromHours(168));
        _service = new OctopusImportSessionService(_dataProvider.Object, _currentUser.Object, _settings.Object);
    }

    [Fact]
    public async Task Settle_ByTheHoldingAttempt_RecordsTheResultAndClearsThePassword()
    {
        var attemptId = Guid.NewGuid();
        var session = Session(OctopusImportSessionState.Importing, attemptId);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var settled = await _service.SettleConfirmationAsync(session.SessionId, 7, attemptId, OctopusImportSessionState.Succeeded, new OctopusImportSessionResultDto());

        settled.State.ShouldBe(OctopusImportSessionState.Succeeded);
        _dataProvider.Verify(p => p.UpdateSessionAsync(It.Is<OctopusImportSession>(s => s.State == "Succeeded" && s.ProtectedExportPassword == null), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Recovered (Validated) or started again by another attempt: whatever this attempt was about to write must not land.
    [Theory]
    [InlineData(OctopusImportSessionState.Validated, false)]
    [InlineData(OctopusImportSessionState.Importing, true)]
    [InlineData(OctopusImportSessionState.Succeeded, false)]
    public async Task Settle_WhenTheAttemptNoLongerHoldsTheSession_ThrowsFencedWithoutWriting(OctopusImportSessionState state, bool anotherAttempt)
    {
        var attemptId = Guid.NewGuid();
        var session = Session(state, anotherAttempt ? Guid.NewGuid() : null);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var exception = await Should.ThrowAsync<OctopusImportConfirmationFencedException>(() => _service.SettleConfirmationAsync(session.SessionId, 7, attemptId, OctopusImportSessionState.Failed, new OctopusImportSessionResultDto()));

        exception.AttemptId.ShouldBe(attemptId);
        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReturnToValidated_FencedOnTheFailuresAttempt_StoresTheFailureRedacted_ReturnsTheSession()
    {
        var session = Session(OctopusImportSessionState.Validated, null);
        var failure = new OctopusImportConfirmationFailureDto { AttemptId = Guid.NewGuid(), Reason = OctopusImportConfirmationFailureReason.RolledBack, Diagnostics = [new OctopusImportDiagnosticDto { Code = "x", Message = "failed with Password=hunter2" }] };
        string stored = null;
        _dataProvider.Setup(p => p.ReturnConfirmationToValidatedAsync(session.SessionId, failure.AttemptId, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Callback<Guid, Guid, string, DateTimeOffset, CancellationToken>((_, _, json, _, _) => { stored = json; session.LastConfirmationFailureJson = json; }).ReturnsAsync(1);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var returned = await _service.ReturnConfirmationToValidatedAsync(session.SessionId, 7, failure);

        stored.ShouldNotContain("hunter2");
        returned.LastConfirmationFailure.ShouldNotBeNull().AttemptId.ShouldBe(failure.AttemptId);
        returned.LastConfirmationFailure.Reason.ShouldBe(OctopusImportConfirmationFailureReason.RolledBack);
    }

    // Only a Validated session can be imported again: once it moves on, an older attempt's failure no longer describes it.
    [Theory]
    [InlineData(OctopusImportSessionState.Validated, true)]
    [InlineData(OctopusImportSessionState.Importing, false)]
    [InlineData(OctopusImportSessionState.Succeeded, false)]
    [InlineData(OctopusImportSessionState.Failed, false)]
    public async Task Map_LastConfirmationFailure_OnlyWhileValidated(OctopusImportSessionState state, bool expected)
    {
        var session = Session(state, null);
        session.LastConfirmationFailureJson = OctopusImportSessionService.SerializeFailure(new OctopusImportConfirmationFailureDto { AttemptId = Guid.NewGuid(), Reason = OctopusImportConfirmationFailureReason.Interrupted });
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var dto = await _service.GetSessionAsync(session.SessionId, 7);

        (dto.LastConfirmationFailure != null).ShouldBe(expected);
    }

    // A plan or selection replaced after a failed attempt is a plan nobody tried: the older attempt's failure must not describe it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplacingThePlanOrSelection_ForgetsTheLastConfirmationFailure(bool replacePlan)
    {
        var session = Session(OctopusImportSessionState.Validated, null);
        session.LastConfirmationFailureJson = OctopusImportSessionService.SerializeFailure(new OctopusImportConfirmationFailureDto { AttemptId = Guid.NewGuid(), Reason = OctopusImportConfirmationFailureReason.RolledBack });
        _dataProvider.Setup(p => p.GetSessionAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var dto = replacePlan ? await _service.ReplaceValidatedPlanAsync(session.SessionId, 7, null) : await _service.UpdateProjectSelectionAsync(session.SessionId, 7, ["Projects-1"], session.DataVersion);

        dto.LastConfirmationFailure.ShouldBeNull();
        _dataProvider.Verify(p => p.UpdateSessionAsync(It.Is<OctopusImportSession>(s => s.LastConfirmationFailureJson == null), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordConfirmationNotStarted_WritesOnlyTheFailure()
    {
        var session = Session(OctopusImportSessionState.Validated, null);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        await _service.RecordConfirmationNotStartedAsync(session.SessionId, 7, new OctopusImportConfirmationFailureDto { AttemptId = Guid.NewGuid(), Reason = OctopusImportConfirmationFailureReason.NotStarted });

        _dataProvider.Verify(p => p.RecordConfirmationFailureAsync(session.SessionId, It.Is<string>(json => json.Contains("NotStarted")), It.IsAny<CancellationToken>()), Times.Once);
        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static OctopusImportSession Session(OctopusImportSessionState state, Guid? attemptId) => new()
    {
        SessionId = Guid.NewGuid(),
        OwnerUserId = 42,
        DestinationSpaceId = 7,
        State = state.ToString(),
        SourceSummaryJson = "{}",
        DataVersion = [1, 2, 3],
        ProtectedExportPassword = "protected",
        ConfirmationAttemptId = attemptId,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        LastStateChangedAt = DateTimeOffset.UtcNow,
        CreatedDate = DateTimeOffset.UtcNow
    };
}
