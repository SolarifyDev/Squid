using Squid.Core.Persistence.Db;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>The background job a queued confirmation runs as: the runner, inside a scope that acts as the session owner.</summary>
public interface IOctopusImportConfirmationJob : IScopedDependency
{
    /// <summary>A job queued by an older server, which carries no data version: kept so those jobs still deserialize and run.</summary>
    Task RunAsOwnerAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct);

    /// <summary>Runs only while the session still holds <paramref name="admittedDataVersion"/>, the version confirmed when it was queued.</summary>
    Task RunAsOwnerAsync(Guid sessionId, int spaceId, int ownerUserId, byte[] admittedDataVersion, CancellationToken ct);
}

public sealed class OctopusImportConfirmationJob(ILifetimeScope lifetimeScope) : IOctopusImportConfirmationJob
{
    public Task RunAsOwnerAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct) => RunAsOwnerAsync(sessionId, spaceId, ownerUserId, null, ct);

    public async Task RunAsOwnerAsync(Guid sessionId, int spaceId, int ownerUserId, byte[] admittedDataVersion, CancellationToken ct)
    {
        var rejection = await RunCapturingRejectionAsync(sessionId, spaceId, ownerUserId, admittedDataVersion, ct).ConfigureAwait(false);

        if (rejection == null) return;

        await RecordRejectionAsync(sessionId, spaceId, ownerUserId, rejection).ConfigureAwait(false);
    }

    private async Task<OctopusImportDiagnosticDto> RunCapturingRejectionAsync(Guid sessionId, int spaceId, int ownerUserId, byte[] admittedDataVersion, CancellationToken ct)
    {
        try
        {
            await using var scope = BeginOwnerScope(ownerUserId);

            var outcome = await scope.Resolve<IOctopusImportConfirmationRunner>().RunAsync(sessionId, spaceId, ownerUserId, admittedDataVersion, ct).ConfigureAwait(false);

            return outcome.Rejection;
        }
        catch (OctopusImportSessionExpiredException ex)
        {
            return MaxAgeReached(ex);
        }
        catch (Exception ex) when (PostgresTransientFailureClassifier.IsTransient(ex))
        {
            Log.Warning(ex, "Background Octopus import confirmation {SessionId} did not start because of a transient database failure; the session stays Validated", sessionId);

            return NotStarted(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Background Octopus import confirmation {SessionId} failed before the import started", sessionId);

            // The message may come from third-party code (a database driver): it reaches the session owner only through the redaction.
            return OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportConfirmationDiagnosticCodes.BackgroundConfirmationFailed, Message = $"The background Octopus import confirmation failed before the import started: {ex.Message}" });
        }
    }

    // Nobody waits on the request that queued this run: a refusal that leaves the session Validated would keep the
    // caller polling forever, so end the session with the reason. A session the orchestrator already ended is left alone,
    // and so is one whose plan was replaced meanwhile (validated again, selection changed): that newer plan is still importable.
    // A retryable refusal (the import rolled back and the session is Validated again with the reason) is left as it is: the page offers
    // "Import again". A transient failure before the import started is recorded the same way, without ending the session.
    private async Task RecordRejectionAsync(Guid sessionId, int spaceId, int ownerUserId, OctopusImportDiagnosticDto rejection)
    {
        if (rejection.Code is OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded or OctopusImportConfirmationDiagnosticCodes.ConfirmationRetryable) return;

        await using var scope = BeginOwnerScope(ownerUserId);

        var sessions = scope.Resolve<IOctopusImportSessionService>();
        var current = await sessions.GetSessionAsync(sessionId, spaceId, CancellationToken.None).ConfigureAwait(false);

        if (current.State != OctopusImportSessionState.Validated) return;

        if (rejection.Code == OctopusImportConfirmationDiagnosticCodes.ConfirmationNotStarted)
        {
            await sessions.RecordConfirmationNotStartedAsync(sessionId, spaceId, NotStartedFailure(rejection), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await sessions.RecordResultAsync(sessionId, spaceId, TerminalStateFor(rejection), new OctopusImportSessionResultDto { Diagnostics = [rejection] }, CancellationToken.None).ConfigureAwait(false);
    }

    // Only the exception's type and SQLSTATE reach the session owner: the message may come from the database driver.
    private static OctopusImportDiagnosticDto NotStarted(Exception ex) => new() { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportConfirmationDiagnosticCodes.ConfirmationNotStarted, Message = $"The import did not start because the database failed transiently ({ex.GetType().Name}, SQLSTATE {PostgresTransientFailureClassifier.SqlStateOf(ex) ?? "none"}). Nothing was imported; import again." };

    private static OctopusImportConfirmationFailureDto NotStartedFailure(OctopusImportDiagnosticDto rejection) => new() { AttemptId = Guid.NewGuid(), FailedAt = DateTimeOffset.UtcNow, Reason = OctopusImportConfirmationFailureReason.NotStarted, Transient = true, Attempts = 0, Diagnostics = [rejection] };

    // The session reached its maximum age before the run started: it ends Expired with that reason, as the cleanup sweep would end it.
    private static OctopusImportDiagnosticDto MaxAgeReached(OctopusImportSessionExpiredException ex) => new() { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportSessionExpiry.MaxAgeReachedCode, Message = ex.Message };

    private static OctopusImportSessionState TerminalStateFor(OctopusImportDiagnosticDto rejection) => rejection.Code == OctopusImportSessionExpiry.MaxAgeReachedCode ? OctopusImportSessionState.Expired : OctopusImportSessionState.Failed;

    private ILifetimeScope BeginOwnerScope(int ownerUserId) => lifetimeScope.BeginLifetimeScope(builder => builder.RegisterInstance<ICurrentUser>(new OctopusImportSessionOwner(ownerUserId)));
}
