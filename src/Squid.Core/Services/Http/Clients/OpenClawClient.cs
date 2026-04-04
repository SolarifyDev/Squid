using System.Linq;
using System.Text.Json;
using Serilog;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Http.Clients;

internal class OpenClawClient
{
    private readonly ISquidHttpClientFactory _httpClientFactory;

    internal OpenClawClient(ISquidHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    internal async Task<OpenClawToolResponse> InvokeToolAsync(string baseUrl, string gatewayToken, string tool, string action, string argsJson, string sessionKey, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/tools/invoke";
        var headers = BuildBearerHeaders(gatewayToken);

        var args = ParseArgsJson(argsJson);
        var body = new { tool, action = action ?? "json", args, sessionKey };

        try
        {
            var response = await _httpClientFactory.PostAsJsonAsync<JsonElement>(url, body, ct, timeout: timeout, headers: headers, shouldLogError: false, isNeedToReadErrorContent: true).ConfigureAwait(false);
            return ParseToolResponse(response);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw] InvokeTool failed for {Tool}", tool);
            return new OpenClawToolResponse(false, null, $"HTTP error: {ex.Message}");
        }
    }

    internal async Task<OpenClawHookResponse> RunAgentAsync(string baseUrl, string hooksToken, object requestBody, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/hooks/agent";

        return await PostHookAsync(url, hooksToken, requestBody, timeout, ct).ConfigureAwait(false);
    }

    internal async Task<OpenClawHookResponse> WakeAsync(string baseUrl, string hooksToken, string text, string mode, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/hooks/wake";
        var body = new { text, mode = mode ?? "now" };

        return await PostHookAsync(url, hooksToken, body, timeout, ct).ConfigureAwait(false);
    }

