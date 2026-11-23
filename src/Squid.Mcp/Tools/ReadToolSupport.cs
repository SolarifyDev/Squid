using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Squid.Message.Requests.Mcp;
using Squid.Message.Response;

namespace Squid.Mcp.Tools;

internal static class ReadToolErrors
{
    internal const string InvalidArgument = "INVALID_ARGUMENT";
    internal const string NotFoundOrForbidden = "NOT_FOUND_OR_FORBIDDEN";
    internal const string TemporaryFailure = "TEMPORARY_FAILURE";

    internal const string InvalidArgumentMessage = "The request arguments are invalid.";
    internal const string NotFoundOrForbiddenMessage = "The requested resource was not found or is inaccessible.";
    internal const string TemporaryFailureMessage = "The request could not be completed. Try again.";
}

internal static class ReadToolSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string NextCursor(
        ScopedReadPosition position,
        ReadToolRegistration tool,
        string scope)
    {
        return position == null ? null : ReadToolCursor.Encode(tool.Name, tool.Sort, scope, position);
    }

    internal static bool TryPosition(
        string cursor,
        ReadToolRegistration tool,
        string scope,
        out ScopedReadPosition position)
    {
        if (cursor == null)
        {
            position = null;
            return true;
        }

        return ReadToolCursor.TryDecode(cursor, tool.Name, tool.Sort, scope, out position);
    }

    internal static bool TryError(SquidResponse response, out CallToolResult result)
    {
        if (response.Code is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            result = Error(ReadToolErrors.NotFoundOrForbidden, ReadToolErrors.NotFoundOrForbiddenMessage);
            return true;
        }

        if ((int)response.Code >= 400)
        {
            result = Error(ReadToolErrors.TemporaryFailure, ReadToolErrors.TemporaryFailureMessage);
            return true;
        }

        result = null;
        return false;
    }

    internal static bool OnlyArguments(IReadOnlyDictionary<string, JsonElement> arguments, params string[] names)
    {
        if (arguments == null) return true;

        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        return arguments.Keys.All(allowed.Contains);
    }

    internal static bool TryRequiredInt(IReadOnlyDictionary<string, JsonElement> arguments, string name,
        out int value)
    {
        value = 0;
        return arguments != null &&
               arguments.TryGetValue(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value) &&
               value > 0;
    }

    internal static bool TryOptionalInt(IReadOnlyDictionary<string, JsonElement> arguments, string name,
        out int value)
    {
        value = 0;
        if (arguments == null || !arguments.TryGetValue(name, out var element))
            return true;

        if (element.ValueKind == JsonValueKind.Null) return false;

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    internal static bool TryPageSize(IReadOnlyDictionary<string, JsonElement> arguments, out int pageSize)
    {
        pageSize = 20;

        if (arguments == null || !arguments.TryGetValue("pageSize", out var element))
            return true;

        if (element.ValueKind == JsonValueKind.Null) return false;

        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out pageSize) &&
               pageSize is >= 1 and <= 100;
    }

    internal static bool TryOptionalString(IReadOnlyDictionary<string, JsonElement> arguments, string name,
        out string value)
    {
        value = null;
        if (arguments == null || !arguments.TryGetValue(name, out var element))
            return true;

        if (element.ValueKind == JsonValueKind.Null) return false;

        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return true;
    }

    internal static bool TryCursor(IReadOnlyDictionary<string, JsonElement> arguments, out string cursor)
    {
        if (!TryOptionalString(arguments, "cursor", out cursor)) return false;
        if (cursor == null) return true;
        return !string.IsNullOrWhiteSpace(cursor);
    }

    internal static string NormalizeKeyword(string keyword)
    {
        var value = keyword?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static CallToolResult Success(string text, object structured)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = JsonSerializer.SerializeToElement(structured, JsonOptions),
            IsError = false
        };
    }

    internal static CallToolResult Error(string code, string message)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = JsonSerializer.SerializeToElement(new { code, message }, JsonOptions),
            IsError = true
        };
    }
}

internal sealed class ToolInvocationAudit
{
    public int? RelatedId { get; private set; }

    public void SetRelatedId(int id) => RelatedId = id;
}
