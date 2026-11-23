using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Net;
using ModelContextProtocol.Protocol;
using Squid.Message.Requests.Mcp;

namespace Squid.Mcp.Tools;

internal static class DiscoveryTools
{
    internal const string ListSpacesName = "list_spaces";
    internal const string ListProjectsName = "list_projects";
    internal const string ListEnvironmentsName = "list_environments";

    internal static ReadToolRegistration ListSpacesTool { get; } = new(
        CreateListSpacesTool(),
        ReadToolSort.Id,
        InvokeListSpacesAsync);

    internal static ReadToolRegistration ListProjectsTool { get; } = new(
        CreateListProjectsTool(),
        ReadToolSort.Id,
        InvokeListProjectsAsync);

    internal static ReadToolRegistration ListEnvironmentsTool { get; } = new(
        CreateListEnvironmentsTool(),
        ReadToolSort.Id,
        InvokeListEnvironmentsAsync);

    private static Tool CreateListSpacesTool()
    {
        return ReadToolSchemas.Create(
            ListSpacesName,
            "List spaces that contain at least one visible project.",
            ReadToolSchemas.Input(
                ("pageSize", ReadToolSchemas.PageSizeSchema()),
                ("cursor", ReadToolSchemas.CursorSchema())),
            ReadToolSchemas.ListOutput(SpaceItemSchema()));
    }

    private static Tool CreateListProjectsTool()
    {
        return ReadToolSchemas.Create(
            ListProjectsName,
            "List visible projects in a space, optionally filtered by name or slug.",
            ReadToolSchemas.Input(
                ("spaceId", ReadToolSchemas.IdSchema()),
                ("keyword", ReadToolSchemas.KeywordSchema()),
                ("pageSize", ReadToolSchemas.PageSizeSchema()),
                ("cursor", ReadToolSchemas.CursorSchema())),
            ReadToolSchemas.ListOutput(ProjectItemSchema()),
            "spaceId");
    }

    private static Tool CreateListEnvironmentsTool()
    {
        return ReadToolSchemas.Create(
            ListEnvironmentsName,
            "List visible environments for a project in a space.",
            ReadToolSchemas.Input(
                ("spaceId", ReadToolSchemas.IdSchema()),
                ("projectId", ReadToolSchemas.IdSchema()),
                ("keyword", ReadToolSchemas.KeywordSchema()),
                ("pageSize", ReadToolSchemas.PageSizeSchema()),
                ("cursor", ReadToolSchemas.CursorSchema())),
            ReadToolSchemas.ListOutput(SimpleNamedItemSchema()),
            "spaceId", "projectId");
    }

    private static async ValueTask<CallToolResult> InvokeListSpacesAsync(
        ReadToolRegistration tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit)
    {
        if (!ReadToolSupport.OnlyArguments(arguments, "pageSize", "cursor") ||
            !ReadToolSupport.TryPageSize(arguments, out var pageSize) ||
            !ReadToolSupport.TryCursor(arguments, out var cursor))
        {
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);
        }

