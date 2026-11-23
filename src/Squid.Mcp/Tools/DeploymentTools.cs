using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Net;
using ModelContextProtocol.Protocol;
using Squid.Message.Requests.Mcp;

namespace Squid.Mcp.Tools;

internal static class DeploymentTools
{
    internal const string ListDeploymentsName = "list_deployments";
    internal const string GetTaskStatusName = "get_task_status";

    internal static ReadToolRegistration ListDeploymentsTool { get; } = new(
        CreateListDeploymentsTool(),
        ReadToolSort.CreatedAt,
        InvokeListDeploymentsAsync);

    internal static ReadToolRegistration GetTaskStatusTool { get; } = new(
        CreateGetTaskStatusTool(),
        ReadToolSort.None,
        InvokeGetTaskStatusAsync);

    private static Tool CreateListDeploymentsTool()
    {
        return ReadToolSchemas.Create(
            ListDeploymentsName,
            "List visible deployment attempts and the latest successful deployment.",
            ReadToolSchemas.Input(
                ("spaceId", ReadToolSchemas.IdSchema()),
                ("projectId", ReadToolSchemas.IdSchema()),
                ("environmentId", ReadToolSchemas.IdSchema()),
                ("pageSize", ReadToolSchemas.PageSizeSchema()),
                ("cursor", ReadToolSchemas.CursorSchema())),
            DeploymentListOutput(),
            "spaceId", "projectId", "environmentId");
    }

    private static Tool CreateGetTaskStatusTool()
    {
        return ReadToolSchemas.Create(
            GetTaskStatusName,
            "Get the status of a task associated with a visible deployment.",
            ReadToolSchemas.Input(
                ("spaceId", ReadToolSchemas.IdSchema()),
                ("taskId", ReadToolSchemas.IdSchema())),
            TaskStatusOutputSchema(),
            "spaceId", "taskId");
    }

    private static async ValueTask<CallToolResult> InvokeListDeploymentsAsync(
        ReadToolRegistration tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit)
    {
        if (!ReadToolSupport.OnlyArguments(arguments, "spaceId", "projectId", "environmentId", "pageSize", "cursor") ||
            !ReadToolSupport.TryRequiredInt(arguments, "spaceId", out var spaceId) ||
            !ReadToolSupport.TryRequiredInt(arguments, "projectId", out var projectId) ||
            !ReadToolSupport.TryRequiredInt(arguments, "environmentId", out var environmentId) ||
            !ReadToolSupport.TryPageSize(arguments, out var pageSize) ||
            !ReadToolSupport.TryCursor(arguments, out var cursor))
        {
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);
        }

