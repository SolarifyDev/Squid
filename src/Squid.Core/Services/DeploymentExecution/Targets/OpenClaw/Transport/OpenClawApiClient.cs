using System.Text.Json;
using Serilog;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

internal class OpenClawApiClient
{
    private readonly ISquidHttpClientFactory _httpClientFactory;

    internal OpenClawApiClient(ISquidHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    internal async Task<OpenClawToolResponse> InvokeToolAsync(string baseUrl, string gatewayToken, string tool, string action, string argsJson, string sessionKey, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/tools/invoke";
        var headers = BuildGatewayHeaders(gatewayToken);

        var args = ParseArgsJson(argsJson);
        var body = new { tool, action = action ?? "json", args, sessionKey };

        try
        {
            var response = await _httpClientFactory.PostAsJsonAsync<JsonElement>(url, body, ct, timeout: timeout, headers: headers, shouldLogError: false).ConfigureAwait(false);
            return ParseToolResponse(response);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw] InvokeTool failed for {Tool}", tool);
            return new OpenClawToolResponse(false, null, $"HTTP error: {ex.Message}");
        }
    }

    internal async Task<bool> RunAgentAsync(string baseUrl, string hooksToken, object requestBody, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/hooks/agent";
        var headers = BuildHooksHeaders(hooksToken);

        try
        {
            await _httpClientFactory.PostAsJsonAsync<string>(url, requestBody, ct, timeout: timeout, headers: headers, shouldLogError: false).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw] RunAgent failed");
            return false;
        }
    }

    internal async Task<bool> WakeAsync(string baseUrl, string hooksToken, string text, string mode, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/hooks/wake";
        var headers = BuildHooksHeaders(hooksToken);
        var body = new { text, mode = mode ?? "now" };

        try
        {
            await _httpClientFactory.PostAsJsonAsync<string>(url, body, ct, timeout: timeout, headers: headers, shouldLogError: false).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw] Wake failed");
            return false;
        }
    }

    private static Dictionary<string, string> BuildGatewayHeaders(string token) => new()
    {
        ["Authorization"] = $"Bearer {token}"
    };

    private static Dictionary<string, string> BuildHooksHeaders(string token) => new()
    {
        ["Authorization"] = $"Bearer {token}"
    };

    private static object ParseArgsJson(string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return new { };

        try { return JsonSerializer.Deserialize<JsonElement>(argsJson); }
        catch { return new { }; }
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