        var scope = ReadToolCursor.QueryScope();
        if (!ReadToolSupport.TryPosition(cursor, tool, scope, out var after))
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);

        var response = await mediator.RequestAsync<ListVisibleSpacesRequest, ListVisibleSpacesResponse>(
            new ListVisibleSpacesRequest { PageSize = pageSize, After = after }, cancellationToken).ConfigureAwait(false);

        if (ReadToolSupport.TryError(response, out var error)) return error;
        if (response.Data == null)
            return ReadToolSupport.Error(ReadToolErrors.NotFoundOrForbidden,
                ReadToolErrors.NotFoundOrForbiddenMessage);

        var items = response.Data.Items.Select(item => new SpaceOutput(item.Id, item.Name)).ToArray();
        return ReadToolSupport.Success(
            $"Returned {items.Length} spaces.",
            new ListSpacesOutput(items, ReadToolSupport.NextCursor(response.Data.NextPosition, tool, scope)));
    }

    private static async ValueTask<CallToolResult> InvokeListProjectsAsync(
        ReadToolRegistration tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit)
    {
        if (!ReadToolSupport.OnlyArguments(arguments, "spaceId", "keyword", "pageSize", "cursor") ||
            !ReadToolSupport.TryRequiredInt(arguments, "spaceId", out var spaceId) ||
            !ReadToolSupport.TryOptionalString(arguments, "keyword", out var keyword) ||
            !ReadToolSupport.TryPageSize(arguments, out var pageSize) ||
            !ReadToolSupport.TryCursor(arguments, out var cursor))
        {
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);
        }

        audit?.SetRelatedId(spaceId);
        keyword = ReadToolSupport.NormalizeKeyword(keyword);
        var scope = ReadToolCursor.QueryScope(spaceId, keyword: keyword);
        if (!ReadToolSupport.TryPosition(cursor, tool, scope, out var after))
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);

        var response = await mediator.RequestAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            new ListVisibleProjectsRequest
            {
                SpaceId = spaceId,
                Keyword = keyword,
                PageSize = pageSize,
                After = after
            }, cancellationToken).ConfigureAwait(false);

        if (ReadToolSupport.TryError(response, out var error)) return error;
        if (response.Data == null)
            return ReadToolSupport.Error(ReadToolErrors.NotFoundOrForbidden,
                ReadToolErrors.NotFoundOrForbiddenMessage);

        var items = response.Data.Items.Select(item => new ProjectOutput(item.Id, item.Name, item.Slug)).ToArray();
        return ReadToolSupport.Success(
            $"Returned {items.Length} projects.",
            new ListProjectsOutput(items, ReadToolSupport.NextCursor(response.Data.NextPosition, tool, scope)));
    }

    private static async ValueTask<CallToolResult> InvokeListEnvironmentsAsync(
        ReadToolRegistration tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit)
    {
        if (!ReadToolSupport.OnlyArguments(arguments, "spaceId", "projectId", "keyword", "pageSize", "cursor") ||
            !ReadToolSupport.TryRequiredInt(arguments, "spaceId", out var spaceId) ||
            !ReadToolSupport.TryRequiredInt(arguments, "projectId", out var projectId) ||
            !ReadToolSupport.TryOptionalString(arguments, "keyword", out var keyword) ||
            !ReadToolSupport.TryPageSize(arguments, out var pageSize) ||
            !ReadToolSupport.TryCursor(arguments, out var cursor))
        {
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);
        }

        audit?.SetRelatedId(projectId);
        keyword = ReadToolSupport.NormalizeKeyword(keyword);
        var scope = ReadToolCursor.QueryScope(spaceId, projectId, keyword: keyword);
        if (!ReadToolSupport.TryPosition(cursor, tool, scope, out var after))
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);

        var response = await mediator.RequestAsync<ListVisibleEnvironmentsRequest, ListVisibleEnvironmentsResponse>(
            new ListVisibleEnvironmentsRequest
            {
                SpaceId = spaceId,
                ProjectId = projectId,
                Keyword = keyword,
                PageSize = pageSize,
                After = after
            }, cancellationToken).ConfigureAwait(false);

        if (ReadToolSupport.TryError(response, out var error)) return error;
        if (response.Data == null)
            return ReadToolSupport.Error(ReadToolErrors.NotFoundOrForbidden,
                ReadToolErrors.NotFoundOrForbiddenMessage);

        var items = response.Data.Items.Select(item => new EnvironmentOutput(item.Id, item.Name)).ToArray();
        return ReadToolSupport.Success(
            $"Returned {items.Length} environments.",
            new ListEnvironmentsOutput(items, ReadToolSupport.NextCursor(response.Data.NextPosition, tool, scope)));
    }

    private static JsonObject SpaceItemSchema()
    {
        return ReadToolSchemas.ObjectSchema(new JsonObject
        {
            ["id"] = ReadToolSchemas.IdSchema(),
            ["name"] = ReadToolSchemas.StringSchema()
        }, ["id", "name"]);
    }

    private static JsonObject ProjectItemSchema()
    {
        return ReadToolSchemas.ObjectSchema(new JsonObject
        {
            ["id"] = ReadToolSchemas.IdSchema(),
            ["name"] = ReadToolSchemas.StringSchema(),
            ["slug"] = ReadToolSchemas.StringSchema()
        }, ["id", "name", "slug"]);
    }

    private static JsonObject SimpleNamedItemSchema()
    {
        return ReadToolSchemas.ObjectSchema(new JsonObject
        {
            ["id"] = ReadToolSchemas.IdSchema(),
            ["name"] = ReadToolSchemas.StringSchema()
        }, ["id", "name"]);
    }

    private sealed record ListSpacesOutput(IReadOnlyList<SpaceOutput> Items, string NextCursor);

    private sealed record SpaceOutput(int Id, string Name);

    private sealed record ListProjectsOutput(IReadOnlyList<ProjectOutput> Items, string NextCursor);

    private sealed record ProjectOutput(int Id, string Name, string Slug);

    private sealed record ListEnvironmentsOutput(IReadOnlyList<EnvironmentOutput> Items, string NextCursor);

    private sealed record EnvironmentOutput(int Id, string Name);
}
