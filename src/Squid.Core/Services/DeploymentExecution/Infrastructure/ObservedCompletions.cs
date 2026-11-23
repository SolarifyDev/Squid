using System.Collections.Concurrent;
using Squid.Core.Halibut.Resilience;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

/// <summary>
/// The "the script already finished, only the agent-side release is unconfirmed" rule shared by the retrying script clients of every
/// protocol version. A GetStatus that reported a real completion is remembered per ticket; when CompleteScript then cannot be
/// confirmed because the transport keeps failing, that remembered outcome stands in for the answer instead of the failure
/// propagating — rethrowing would pause the deployment, and a resume would find the ticket released and have no way to tell a
/// finished script from a lost one. Only the version-specific shape of the substitute answer stays with each client.
/// </summary>
internal sealed class ObservedCompletions<TObserved>
{
    private readonly ConcurrentDictionary<string, TObserved> _observed = new();

    public void Remember(string ticketId, TObserved observed) => _observed[ticketId] = observed;

    public bool TryGet(string ticketId, out TObserved observed) => _observed.TryGetValue(ticketId, out observed);

    /// <summary>True when <paramref name="failure"/> is a transport blip AND the ticket's completion was already observed.</summary>
    public bool CanSubstituteFor(Exception failure, string ticketId) => TransientFailureClassifier.IsTransient(failure) && _observed.ContainsKey(ticketId);

    public TObserved Get(string ticketId) => _observed[ticketId];
}
