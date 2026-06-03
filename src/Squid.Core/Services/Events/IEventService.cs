using Squid.Core.Persistence.Db;
using Squid.Message.Models.Events;
using Squid.Message.Requests.Events;

namespace Squid.Core.Services.Events;

/// <summary>
/// Owns the persisted audit-event ("history") stream: recording events (with
/// provenance resolved from the ambient request context) and reading the
/// keyset-paginated feed for a document. Handlers stay thin and delegate here.
/// </summary>
public interface IEventService : IScopedDependency
{
    /// <summary>
    /// Low-level primitive that persists one audit event. It is intended to be
    /// called from the TWO central, generic emission points ONLY — never sprinkled
    /// across handlers/services:
    /// <list type="bullet">
    ///   <item>the EF Core SaveChanges interceptor (document Created/Modified/Deleted), and</item>
    ///   <item>the deployment lifecycle audit handler (one <c>IDeploymentLifecycleHandler</c>
    ///   that subscribes to the pipeline's existing lifecycle events and maps each milestone
    ///   to a category — no producer changes, no scattered calls).</item>
    /// </list>
    /// Keeping emission centralized is what makes the audit stream generic and
    /// maintainable; do not add ad-hoc RecordAsync calls in feature code.
    /// </summary>
    Task RecordAsync(RecordEventRequest request, CancellationToken cancellationToken = default);

    Task<GetEventsResponseData> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default);
}
