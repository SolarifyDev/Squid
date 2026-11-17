using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportConfirmationRecoveryService : IScopedDependency
{
    /// <summary>Returns every Importing session whose heartbeat stopped to Validated (it committed nothing) and reports how many.</summary>
    Task<int> RecoverInterruptedAsync(CancellationToken ct = default);
}

/// <summary>
/// An Importing session commits nothing until its Succeeded record lands in the same transaction as the import, so a session whose
/// heartbeat stopped (its server died) imported nothing and can be imported again. A session started by an older server has no
/// heartbeat and is trusted for the configured interrupted-import grace period instead.
/// </summary>
public sealed class OctopusImportConfirmationRecoveryService : IOctopusImportConfirmationRecoveryService
{
    private readonly IOctopusImportSessionDataProvider _dataProvider;
    private readonly IOctopusImportTemporaryUploadSettings _settings;
    private readonly TimeProvider _time;
    private readonly OctopusImportConfirmationResilienceOptions _resilience;

    public OctopusImportConfirmationRecoveryService(IOctopusImportSessionDataProvider dataProvider, IOctopusImportTemporaryUploadSettings settings) : this(dataProvider, settings, TimeProvider.System, OctopusImportConfirmationResilienceOptions.FromEnvironment())
    {
    }

    internal OctopusImportConfirmationRecoveryService(IOctopusImportSessionDataProvider dataProvider, IOctopusImportTemporaryUploadSettings settings, TimeProvider time, OctopusImportConfirmationResilienceOptions resilience)
    {
        _dataProvider = dataProvider;
        _settings = settings;
        _time = time;
        _resilience = resilience;
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var cutoffs = Cutoffs(now);

        var interrupted = await _dataProvider.GetInterruptedConfirmationsAsync(cutoffs, _settings.CleanupBatchSize, ct).ConfigureAwait(false);
        var recovered = await RecoverAllAsync(interrupted, cutoffs, now, ct).ConfigureAwait(false);

        if (recovered > 0) Log.Warning("Recovered {Count} interrupted Octopus import confirmation(s) whose heartbeat is older than {HeartbeatStale}", recovered, cutoffs.HeartbeatStale);

        return recovered;
    }

    // The heartbeat cut-off is an age the database measures on its own clock; only the legacy rule uses this server's clock (hours of grace).
    internal OctopusImportInterruptedConfirmationCutoffs Cutoffs(DateTimeOffset now) => new(_resilience.HeartbeatStale, now.Subtract(_settings.InterruptedImportGracePeriod));

    private async Task<int> RecoverAllAsync(IReadOnlyList<OctopusImportInterruptedConfirmation> interrupted, OctopusImportInterruptedConfirmationCutoffs cutoffs, DateTimeOffset now, CancellationToken ct)
    {
        var recovered = 0;

        foreach (var session in interrupted)
            recovered += await _dataProvider.RecoverInterruptedConfirmationAsync(session, cutoffs, InterruptedFailure(session, now), now, ct).ConfigureAwait(false);

        return recovered;
    }

    private static string InterruptedFailure(OctopusImportInterruptedConfirmation session, DateTimeOffset now)
        => OctopusImportSessionService.SerializeFailure(new OctopusImportConfirmationFailureDto { AttemptId = session.AttemptId ?? Guid.NewGuid(), FailedAt = now, Reason = OctopusImportConfirmationFailureReason.Interrupted, Transient = true, Attempts = 1, Diagnostics = [InterruptedDiagnostic()] });

    private static OctopusImportDiagnosticDto InterruptedDiagnostic()
        => new() { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportConfirmationDiagnosticCodes.ConfirmationInterrupted, Message = "The server running this import stopped before the import was saved, so nothing was imported. Import again to retry the same plan." };
}
