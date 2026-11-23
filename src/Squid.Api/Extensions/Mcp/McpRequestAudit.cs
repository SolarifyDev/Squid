using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Squid.Mcp;

namespace Squid.Api.Extensions;

internal static class McpRequestAudit
{
    internal static void LogRequest(Microsoft.Extensions.Logging.ILogger logger, HttpContext context, long started)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var protocolMethod = GetProtocolMethod(context.Request);
        var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var resultCategory = GetResultCategory(context);
        var toolName = context.Items[SquidMcpAudit.ToolNameItemKey] as string ?? "unknown";
        var relatedId = context.Items[SquidMcpAudit.RelatedIdItemKey] as string;

        logger.LogInformation(
            "MCP request completed. UserId={UserId} ProtocolMethod={ProtocolMethod} ToolName={ToolName} RelatedId={RelatedId} DurationMs={DurationMs} Result={ResultCategory} CorrelationId={CorrelationId}",
            userId,
            protocolMethod,
            toolName,
            relatedId,
            durationMs,
            resultCategory,
            context.TraceIdentifier);
    }

    private static string GetProtocolMethod(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Mcp-Method", out var methods) || methods.Count != 1)
            return "unknown";

        var method = methods[0];

        return !string.IsNullOrWhiteSpace(method) && McpEndpointExtensions.KnownProtocolMethods.Contains(method)
            ? method
            : "unknown";
    }

    private static string GetResultCategory(HttpContext context)
    {
        if (context.Items[SquidMcpAudit.ResultCategoryItemKey] is string toolResultCategory)
            return toolResultCategory;

        return context.Response.StatusCode switch
        {
            < 400 => "success",
            StatusCodes.Status401Unauthorized => "unauthorized",
            StatusCodes.Status403Forbidden => "forbidden",
            StatusCodes.Status400BadRequest when HttpMethods.IsPost(context.Request.Method) &&
                context.GetEndpoint()?.Metadata.GetMetadata<McpSdkEndpointMetadata>() != null => "protocol_error",
            < 500 => "client_error",
            _ => "server_error"
        };
    }
}
