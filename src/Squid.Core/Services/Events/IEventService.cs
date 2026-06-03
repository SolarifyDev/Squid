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
    Task RecordAsync(RecordEventRequest request, CancellationToken cancellationToken = default);

    Task<GetEventsResponseData> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default);
}
