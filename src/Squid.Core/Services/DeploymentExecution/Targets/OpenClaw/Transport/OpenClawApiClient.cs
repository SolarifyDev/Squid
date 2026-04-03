using System.Runtime.CompilerServices;
using System.Text.Json;
using Serilog;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

internal class OpenClawApiClient : IAsyncDisposable
{
    private readonly ISquidHttpClientFactory _httpClientFactory;
    private OpenClawWsChannel _wsChannel;

    internal OpenClawApiClient(ISquidHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    internal bool IsWsAvailable => _wsChannel != null;

    internal void InitializeWs(string wsUrl, string gatewayToken)
    {
        if (string.IsNullOrEmpty(wsUrl) || string.IsNullOrEmpty(gatewayToken)) return;

        _wsChannel ??= new OpenClawWsChannel(wsUrl, gatewayToken);
    }

    // --- HTTP methods (unchanged) ---

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

    internal async Task<bool> RunAgentAsync(string baseUrl, string hooksToken, object requestBody, TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/hooks/agent";
        var headers = BuildBearerHeaders(hooksToken);

        try
        {
            await _httpClientFactory.PostAsJsonAsync<string>(url, requestBody, ct, timeout: timeout, headers: headers, shouldLogError: false, isNeedToReadErrorContent: true).ConfigureAwait(false);
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
        var headers = BuildBearerHeaders(hooksToken);
        var body = new { text, mode = mode ?? "now" };

        try
        {
            await _httpClientFactory.PostAsJsonAsync<string>(url, body, ct, timeout: timeout, headers: headers, shouldLogError: false, isNeedToReadErrorContent: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OpenClaw] Wake failed");
            return false;
        }
    }

    // --- WebSocket methods ---

    internal async Task<OpenClawToolResponse> InvokeToolViaWsAsync(string tool, string action, string argsJson, string sessionKey, TimeSpan timeout, CancellationToken ct)
    {
        if (_wsChannel == null)
            return new OpenClawToolResponse(false, null, "WebSocket channel not initialized");

        try
        {
            var args = ParseArgsJson(argsJson);
            var parameters = new { tool, action = action ?? "json", args, sessionKey };
            var response = await _wsChannel.SendRequestRawAsync("tools.invoke", parameters, timeout, ct).ConfigureAwait(false);

            var resultJson = response.Payload?.GetRawText();

            return new OpenClawToolResponse(response.Ok, resultJson, response.Ok ? null : response.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[OpenClaw] WS InvokeTool failed for {Tool}", tool);
            return new OpenClawToolResponse(false, null, $"WS error: {ex.Message}");
        }
    }

    internal async IAsyncEnumerable<WsEvent> SubscribeSessionEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_wsChannel == null) yield break;

        await foreach (var evt in _wsChannel.SubscribeEventsAsync("session:", ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    internal async Task<JsonElement?> HealthSnapshotAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_wsChannel == null) return null;

        return await _wsChannel.SendRequestAsync<JsonElement>("health", new { }, timeout, ct).ConfigureAwait(false);
    }

    // --- Disposal ---

    public async ValueTask DisposeAsync()
    {
        if (_wsChannel != null)
            await _wsChannel.DisposeAsync().ConfigureAwait(false);
    }

    // --- Helpers ---

    private static Dictionary<string, string> BuildBearerHeaders(string token) => new()
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
