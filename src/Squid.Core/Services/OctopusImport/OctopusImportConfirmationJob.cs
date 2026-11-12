using Squid.Core.Services.Identity;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>The background job a queued confirmation runs as: the runner, inside a scope that acts as the session owner.</summary>
public interface IOctopusImportConfirmationJob : IScopedDependency
{
    Task RunAsOwnerAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct);
}

public sealed class OctopusImportConfirmationJob(ILifetimeScope lifetimeScope) : IOctopusImportConfirmationJob
{
    public async Task RunAsOwnerAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct)
    {
        var rejection = await RunCapturingRejectionAsync(sessionId, spaceId, ownerUserId, ct).ConfigureAwait(false);

        if (rejection == null) return;

        await RecordRejectionAsync(sessionId, spaceId, ownerUserId, rejection).ConfigureAwait(false);
    }

    private async Task<OctopusImportDiagnosticDto> RunCapturingRejectionAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct)
    {
        try
        {
            await using var scope = BeginOwnerScope(ownerUserId);

            var outcome = await scope.Resolve<IOctopusImportConfirmationRunner>().RunAsync(sessionId, spaceId, ownerUserId, ct).ConfigureAwait(false);

            return outcome.Rejection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Background Octopus import confirmation {SessionId} failed before the import started", sessionId);

            return new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportConfirmationDiagnosticCodes.BackgroundConfirmationFailed, Message = $"The background Octopus import confirmation failed before the import started: {ex.Message}" };
        }
    }

    // Nobody waits on the request that queued this run: a refusal that leaves the session Validated would keep the
    // caller polling forever, so end the session with the reason. A session the orchestrator already ended is left alone.
    private async Task RecordRejectionAsync(Guid sessionId, int spaceId, int ownerUserId, OctopusImportDiagnosticDto rejection)
    {
        await using var scope = BeginOwnerScope(ownerUserId);

        var sessions = scope.Resolve<IOctopusImportSessionService>();
        var current = await sessions.GetSessionAsync(sessionId, spaceId, CancellationToken.None).ConfigureAwait(false);

        if (current.State != OctopusImportSessionState.Validated) return;

        await sessions.RecordResultAsync(sessionId, spaceId, OctopusImportSessionState.Failed, new OctopusImportSessionResultDto { Diagnostics = [rejection] }, CancellationToken.None).ConfigureAwait(false);
    }

    private ILifetimeScope BeginOwnerScope(int ownerUserId) => lifetimeScope.BeginLifetimeScope(builder => builder.RegisterInstance<ICurrentUser>(new OctopusImportSessionOwner(ownerUserId)));
}
