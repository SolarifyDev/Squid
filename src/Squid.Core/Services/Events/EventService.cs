using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Identity;
using Squid.Message.Constants.Events;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Events;
using Squid.Message.Requests.Events;

namespace Squid.Core.Services.Events;

public class EventService : IEventService
{
    private const int MaxPageSize = 100;

    private readonly IRepository _repository;
    private readonly ICurrentUser _currentUser;

    public EventService(IRepository repository, ICurrentUser currentUser)
    {
        _repository = repository;
        _currentUser = currentUser;
    }

    public async Task RecordAsync(RecordEventRequest request, CancellationToken cancellationToken = default)
    {
        // Document snapshots (the EventDocumentSnapshot side table) are written by
        // the document-audit path in a later step, where the saved event id is
        // available to link them. PR-1 records the event itself.
        await _repository.InsertAsync(EventFactory.Build(request, _currentUser), cancellationToken).ConfigureAwait(false);
    }

    public async Task<GetEventsResponseData> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.SpaceId.HasValue) return new GetEventsResponseData();

        var take = Math.Clamp(request.Take, 1, MaxPageSize);

        var rows = await BuildFilteredQuery(request)
            .OrderByDescending(e => e.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildPage(rows, take);
    }

    public async Task<int> PruneByServerTaskIdsAsync(IReadOnlyCollection<int> serverTaskIds, CancellationToken cancellationToken = default)
    {
        if (serverTaskIds is null || serverTaskIds.Count == 0) return 0;

        // event_document_snapshot rows cascade via the FK (ON DELETE CASCADE).
        return await _repository.ExecuteDeleteAsync<Event>(e => e.ServerTaskId != null && serverTaskIds.Contains(e.ServerTaskId.Value), cancellationToken).ConfigureAwait(false);
    }

    private IQueryable<Event> BuildFilteredQuery(GetEventsRequest request)
    {
        var query = _repository.QueryNoTracking<Event>(e => e.SpaceId == request.SpaceId!.Value);

        if (request.ProjectId.HasValue) query = query.Where(e => e.ProjectId == request.ProjectId);
        if (request.ReleaseId.HasValue) query = query.Where(e => e.ReleaseId == request.ReleaseId);
        if (request.DeploymentId.HasValue) query = query.Where(e => e.DeploymentId == request.DeploymentId);
        if (request.EnvironmentId.HasValue) query = query.Where(e => e.EnvironmentId == request.EnvironmentId);
        if (request.MachineId.HasValue) query = query.Where(e => e.MachineId == request.MachineId);
        if (request.BeforeId.HasValue) query = query.Where(e => e.Id < request.BeforeId.Value);

        return query;
    }

    private static GetEventsResponseData BuildPage(List<Event> rows, int take)
    {
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;

        return new GetEventsResponseData
        {
            Events = page.Select(ToDto).ToList(),
            HasMore = hasMore,
            NextCursor = hasMore && page.Count > 0 ? page[^1].Id : null
        };
    }

    private static EventDto ToDto(Event e)
    {
        var descriptor = EventCategoryRegistry.Describe(e.Category);

        return new EventDto
        {
            Id = e.Id,
            Category = (int)e.Category,
            CategoryName = descriptor.DisplayName,
            MessageTemplate = descriptor.MessageTemplate,
            ReferencesJson = e.ReferencesJson,
            SpaceId = e.SpaceId,
            ProjectId = e.ProjectId,
            ReleaseId = e.ReleaseId,
            DeploymentId = e.DeploymentId,
            EnvironmentId = e.EnvironmentId,
            MachineId = e.MachineId,
            ServerTaskId = e.ServerTaskId,
            UserId = e.UserId,
            Username = e.Username,
            EstablishedWith = (int)e.EstablishedWith,
            EstablishedWithName = DescribeEstablishedWith(e.EstablishedWith),
            UserAgent = e.UserAgent,
            Occurred = e.Occurred
        };
    }

    private static string DescribeEstablishedWith(EventIdentityEstablishedWith value) => value switch
    {
        EventIdentityEstablishedWith.SessionCookie => "Session cookie",
        EventIdentityEstablishedWith.ApiKey => "API key",
        EventIdentityEstablishedWith.Cli => "Command-line or tools",
        _ => "Server"
    };
}
