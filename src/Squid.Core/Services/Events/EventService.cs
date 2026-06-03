using System.Text.Json;
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
        await _repository.InsertAsync(BuildEvent(request), cancellationToken).ConfigureAwait(false);
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

    private Event BuildEvent(RecordEventRequest request) => new()
    {
        Category = request.Category,
        ReferencesJson = Serialize(request.References),
        SpaceId = request.SpaceId,
        ProjectId = request.ProjectId,
        ReleaseId = request.ReleaseId,
        DeploymentId = request.DeploymentId,
        EnvironmentId = request.EnvironmentId,
        MachineId = request.MachineId,
        ServerTaskId = request.ServerTaskId,
        UserId = ResolveUserId(),
        Username = ResolveUsername(),
        // established-with + user-agent need the HTTP auth scheme / request headers;
        // PR-4 (provenance polish) resolves those via a dedicated resolver. A real
        // portal/API actor is still attributed via ICurrentUser (ResolveUserId/Name).
        EstablishedWith = _currentUser.IsInternal ? EventIdentityEstablishedWith.Server : EventIdentityEstablishedWith.SessionCookie,
        UserAgent = null,
        Occurred = DateTimeOffset.UtcNow
    };

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

    private static string Serialize(object? references) =>
        references is null ? "{}" : JsonSerializer.Serialize(references);

    private int? ResolveUserId() => _currentUser.IsInternal ? null : _currentUser.Id;

    private string ResolveUsername() =>
        _currentUser.IsInternal || string.IsNullOrWhiteSpace(_currentUser.Name) ? "system" : _currentUser.Name;

    private static string DescribeEstablishedWith(EventIdentityEstablishedWith value) => value switch
    {
        EventIdentityEstablishedWith.SessionCookie => "Session cookie",
        EventIdentityEstablishedWith.ApiKey => "API key",
        EventIdentityEstablishedWith.Cli => "Command-line or tools",
        _ => "Server"
    };
}