        audit?.SetRelatedId(environmentId);
        var scope = ReadToolCursor.QueryScope(spaceId, projectId, environmentId);
        if (!ReadToolSupport.TryPosition(cursor, tool, scope, out var after))
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);

        var response = await mediator.RequestAsync<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>(
            new ListVisibleDeploymentsRequest
            {
                SpaceId = spaceId,
                ProjectId = projectId,
                EnvironmentId = environmentId,
                PageSize = pageSize,
                After = after
            }, cancellationToken).ConfigureAwait(false);

        if (ReadToolSupport.TryError(response, out var error)) return error;
        if (response.Data == null)
            return ReadToolSupport.Error(ReadToolErrors.NotFoundOrForbidden,
                ReadToolErrors.NotFoundOrForbiddenMessage);

        var items = response.Data.Items.Select(MapDeployment).ToArray();
        var latest = response.LatestSuccessfulDeployment == null
            ? null
            : MapDeployment(response.LatestSuccessfulDeployment);

        return ReadToolSupport.Success(
            $"Returned {items.Length} deployments.",
            new ListDeploymentsOutput(
                items,
                ReadToolSupport.NextCursor(response.Data.NextPosition, tool, scope),
                latest));
    }

    private static async ValueTask<CallToolResult> InvokeGetTaskStatusAsync(
        ReadToolRegistration tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit)
    {
        if (!ReadToolSupport.OnlyArguments(arguments, "spaceId", "taskId") ||
            !ReadToolSupport.TryRequiredInt(arguments, "spaceId", out var spaceId) ||
            !ReadToolSupport.TryRequiredInt(arguments, "taskId", out var taskId))
        {
            return ReadToolSupport.Error(ReadToolErrors.InvalidArgument, ReadToolErrors.InvalidArgumentMessage);
        }

        audit?.SetRelatedId(taskId);
        var response = await mediator.RequestAsync<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>(
            new GetVisibleDeploymentTaskRequest { SpaceId = spaceId, TaskId = taskId },
            cancellationToken).ConfigureAwait(false);

        if (ReadToolSupport.TryError(response, out var error)) return error;
        if (response.Data == null)
            return ReadToolSupport.Error(ReadToolErrors.NotFoundOrForbidden,
                ReadToolErrors.NotFoundOrForbiddenMessage);

        var task = response.Data;
        return ReadToolSupport.Success(
            "Task status retrieved.",
            new TaskStatusOutput(task.TaskId, task.DeploymentId, task.State, task.StartedAt, task.CompletedAt));
    }

    private static JsonElement DeploymentListOutput()
    {
        return ReadToolSchemas.Output(ReadToolSchemas.ObjectSchema(new JsonObject
        {
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = DeploymentItemSchema()
            },
            ["nextCursor"] = ReadToolSchemas.NullableString(),
            ["latestSuccessfulDeployment"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(DeploymentItemSchema(), ReadToolSchemas.NullSchema())
            }
        }, ["items", "nextCursor", "latestSuccessfulDeployment"]));
    }

    private static JsonElement TaskStatusOutputSchema()
    {
        return ReadToolSchemas.Output(ReadToolSchemas.ObjectSchema(new JsonObject
        {
            ["taskId"] = ReadToolSchemas.IdSchema(),
            ["deploymentId"] = ReadToolSchemas.IdSchema(),
            ["state"] = ReadToolSchemas.StateSchema(),
            ["startedAt"] = ReadToolSchemas.NullableDateTime(),
            ["completedAt"] = ReadToolSchemas.NullableDateTime()
        }, ["taskId", "deploymentId", "state", "startedAt", "completedAt"]));
    }

    private static JsonObject DeploymentItemSchema()
    {
        return ReadToolSchemas.ObjectSchema(new JsonObject
        {
            ["id"] = ReadToolSchemas.IdSchema(),
            ["releaseId"] = ReadToolSchemas.IdSchema(),
            ["currentReleaseVersion"] = ReadToolSchemas.NullableString(),
            ["taskId"] = new JsonObject { ["type"] = new JsonArray("integer", "null"), ["minimum"] = 1 },
            ["state"] = ReadToolSchemas.StateSchema(),
            ["createdAt"] = ReadToolSchemas.DateTimeSchema(),
            ["completedAt"] = ReadToolSchemas.NullableDateTime()
        }, ["id", "releaseId", "currentReleaseVersion", "taskId", "state", "createdAt", "completedAt"]);
    }

    private static DeploymentOutput MapDeployment(DeploymentReadItem item)
    {
        return new DeploymentOutput(
            item.Id,
            item.ReleaseId,
            item.CurrentReleaseVersion,
            item.TaskId,
            item.State,
            item.CreatedAt,
            item.CompletedAt);
    }

    private sealed record ListDeploymentsOutput(
        IReadOnlyList<DeploymentOutput> Items,
        string NextCursor,
        DeploymentOutput LatestSuccessfulDeployment);

    private sealed record DeploymentOutput(
        int Id,
        int ReleaseId,
        string CurrentReleaseVersion,
        int? TaskId,
        string State,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);

    private sealed record TaskStatusOutput(
        int TaskId,
        int DeploymentId,
        string State,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt);
}
