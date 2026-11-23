using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Squid.Mcp.Tools;

namespace Squid.Mcp;

public static class SquidMcpAudit
{
    public const string ToolNameItemKey = "Squid.Mcp.Audit.ToolName";
    public const string ResultCategoryItemKey = "Squid.Mcp.Audit.ResultCategory";
    public const string RelatedIdItemKey = "Squid.Mcp.Audit.RelatedId";

    internal static void Record(
        RequestContext<CallToolRequestParams> context,
        string requestedToolName,
        CallToolResult result,
        int? relatedId)
    {
        Record(context.Services, requestedToolName, result, relatedId);
    }

    internal static void Record(
        IServiceProvider services,
        string requestedToolName,
        CallToolResult result,
        int? relatedId)
    {
        var accessor = services?.GetService<IHttpContextAccessor>();
        var httpContext = accessor?.HttpContext;
        if (httpContext == null) return;

        if (ReadToolCatalog.Contains(requestedToolName))
        {
            httpContext.Items[ToolNameItemKey] = requestedToolName;
        }

        httpContext.Items[ResultCategoryItemKey] = GetResultCategory(result);

        if (relatedId != null)
            httpContext.Items[RelatedIdItemKey] = relatedId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static void RecordProtocolError(IServiceProvider services)
    {
        var accessor = services?.GetService<IHttpContextAccessor>();
        var httpContext = accessor?.HttpContext;
        if (httpContext != null)
            httpContext.Items[ResultCategoryItemKey] = "protocol_error";
    }

    private static string GetResultCategory(CallToolResult result)
    {
        if (result.IsError != true) return "success";

        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured ||
            !structured.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.String)
        {
            return "error";
        }

        return code.GetString() switch
        {
            ReadToolErrors.InvalidArgument => "invalid_argument",
            ReadToolErrors.NotFoundOrForbidden => "not_found_or_forbidden",
            ReadToolErrors.TemporaryFailure => "temporary_failure",
            _ => "error"
        };
    }
}
