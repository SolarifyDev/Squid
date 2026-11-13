using Squid.Core.Services.Authorization;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public sealed partial class OctopusImportConfirmationRunner(IOctopusImportSessionDataProvider sessionDataProvider, IOctopusImportSessionService sessionService, IAuthorizationService authorizationService, IOctopusImportPlanningPipeline planningPipeline, IOctopusImportConfirmationOrchestrator confirmationOrchestrator, ISquidBackgroundJobClient backgroundJobClient, IOctopusImportExportSecretStore secretStore = null, IOctopusExportSecretsAccessor secretsAccessor = null) : IOctopusImportConfirmationRunner
{
    private RunContext _ctx;

    public async Task<OctopusImportConfirmationOutcome> RunAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct = default)
    {
        _ctx = new RunContext(sessionId, spaceId, ownerUserId);

        try
        {
            await LoadOwnedSessionAsync(ct).ConfigureAwait(false);

            if (IsSettled()) return await CurrentOutcomeAsync(ct).ConfigureAwait(false);

            await EnsureConfirmableAsync(ct).ConfigureAwait(false);
            await BuildSnapshotAsync(ct).ConfigureAwait(false);
            await EnsureImportPermissionsAsync(ct).ConfigureAwait(false);

            return await ConfirmAsync(ct).ConfigureAwait(false);
        }
        catch (OctopusImportConfirmationRejectedException ex)
        {
            return Rejected(ex);
        }
    }

    public async Task<OctopusImportConfirmationOutcome> EnqueueAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct = default)
    {
        _ctx = new RunContext(sessionId, spaceId, ownerUserId);

        try
        {
            await LoadOwnedSessionAsync(ct).ConfigureAwait(false);

            if (IsSettled()) return await CurrentOutcomeAsync(ct).ConfigureAwait(false);

            await EnsureConfirmableAsync(ct).ConfigureAwait(false);
            EnqueueBackgroundRun();

            return await CurrentOutcomeAsync(ct).ConfigureAwait(false);
        }
        catch (OctopusImportConfirmationRejectedException ex)
        {
            return Rejected(ex);
        }
    }

    // Terminal, or another confirmation already holds it: report the session as it is instead of confirming twice.
    private bool IsSettled() => OctopusImportSessionStateMachine.IsTerminal(_ctx.State) || _ctx.State == OctopusImportSessionState.Importing;

    private async Task<OctopusImportConfirmationOutcome> CurrentOutcomeAsync(CancellationToken ct)
        => new(await sessionService.GetSessionAsync(_ctx.SessionId, _ctx.SpaceId, ct).ConfigureAwait(false));

    private async Task<OctopusImportConfirmationOutcome> ConfirmAsync(CancellationToken ct)
    {
        var request = new OctopusImportConfirmationRequest(_ctx.SessionId, _ctx.SpaceId, _ctx.Snapshot.Graph, _ctx.Snapshot.DependencyPlan, _ctx.ValidatedPlan.PreviewPlan) { SessionDirectory = Path.GetDirectoryName(_ctx.Session.TemporaryUploadPath) };

        return new OctopusImportConfirmationOutcome(await confirmationOrchestrator.ConfirmAsync(request, ct).ConfigureAwait(false));
    }

    // The job reopens a scope as the owner: Hangfire scopes run as the internal user, which owns no import session.
    private void EnqueueBackgroundRun()
    {
        var (sessionId, spaceId, ownerUserId) = (_ctx.SessionId, _ctx.SpaceId, _ctx.OwnerUserId);

        backgroundJobClient.Enqueue<IOctopusImportConfirmationJob>(job => job.RunAsOwnerAsync(sessionId, spaceId, ownerUserId, CancellationToken.None));
    }

    private OctopusImportConfirmationOutcome Rejected(OctopusImportConfirmationRejectedException ex)
    {
        var session = new OctopusImportSessionDto { SessionId = _ctx.SessionId, DestinationSpaceId = _ctx.SpaceId, State = _ctx.State };
        var rejection = new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = ex.Code, Message = ex.Message };

        return new OctopusImportConfirmationOutcome(session, rejection);
    }
}
