using Squid.Message.Response;

namespace Squid.Message.Requests.Mcp;

public interface IScopedReadQuery
{
}

public sealed record ScopedReadPosition(int Id, DateTimeOffset? CreatedAt);

public sealed record ScopedReadPage<T>(IReadOnlyList<T> Items, ScopedReadPosition? NextPosition);

public sealed record SpaceReadItem(int Id, string Name);

public sealed record ProjectReadItem(int Id, string Name, string Slug);

public sealed record EnvironmentReadItem(int Id, string Name);

public sealed record ReleaseReadItem(int Id, string Version, int ChannelId, DateTimeOffset CreatedAt);

public sealed class ListVisibleSpacesRequest : IRequest, IScopedReadQuery
{
    public int PageSize { get; set; } = 20;
    public ScopedReadPosition? After { get; set; }
}

public sealed class ListVisibleProjectsRequest : IRequest, IScopedReadQuery
{
    public int SpaceId { get; set; }
    public string? Keyword { get; set; }
    public int PageSize { get; set; } = 20;
    public ScopedReadPosition? After { get; set; }
}

public sealed class ListVisibleEnvironmentsRequest : IRequest, IScopedReadQuery
{
    public int SpaceId { get; set; }
    public int ProjectId { get; set; }
    public string? Keyword { get; set; }
    public int PageSize { get; set; } = 20;
    public ScopedReadPosition? After { get; set; }
}

public sealed class ListVisibleReleasesRequest : IRequest, IScopedReadQuery
{
    public int SpaceId { get; set; }
    public int ProjectId { get; set; }
    public int PageSize { get; set; } = 20;
    public ScopedReadPosition? After { get; set; }
}

public sealed class ListVisibleSpacesResponse : SquidResponse<ScopedReadPage<SpaceReadItem>>;

public sealed class ListVisibleProjectsResponse : SquidResponse<ScopedReadPage<ProjectReadItem>>;

public sealed class ListVisibleEnvironmentsResponse : SquidResponse<ScopedReadPage<EnvironmentReadItem>>;

public sealed class ListVisibleReleasesResponse : SquidResponse<ScopedReadPage<ReleaseReadItem>>;