    private async Task<OpenClawHookResponse> PostHookAsync(string url, string token, object body, TimeSpan timeout, CancellationToken ct)
    {
        var headers = BuildBearerHeaders(token);

        try
        {
            var httpResponse = await _httpClientFactory.PostAsJsonAsync(url, body, ct, timeout: timeout, headers: headers, shouldLogError: true).ConfigureAwait(false);

            if (httpResponse == null)
                return new OpenClawHookResponse(false, "No response — request may have failed (check logs)");

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new OpenClawHookResponse(false, $"HTTP {(int)httpResponse.StatusCode}: {errorBody}");
            }

            return new OpenClawHookResponse(true, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw] Hook POST failed for {Url}", url);
            return new OpenClawHookResponse(false, $"HTTP error: {ex.Message}");
        }
    }

    internal async Task<OpenClawChatResponse> ChatCompletionAsync(OpenClawChatRequest request, CancellationToken ct)
    {
        var url = $"{request.BaseUrl.TrimEnd('/')}/v1/chat/completions";
        var headers = BuildChatHeaders(request);
        var body = BuildChatBody(request);
        var chatTimeout = request.Timeout < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(120) : request.Timeout;

        Log.Information("[OpenClaw] ChatCompletion: {Url} model={Model} messages={Count}", url, request.Model, request.Messages.Count);

        try
        {
            var httpResponse = await _httpClientFactory.PostAsJsonAsync(url, body, ct, timeout: chatTimeout, headers: headers, shouldLogError: true).ConfigureAwait(false);

            if (httpResponse == null)
                return new OpenClawChatResponse(false, null, null, null, "No response — request may have failed (check logs)");

            var responseBody = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
                return new OpenClawChatResponse(false, null, null, null, $"HTTP {(int)httpResponse.StatusCode}: {responseBody}");

            if (string.IsNullOrEmpty(responseBody))
                return new OpenClawChatResponse(false, null, null, null, "Empty response body");

            var json = JsonDocument.Parse(responseBody).RootElement;
            return ParseChatResponse(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw] ChatCompletion failed");
            return new OpenClawChatResponse(false, null, null, null, $"HTTP error: {ex.Message}");
        }
    }

    internal async IAsyncEnumerable<OpenClawChatStreamChunk> ChatCompletionStreamAsync(OpenClawChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"{request.BaseUrl.TrimEnd('/')}/v1/chat/completions";
        var headers = BuildChatHeaders(request);
        var body = BuildChatBody(request, stream: true);
        var chatTimeout = request.Timeout < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(120) : request.Timeout;

        var client = _httpClientFactory.CreateClient(timeout: chatTimeout, headers: headers);
        var jsonBody = JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var httpResponse = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)httpResponse.StatusCode}: {errorBody}");
        }

        using var stream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];

            if (data == "[DONE]") yield break;

            OpenClawChatStreamChunk chunk;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                string delta = null;
                string model = null;
                string finishReason = null;

                if (root.TryGetProperty("model", out var modelProp))
                    model = modelProp.GetString();

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];

                    if (choice.TryGetProperty("delta", out var deltaProp) && deltaProp.TryGetProperty("content", out var contentProp))
                        delta = contentProp.GetString();

                    if (choice.TryGetProperty("finish_reason", out var frProp) && frProp.ValueKind == JsonValueKind.String)
                        finishReason = frProp.GetString();
                }

                chunk = new OpenClawChatStreamChunk(delta, model, finishReason);
            }
            catch
            {
                continue;
            }

            if (chunk.Delta != null || chunk.FinishReason != null)
                yield return chunk;
        }
    }

    internal static Dictionary<string, string> BuildChatHeaders(OpenClawChatRequest request)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {request.GatewayToken}"
        };

        if (!string.IsNullOrEmpty(request.ModelOverride))
            headers["x-openclaw-model"] = request.ModelOverride;

        if (!string.IsNullOrEmpty(request.AgentId))
            headers["x-openclaw-agent-id"] = request.AgentId;

        if (!string.IsNullOrEmpty(request.SessionKey))
            headers["x-openclaw-session-key"] = request.SessionKey;

        if (!string.IsNullOrEmpty(request.Channel))
            headers["x-openclaw-message-channel"] = request.Channel;

        return headers;
    }

    internal static object BuildChatBody(OpenClawChatRequest request, bool stream = false)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = request.Model ?? "openclaw",
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = stream
        };

        if (!string.IsNullOrEmpty(request.User))
            body["user"] = request.User;

        if (request.Temperature.HasValue)
            body["temperature"] = request.Temperature.Value;

        if (request.MaxTokens.HasValue)
            body["max_tokens"] = request.MaxTokens.Value;

        if (!string.IsNullOrEmpty(request.ResponseFormat))
            body["response_format"] = new { type = request.ResponseFormat };

        return body;
    }

    internal static OpenClawChatResponse ParseChatResponse(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Undefined)
            return new OpenClawChatResponse(false, null, null, null, "Empty response — check logs for HTTP error (endpoint may not be enabled: gateway.http.endpoints.chatCompletions.enabled)");

        // Check for error response
        if (response.TryGetProperty("error", out var errorProp))
        {
            var errorMsg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : errorProp.GetRawText();
            return new OpenClawChatResponse(false, null, null, null, errorMsg);
        }

        // Parse OpenAI-compatible response
        var model = response.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;

        string content = null;
        string finishReason = null;

        if (response.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];

            if (firstChoice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var contentProp))
                content = contentProp.GetString();

            if (firstChoice.TryGetProperty("finish_reason", out var frProp))
                finishReason = frProp.GetString();
        }

        return new OpenClawChatResponse(content != null, content, model, finishReason, null);
    }

    private static Dictionary<string, string> BuildBearerHeaders(string token) => new()
    {
        ["Authorization"] = $"Bearer {token}"
    };

    internal static object ParseArgsJson(string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return new { };

        try { return JsonSerializer.Deserialize<JsonElement>(argsJson); }
        catch { /* Variable expansion may have broken JSON — attempt repair */ }

        try
        {
            var repaired = RepairJsonStrings(argsJson);
            return JsonSerializer.Deserialize<JsonElement>(repaired);
        }
        catch
        {
            Log.Warning("[OpenClaw] argsJson is not valid JSON even after repair: {ArgsJson}", argsJson[..Math.Min(200, argsJson.Length)]);
            return new { };
        }
    }

    /// <summary>
    /// Repairs JSON broken by raw variable expansion inside string values.
    /// Escapes unescaped quotes, newlines, tabs, and backslashes within JSON strings.
    /// </summary>
    internal static string RepairJsonStrings(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length + 64);
        var inString = false;
        var i = 0;

        while (i < json.Length)
        {
            var c = json[i];

            if (!inString)
            {
                sb.Append(c);

                if (c == '"')
                    inString = true;

                i++;
                continue;
            }

            // Inside a JSON string — check for characters that need escaping
            if (c == '\\' && i + 1 < json.Length)
            {
                // Already escaped sequence — pass through
                sb.Append(c);
                sb.Append(json[i + 1]);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                // Is this the real closing quote? Check if next non-whitespace is a JSON structural char
                if (IsClosingQuote(json, i + 1))
                {
                    sb.Append('"');
                    inString = false;
                    i++;
                    continue;
                }

                // Unescaped quote inside string value — escape it
                sb.Append("\\\"");
                i++;
                continue;
            }

            if (c == '\n') { sb.Append("\\n"); i++; continue; }
            if (c == '\r') { sb.Append("\\r"); i++; continue; }
            if (c == '\t') { sb.Append("\\t"); i++; continue; }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsClosingQuote(string json, int afterQuoteIndex)
    {
        for (var i = afterQuoteIndex; i < json.Length; i++)
        {
            var c = json[i];

            if (c == ' ' || c == '\r' || c == '\n' || c == '\t')
                continue;

            // JSON structural characters that can follow a closing quote
            return c is ':' or ',' or '}' or ']';
        }

        // End of string — this is the closing quote
        return true;
    }

    private static OpenClawToolResponse ParseToolResponse(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Undefined)
            return new OpenClawToolResponse(false, null, "Empty response");

        var ok = response.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        var resultJson = response.TryGetProperty("result", out var resultProp) ? resultProp.GetRawText() : null;

        string error = null;

        if (!ok && response.TryGetProperty("error", out var errorProp))
        {
            error = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : errorProp.GetRawText();
        }

        return new OpenClawToolResponse(ok, resultJson, error);
    }
}

internal record OpenClawToolResponse(bool Ok, string ResultJson, string Error);

internal record OpenClawChatRequest(string BaseUrl, string GatewayToken, List<OpenClawChatMessage> Messages, string Model, string ModelOverride, string SessionKey, string AgentId, string Channel, string User, TimeSpan Timeout, double? Temperature = null, int? MaxTokens = null, string ResponseFormat = null);

internal record OpenClawChatMessage(string Role, string Content);

internal record OpenClawChatResponse(bool Ok, string Content, string Model, string FinishReason, string Error);

internal record OpenClawChatStreamChunk(string Delta, string Model, string FinishReason);

internal record OpenClawHookResponse(bool Ok, string Error);
