using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Message.Requests.Mcp;

namespace Squid.Mcp.Tools;

internal static class ReadToolCursor
{
    private const int CurrentVersion = 1;
    private const int MaxEncodedLength = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string Encode(string toolName, ReadToolSort sort, string queryScope,
        ScopedReadPosition position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(queryScope);
        ArgumentNullException.ThrowIfNull(position);
        if (!IsValidSortPosition(sort, position.Id, position.CreatedAt))
            throw new ArgumentException("The cursor position does not match the tool sort order.", nameof(position));

        var payload = new CursorPayload(CurrentVersion, toolName, queryScope, position.Id, position.CreatedAt);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);

        return Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool TryDecode(string cursor, string toolName, ReadToolSort sort, string queryScope,
        out ScopedReadPosition position)
    {
        position = null;

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaxEncodedLength)
            return false;

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');

            var payload = JsonSerializer.Deserialize<CursorPayload>(
                Convert.FromBase64String(padded), SerializerOptions);

            if (payload == null ||
                payload.Version != CurrentVersion ||
                !string.Equals(payload.Tool, toolName, StringComparison.Ordinal) ||
                !string.Equals(payload.Query, queryScope, StringComparison.Ordinal) ||
                !IsValidSortPosition(sort, payload.Id, payload.CreatedAt))
            {
                return false;
            }

            position = new ScopedReadPosition(payload.Id, payload.CreatedAt);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    internal static string QueryScope(int? spaceId = null, int? projectId = null,
        int? environmentId = null, string keyword = null)
    {
        var parts = new List<string>();

        if (spaceId != null) parts.Add($"spaceId={spaceId.Value.ToString(CultureInfo.InvariantCulture)}");
        if (projectId != null) parts.Add($"projectId={projectId.Value.ToString(CultureInfo.InvariantCulture)}");
        if (environmentId != null)
            parts.Add($"environmentId={environmentId.Value.ToString(CultureInfo.InvariantCulture)}");

        var normalizedKeyword = NormalizeKeyword(keyword);
        if (normalizedKeyword != null)
            parts.Add($"keywordSha256={HashKeyword(normalizedKeyword)}");

        return string.Join('&', parts);
    }

    private static bool IsValidSortPosition(ReadToolSort sort, int id, DateTimeOffset? createdAt)
    {
        if (id <= 0) return false;

        return sort switch
        {
            ReadToolSort.Id => createdAt == null,
            ReadToolSort.CreatedAt =>
                createdAt is { Offset: var offset } && offset == TimeSpan.Zero,
            _ => false
        };
    }

    private static string NormalizeKeyword(string keyword)
    {
        var value = keyword?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.ToLowerInvariant();
    }

    private static string HashKeyword(string normalizedKeyword)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKeyword));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private sealed record CursorPayload(
        int Version,
        string Tool,
        string Query,
        int Id,
        DateTimeOffset? CreatedAt);
}
