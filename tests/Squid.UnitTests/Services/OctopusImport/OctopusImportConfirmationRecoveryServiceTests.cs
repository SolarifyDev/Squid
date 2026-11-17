using Mediator.Net;
using System.Text.Json;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Jobs.RecurringJobs;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConfirmationRecoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<IOctopusImportTemporaryUploadSettings> _settings = new();
    // A recovered session's import may have outlasted the idle expiry: it is kept a retention period from now, within its maximum age.
    private static readonly OctopusImportValidatedReturn ExpectedReturn = new(Now, TimeSpan.FromHours(24), TimeSpan.FromHours(168));

    private readonly OctopusImportConfirmationResilienceOptions _resilience = new() { HeartbeatStale = TimeSpan.FromMinutes(5) };

    public OctopusImportConfirmationRecoveryServiceTests()
    {
        _settings.SetupGet(s => s.InterruptedImportGracePeriod).Returns(TimeSpan.FromHours(6));
        _settings.SetupGet(s => s.CleanupBatchSize).Returns(25);
        _settings.SetupGet(s => s.DefaultRetentionPeriod).Returns(TimeSpan.FromHours(24));
        _settings.SetupGet(s => s.MaxSessionAge).Returns(TimeSpan.FromHours(168));
    }

    // A heartbeat is stale after the stale threshold, measured by the database clock that stamped it (never this server's clock, which
    // may run ahead of the importing server's); only a session with no heartbeat at all (an older server's) gets the long grace.
    [Fact]
    public void Cutoffs_StaleIsAnAgeOnTheDatabaseClock_LegacyFromTheGracePeriod()
    {
        var cutoffs = Service().Cutoffs(Now);

        cutoffs.HeartbeatStale.ShouldBe(TimeSpan.FromMinutes(5));
        cutoffs.LegacyStaleBefore.ShouldBe(Now.AddHours(-6));
    }

    [Fact]
    public async Task RecoverInterrupted_ReturnsEachCandidateToValidated_WithAnInterruptedFailureForItsAttempt()
    {
        var withHeartbeat = new OctopusImportInterruptedConfirmation(Guid.NewGuid(), Guid.NewGuid());
        var legacy = new OctopusImportInterruptedConfirmation(Guid.NewGuid(), null);
        var failures = new List<string>();
        _dataProvider.Setup(p => p.GetInterruptedConfirmationsAsync(It.IsAny<OctopusImportInterruptedConfirmationCutoffs>(), 25, It.IsAny<CancellationToken>())).ReturnsAsync([withHeartbeat, legacy]);
        _dataProvider.Setup(p => p.RecoverInterruptedConfirmationAsync(It.IsAny<OctopusImportInterruptedConfirmation>(), It.IsAny<OctopusImportInterruptedConfirmationCutoffs>(), It.IsAny<string>(), ExpectedReturn, It.IsAny<CancellationToken>())).Callback<OctopusImportInterruptedConfirmation, OctopusImportInterruptedConfirmationCutoffs, string, OctopusImportValidatedReturn, CancellationToken>((_, _, json, _, _) => failures.Add(json)).ReturnsAsync(1);

        var recovered = await Service().RecoverInterruptedAsync();

        recovered.ShouldBe(2);
        var expectedCutoffs = new OctopusImportInterruptedConfirmationCutoffs(TimeSpan.FromMinutes(5), Now.AddHours(-6));
        _dataProvider.Verify(p => p.GetInterruptedConfirmationsAsync(expectedCutoffs, 25, It.IsAny<CancellationToken>()), Times.Once);
        _dataProvider.Verify(p => p.RecoverInterruptedConfirmationAsync(withHeartbeat, expectedCutoffs, It.IsAny<string>(), ExpectedReturn, It.IsAny<CancellationToken>()), Times.Once);
        _dataProvider.Verify(p => p.RecoverInterruptedConfirmationAsync(legacy, expectedCutoffs, It.IsAny<string>(), ExpectedReturn, It.IsAny<CancellationToken>()), Times.Once);
        failures[0].ShouldContain(withHeartbeat.AttemptId.ToString());
        failures.ShouldAllBe(json => json.Contains(nameof(OctopusImportConfirmationFailureReason.Interrupted)) && json.Contains(OctopusImportConfirmationDiagnosticCodes.ConfirmationInterrupted));
        JsonDocument.Parse(failures[1]).RootElement.GetProperty("attemptId").GetGuid().ShouldNotBe(Guid.Empty);
    }

    // A candidate whose heartbeat came back between the read and the update is not recovered: the update refuses it and it is not counted.
    [Fact]
    public async Task RecoverInterrupted_CountsOnlyWhatTheUpdateRecovered()
    {
        _dataProvider.Setup(p => p.GetInterruptedConfirmationsAsync(It.IsAny<OctopusImportInterruptedConfirmationCutoffs>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([new OctopusImportInterruptedConfirmation(Guid.NewGuid(), Guid.NewGuid())]);
        _dataProvider.Setup(p => p.RecoverInterruptedConfirmationAsync(It.IsAny<OctopusImportInterruptedConfirmation>(), It.IsAny<OctopusImportInterruptedConfirmationCutoffs>(), It.IsAny<string>(), It.IsAny<OctopusImportValidatedReturn>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        (await Service().RecoverInterruptedAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Handler_DispatchesToTheServiceOnce()
    {
        var service = new Mock<IOctopusImportConfirmationRecoveryService>();
        service.Setup(s => s.RecoverInterruptedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var context = new Mock<IReceiveContext<RecoverInterruptedOctopusImportConfirmationsCommand>>();
        context.SetupGet(c => c.Message).Returns(new RecoverInterruptedOctopusImportConfirmationsCommand());

        var response = await new RecoverInterruptedOctopusImportConfirmationsCommandHandler(service.Object).Handle(context.Object, CancellationToken.None);

        response.Data.Recovered.ShouldBe(3);
        service.Verify(s => s.RecoverInterruptedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // The job id is Hangfire's key for the schedule: renaming it leaves the old schedule running beside the new one.
    [Fact]
    public async Task RecurringJob_PinnedEveryMinute_DispatchesTheCommand()
    {
        var mediator = new Mock<IMediator>();
        var job = new OctopusImportInterruptedConfirmationRecurringJob(mediator.Object);

        await job.Execute();

        job.JobId.ShouldBe("octopus-import-interrupted-confirmation-recovery");
        job.CronExpression.ShouldBe("* * * * *");
        mediator.Verify(m => m.SendAsync<RecoverInterruptedOctopusImportConfirmationsCommand, RecoverInterruptedOctopusImportConfirmationsResponse>(It.IsAny<RecoverInterruptedOctopusImportConfirmationsCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private OctopusImportConfirmationRecoveryService Service() => new(_dataProvider.Object, _settings.Object, new FixedTime(Now), _resilience);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
