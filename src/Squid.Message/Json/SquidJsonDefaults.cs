using System.Text.Json;

namespace Squid.Message.Json;

public static class SquidJsonDefaults
{
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
