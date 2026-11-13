using System.Linq;
using Autofac;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
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
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConfirmationOutcome(Session(sessionId, OctopusImportSessionState.Succeeded)));

        await RunJobAsync(sessionId);

        var user = _runnerUsers.ShouldHaveSingleItem();
        user.Id.ShouldBe(OwnerUserId);
        user.IsInternal.ShouldBeFalse("the owner's permissions must be checked, exactly as in a synchronous confirmation");
        _sessions.Verify(s => s.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OctopusImportSessionState>(), It.IsAny<OctopusImportSessionResultDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Validated, true)]   // refused before the import: nobody polls a reply, so the session ends with the reason
    [InlineData(OctopusImportSessionState.Importing, false)]  // another run owns it
    [InlineData(OctopusImportSessionState.Failed, false)]     // the orchestrator already recorded the outcome
    public async Task RunAsOwner_Rejected_EndsAStillValidatedSessionWithTheReason(OctopusImportSessionState currentState, bool expectRecorded)
    {
        var sessionId = Guid.NewGuid();
        var rejection = new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable, Message = "password unreadable" };
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConfirmationOutcome(Session(sessionId, OctopusImportSessionState.Validated), rejection));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, currentState));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(sessionId, SpaceId, OctopusImportSessionState.Failed, It.Is<OctopusImportSessionResultDto>(r => r.Diagnostics.Single() == rejection), It.IsAny<CancellationToken>()), expectRecorded ? Times.Once() : Times.Never());
        _sessionUsers.ShouldAllBe(user => user.Id == OwnerUserId && !user.IsInternal);
    }

    [Fact]
    public async Task RunAsOwner_RunnerThrowsBeforeTheImport_EndsTheSessionAsBackgroundFailed()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("permission denied"));
        _sessions.Setup(s => s.GetSessionAsync(sessionId, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Session(sessionId, OctopusImportSessionState.Validated));

        await RunJobAsync(sessionId);

        _sessions.Verify(s => s.RecordResultAsync(sessionId, SpaceId, OctopusImportSessionState.Failed, It.Is<OctopusImportSessionResultDto>(r => r.Diagnostics.Single().Code == OctopusImportConfirmationDiagnosticCodes.BackgroundConfirmationFailed && r.Diagnostics.Single().Severity == OctopusImportCompatibilitySeverity.Blocker && r.Diagnostics.Single().Message.Contains("permission denied")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsOwner_Cancelled_PropagatesAndLeavesTheSessionAlone()
    {
        var sessionId = Guid.NewGuid();
        _runner.Setup(r => r.RunAsync(sessionId, SpaceId, OwnerUserId, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => RunJobAsync(sessionId));

        _sessions.VerifyNoOtherCalls();
    }

    private async Task RunJobAsync(Guid sessionId)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance<ICurrentUser>(new InternalUser());
        builder.Register(c => { _runnerUsers.Add(c.Resolve<ICurrentUser>()); return _runner.Object; }).As<IOctopusImportConfirmationRunner>().InstancePerLifetimeScope();
        builder.Register(c => { _sessionUsers.Add(c.Resolve<ICurrentUser>()); return _sessions.Object; }).As<IOctopusImportSessionService>().InstancePerLifetimeScope();

        await using var container = builder.Build();
        await using var jobScope = container.BeginLifetimeScope();

        await new OctopusImportConfirmationJob(jobScope).RunAsOwnerAsync(sessionId, SpaceId, OwnerUserId, CancellationToken.None);
    }

    private static OctopusImportSessionDto Session(Guid sessionId, OctopusImportSessionState state) => new() { SessionId = sessionId, DestinationSpaceId = SpaceId, State = state };
}
