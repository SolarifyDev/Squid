using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Confirms an owner's validated Octopus import session: reloads it by owner, re-checks it, rebuilds the plan,
/// preflights the owner's permissions and hands it to the orchestrator. The caller's scope must act as the owner.
/// </summary>
public interface IOctopusImportConfirmationRunner : IScopedDependency
{
    /// <summary>Confirms the session now and returns it as it ended (or as it already was, when it had already run).</summary>
    Task<OctopusImportConfirmationOutcome> RunAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct = default);

    /// <summary>Applies the same cheap checks as <see cref="RunAsync"/>, then queues the confirmation as a background job run as the owner.</summary>
    Task<OctopusImportConfirmationOutcome> EnqueueAsync(Guid sessionId, int spaceId, int ownerUserId, CancellationToken ct = default);
}
