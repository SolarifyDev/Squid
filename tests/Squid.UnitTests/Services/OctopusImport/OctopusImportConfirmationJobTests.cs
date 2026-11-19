using System.Linq;
using Autofac;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The background entry point of a queued confirmation: a real Autofac container whose root scope acts as the
/// internal user (what a Hangfire job scope gets), so the tests prove the runner really runs as the owner.
/// </summary>
public class OctopusImportConfirmationJobTests
{
    private const int SpaceId = 7;
    private const int OwnerUserId = 99;

    private readonly Mock<IOctopusImportConfirmationRunner> _runner = new();
    private readonly Mock<IOctopusImportSessionService> _sessions = new();
    private readonly List<ICurrentUser> _runnerUsers = [];
    private readonly List<ICurrentUser> _sessionUsers = [];

    [Fact]
    public async Task RunAsOwner_RunsTheRunnerInAScopeActingAsTheOwner_NotTheInternalUser()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConfirmationOutcome(Session(sessionId, OctopusImportSessionState.Succeeded)));

        await RunJobAsync(sessionId);

        var user = _runnerUsers.ShouldHaveSingleItem();
        user.Id.ShouldBe(OwnerUserId);
        user.IsInternal.ShouldBeFalse("the owner's permissions must be checked, exactly as in a synchronous confirmation");
        _sessions.Verify(s => s.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OctopusImportSessionState>(), It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The data version admitted when Import was pressed travels with the job, so a job requeued after its server died imports nothing
    // the user did not confirm; a job queued by an older server carries none and runs as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsOwner_PassesTheAdmittedDataVersionToTheRunner(bool versioned)
    {
        var sessionId = Guid.NewGuid();
        byte[] admitted = versioned ? [1, 2, 3] : null;
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConfirmationOutcome(Session(sessionId, OctopusImportSessionState.Succeeded)));

        await RunJobAsync(sessionId, admitted);

        _runner.Verify(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, admitted, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Validated, true)]   // refused before the import: nobody polls a reply, so the session ends with the reason
    [InlineData(OctopusImportSessionState.Importing, false)]  // another run owns it
    [InlineData(OctopusImportSessionState.Failed, false)]     // the orchestrator already recorded the outcome
    public async Task RunAsOwner_Rejected_EndsAStillValidatedSessionWithTheReason(OctopusImportSessionState currentState, bool expectRecorded)
    {
        var sessionId = Guid.NewGuid();
        var rejection = new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable, Message = "password unreadable" };
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConfirmationOutcome(Session(sessionId, OctopusImportSessionState.Validated), rejection));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, currentState));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(sessionId, SpaceId, OctopusImportSessionState.Failed, It.Is<OctopusImportSessionResultDto>(r => r.Diagnostics.Single() == rejection), It.IsAny<CancellationToken>()), expectRecorded ? Times.Once() : Times.Never());
        _sessionUsers.ShouldAllBe(user => user.Id == OwnerUserId && !user.IsInternal);
    }

    // The plan this run read was replaced while it built its snapshot (another tab validated again, or changed the projects): the
    // session still holds a plan the user can import, so the run leaves it Validated instead of ending it.
    [Fact]
    public async Task RunAsOwner_PlanSupersededMeanwhile_LeavesTheSessionValidated()
    {
        var sessionId = Guid.NewGuid();
        var rejection = new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded, Message = "validated again" };
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConfirmationOutcome(Session(sessionId, OctopusImportSessionState.Validated), rejection));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, OctopusImportSessionState.Validated));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OctopusImportSessionState>(), It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsOwner_RunnerThrowsBeforeTheImport_EndsTheSessionAsBackgroundFailed()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("permission denied"));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, OctopusImportSessionState.Validated));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(sessionId, SpaceId, OctopusImportSessionState.Failed, It.Is<OctopusImportSessionResultDto>(r => r.Diagnostics.Single().Code == OctopusImportConfirmationDiagnosticCodes.BackgroundConfirmationFailed && r.Diagnostics.Single().Severity == OctopusImportCompatibilitySeverity.Blocker && r.Diagnostics.Single().Message.Contains("permission denied")), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The session passed its maximum age between queueing and running: it ends Expired with the max-age reason (the page offers a
    // new session), never Failed with a generic background failure that would also keep the upload a further retention period.
    [Fact]
    public async Task RunAsOwner_SessionReachedMaxAge_EndsTheSessionExpiredWithTheMaxAgeReason()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OctopusImportSessionExpiredException(sessionId, DateTimeOffset.UtcNow, TimeSpan.FromHours(168)));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, OctopusImportSessionState.Validated));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(sessionId, SpaceId, OctopusImportSessionState.Expired, It.Is<OctopusImportSessionResultDto>(r => r.Diagnostics.Single().Code == OctopusImportSessionExpiry.MaxAgeReachedCode && r.Diagnostics.Single().Severity == OctopusImportCompatibilitySeverity.Blocker && r.Diagnostics.Single().Message == OctopusImportSessionExpiry.MaxAgeReachedMessage(TimeSpan.FromHours(168))), It.IsAny<CancellationToken>()), Times.Once);
        _sessions.Verify(s => s.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), OctopusImportSessionState.Failed, It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A third-party message (a driver error quoting a connection setting) reaches the session owner only through the import's redaction.
    [Fact]
    public async Task RunAsOwner_RunnerThrowsWithASecretInTheMessage_StoredRedacted()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("connect failed: Password=hunter2 Host=db"));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, OctopusImportSessionState.Validated));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(sessionId, SpaceId, OctopusImportSessionState.Failed, It.Is<OctopusImportSessionResultDto>(r => !r.Diagnostics.Single().Message.Contains("hunter2") && r.Diagnostics.Single().Message.Contains("connect failed")), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The import rolled back and returned the session to Validated with the reason: the page offers "Import again", so it is not ended.
    [Fact]
    public async Task RunAsOwner_RetryableRejection_LeavesTheSessionValidated()
    {
        var sessionId = Guid.NewGuid();
        var rejection = new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportConfirmationDiagnosticCodes.ConfirmationRetryable, Message = "rolled back" };
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConfirmationOutcome(Session(sessionId, OctopusImportSessionState.Validated), rejection));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, OctopusImportSessionState.Validated));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OctopusImportSessionState>(), It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A transient database failure before the import started (reading staged files, admission) imported nothing: the session stays
    // Validated with a NotStarted failure the page can import again from, never Failed.
    [Fact]
    public async Task RunAsOwner_TransientPreImportFailure_RecordsNotStartedAndStaysValidated()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Npgsql.PostgresException("Password=hunter2", "FATAL", "FATAL", "57P01"));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, OctopusImportSessionState.Validated));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordConfirmationNotStartedAsync(sessionId, SpaceId, It.Is<OctopusImportConfirmationFailureDto>(f => f.Reason == OctopusImportConfirmationFailureReason.NotStarted && f.Transient && f.AttemptId != Guid.Empty && f.Diagnostics.Single().Code == OctopusImportConfirmationDiagnosticCodes.ConfirmationNotStarted && f.Diagnostics.Single().Message.Contains("57P01") && !f.Diagnostics.Single().Message.Contains("hunter2")), It.IsAny<CancellationToken>()), Times.Once);
        _sessions.Verify(s => s.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OctopusImportSessionState>(), It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsOwner_Cancelled_PropagatesAndLeavesTheSessionAlone()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => RunJobAsync(sessionId));

        _sessions.VerifyNoOtherCalls();
    }

    private async Task RunJobAsync(Guid sessionId, byte[] admittedDataVersion = null)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance<ICurrentUser>(new InternalUser());
        builder.Register(c => { _runnerUsers.Add(c.Resolve<ICurrentUser>()); return _runner.Object; }).As<IOctopusImportConfirmationRunner>().InstancePerLifetimeScope();
        builder.Register(c => { _sessionUsers.Add(c.Resolve<ICurrentUser>()); return _sessions.Object; }).As<IOctopusImportSessionService>().InstancePerLifetimeScope();

        await using var container = builder.Build();
        await using var jobScope = container.BeginLifetimeScope();

        var job = new OctopusImportConfirmationJob(jobScope);

        if (admittedDataVersion == null) await job.RunAsOwnerAsync(sessionId, SpaceId, OwnerUserId, CancellationToken.None);
        else await job.RunAsOwnerAsync(sessionId, SpaceId, OwnerUserId, admittedDataVersion, CancellationToken.None);
    }

    private static OctopusImportSessionDto Session(Guid sessionId, OctopusImportSessionState state) => new() { SessionId = sessionId, DestinationSpaceId = SpaceId, State = state };
}
