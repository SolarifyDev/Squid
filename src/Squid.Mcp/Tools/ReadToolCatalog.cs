using System.Text.Json;
using Mediator.Net;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Squid.Mcp;

namespace Squid.Mcp.Tools;

internal static class ReadToolCatalog
{
    internal static IReadOnlyList<ReadToolRegistration> Tools { get; } =
    [
        DiscoveryTools.ListSpacesTool,
        DiscoveryTools.ListProjectsTool,
        DiscoveryTools.ListEnvironmentsTool,
        ReleaseTools.ListReleasesTool,
        DeploymentTools.ListDeploymentsTool,
        DeploymentTools.GetTaskStatusTool
    ];

    internal static IReadOnlyList<Tool> ToolDefinitions { get; } =
        Tools.Select(tool => tool.Definition).ToArray();

    private static readonly IReadOnlyDictionary<string, ReadToolRegistration> ToolsByName =
        Tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    internal static bool Contains(string toolName)
    {
        return TryGet(toolName, out _);
    }

    internal static bool TryGet(string toolName, out ReadToolRegistration tool)
    {
        if (toolName != null)
            return ToolsByName.TryGetValue(toolName, out tool);

        tool = null;
        return false;
    }

    internal static async ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var audit = new ToolInvocationAudit();
        CallToolResult result;

        try
        {
            var mediator = context.Services.GetRequiredService<IMediator>();
            var arguments = context.Params?.Arguments == null
                ? null
                : new Dictionary<string, JsonElement>(context.Params.Arguments, StringComparer.Ordinal);

            result = await InvokeAsync(context.Params?.Name, arguments, mediator, cancellationToken, audit)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = ReadToolSupport.Error(
                ReadToolErrors.TemporaryFailure,
                ReadToolErrors.TemporaryFailureMessage);
        }

        SquidMcpAudit.Record(context, context.Params?.Name, result, audit.RelatedId);
        return result;
    }

    internal static async ValueTask<CallToolResult> InvokeAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IMediator mediator,
        CancellationToken cancellationToken,
        ToolInvocationAudit audit = null)
    {
        try
        {
            if (!TryGet(toolName, out var tool))
                return ReadToolSupport.Error(
                    ReadToolErrors.InvalidArgument,
                    ReadToolErrors.InvalidArgumentMessage);

            return await tool.InvokeAsync(arguments, mediator, cancellationToken, audit)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ReadToolSupport.Error(
                ReadToolErrors.TemporaryFailure,
                ReadToolErrors.TemporaryFailureMessage);
        }
    }
}
