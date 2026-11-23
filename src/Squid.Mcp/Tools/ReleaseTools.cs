using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Net;
using ModelContextProtocol.Protocol;
using Squid.Message.Requests.Mcp;

namespace Squid.Mcp.Tools;

internal static class ReleaseTools
{
    internal const string ListReleasesName = "list_releases";

    internal static ReadToolRegistration ListReleasesTool { get; } = new(
        CreateListReleasesTool(),
        ReadToolSort.CreatedAt,
        InvokeListReleasesAsync);

    private static Tool CreateListReleasesTool()
    {
        return ReadToolSchemas.Create(
            ListReleasesName,
            "List visible releases for a project.",
            ReadToolSchemas.Input(
                ("spaceId", ReadToolSchemas.IdSchema()),
                ("projectId", ReadToolSchemas.IdSchema()),
                ("pageSize", ReadToolSchemas.PageSizeSchema()),
                ("cursor", ReadToolSchemas.CursorSchema())),
            ReadToolSchemas.ListOutput(ReleaseItemSchema()),
            "spaceId", "projectId");
    }

    private static async ValueTask<CallToolResult> InvokeListReleasesAsync(
        ReadToolRegistration tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit)
    {
        if (!ReadToolSupport.OnlyArguments(arguments, "spaceId", "projectId", "pageSize", "cursor") ||
            !ReadToolSupport.TryRequiredInt(arguments, "spaceId", out var spaceId) ||
            !ReadToolSupport.TryRequiredInt(arguments, "projectId", out var projectId) ||
            !ReadToolSupport.TryPageSize(arguments, out var pageSize) ||
            !ReadToolSupport.TryCursor(arguments, out var cursor))
        {
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);
        }

        audit?.SetRelatedId(projectId);
        var scope = ReadToolCursor.QueryScope(spaceId, projectId);
        if (!ReadToolSupport.TryPosition(cursor, tool, scope, out var after))
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);

        var response = await mediator.RequestAsync<ListVisibleReleasesRequest, ListVisibleReleasesResponse>(
            new ListVisibleReleasesRequest
            {
                SpaceId = spaceId,
                ProjectId = projectId,
                PageSize = pageSize,
                After = after
            }, cancellationToken).ConfigureAwait(false);

        if (ReadToolSupport.TryError(response, out var error)) return error;
        if (response.Data == null)
            return ReadToolSupport.Error(ReadToolErrors.NotFoundOrForbidden,
                ReadToolErrors.NotFoundOrForbiddenMessage);

        var items = response.Data.Items.Select(item =>
            new ReleaseOutput(item.Id, item.Version, item.ChannelId, item.CreatedAt)).ToArray();
        return ReadToolSupport.Success(
            $"Returned {items.Length} releases.",
            new ListReleasesOutput(items, ReadToolSupport.NextCursor(response.Data.NextPosition, tool, scope)));
    }

    private static JsonObject ReleaseItemSchema()
    {
        return ReadToolSchemas.ObjectSchema(new JsonObject
        {
            ["id"] = ReadToolSchemas.IdSchema(),
            ["version"] = ReadToolSchemas.StringSchema(),
            ["channelId"] = ReadToolSchemas.IdSchema(),
            ["createdAt"] = ReadToolSchemas.DateTimeSchema()
        }, ["id", "version", "channelId", "createdAt"]);
    }

    private sealed record ListReleasesOutput(IReadOnlyList<ReleaseOutput> Items, string NextCursor);

    private sealed record ReleaseOutput(int Id, string Version, int ChannelId, DateTimeOffset CreatedAt);
}
