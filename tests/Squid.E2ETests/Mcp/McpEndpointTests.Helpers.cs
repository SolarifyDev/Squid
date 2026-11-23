using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Squid.E2ETests.Mcp;

public partial class McpEndpointTests
{
    private static async Task<(HttpStatusCode StatusCode, string Body)> SendToolsListAsync(
        HttpClient client,
        string? apiKey = null,
        string? bearerToken = null,
        string? origin = null,
        string? mcpName = null,
        string? payloadMarker = null,
        string protocolVersion = McpEndpointFixture.ProtocolVersion,
        bool includePerRequestMetadata = true,
        bool includeMcpMethod = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        if (includeMcpMethod)
            request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list");

        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);

        if (!string.IsNullOrEmpty(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        if (!string.IsNullOrEmpty(origin))
            request.Headers.TryAddWithoutValidation("Origin", origin);

        if (!string.IsNullOrEmpty(mcpName))
            request.Headers.TryAddWithoutValidation("Mcp-Name", mcpName);

        request.Content = new StringContent(
            CreateToolsListBody(payloadMarker, includePerRequestMetadata),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (response.StatusCode, body);
    }

    private static string CreateToolsListBody(
        string? payloadMarker,
        bool includePerRequestMetadata)
    {
        if (includePerRequestMetadata)
        {
            if (string.IsNullOrEmpty(payloadMarker)) return McpEndpointFixture.ToolsListBody;

            return McpEndpointFixture.ToolsListBody
                .Replace("\"id\": 1", $"\"id\": \"{payloadMarker}\"")
                .Replace(
                    "\"io.modelcontextprotocol/clientCapabilities\": {}",
                    $"\"io.modelcontextprotocol/clientCapabilities\": {{}}, \"test/payload\": \"{payloadMarker}\"");
        }

        var parameters = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(payloadMarker))
            parameters["test/payload"] = payloadMarker;

        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = string.IsNullOrEmpty(payloadMarker) ? "1" : payloadMarker,
            method = "tools/list",
            @params = parameters
        });
    }

    private static Task<(HttpStatusCode StatusCode, string Body)> SendToolCallAsync(
        HttpClient client,
        string apiKey,
        string toolName,
        object arguments)
    {
        return SendToolCallAsync(
            client,
            apiKey,
            toolName,
            arguments,
            McpEndpointFixture.ProtocolVersion,
            includePerRequestMetadata: true);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendToolCallAsync(
        HttpClient client,
        string apiKey,
        string toolName,
        object arguments,
        string protocolVersion,
        bool includePerRequestMetadata,
        bool includeMcpMethod = true)
    {
        var parameters = new Dictionary<string, object>
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        };
        if (includePerRequestMetadata)
        {
            parameters["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = protocolVersion,
                ["io.modelcontextprotocol/clientCapabilities"] = new Dictionary<string, object>()
            };
        }

        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = parameters
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        if (includeMcpMethod)
            request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/call");
        request.Headers.TryAddWithoutValidation("Mcp-Name", toolName);
        request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendRawAsync(
        HttpClient client,
        string apiKey,
        string body,
        string mcpMethod = "tools/call")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpEndpointFixture.ProtocolVersion);
        request.Headers.TryAddWithoutValidation("Mcp-Method", mcpMethod);
        request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static string[] GetAllowedHeaders(HttpResponseMessage response)
    {
        return response.Headers.GetValues("Access-Control-Allow-Headers")
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .ToArray();
    }

    private static JsonElement GetTools(string body)
    {
        return GetMcpResult(body).GetProperty("tools").Clone();
    }

    private static JsonElement GetMcpResult(string body)
    {
        var json = body;
        var dataLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        if (dataLine != null) json = dataLine["data: ".Length..];

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertToolError(JsonElement result, string code)
    {
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.EnumerateObject().Select(property => property.Name).ShouldBe(["code", "message"]);
        structured.GetProperty("code").GetString().ShouldBe(code);
        structured.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
