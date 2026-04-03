using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Http;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawExecutionStrategy : IExecutionStrategy
{
    private readonly OpenClawApiClient _client;

    public OpenClawExecutionStrategy(ISquidHttpClientFactory httpClientFactory)
    {
        _client = new OpenClawApiClient(httpClientFactory);
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        var props = request.ActionProperties ?? new Dictionary<string, string>();

        return request.ActionType switch
        {
            SpecialVariables.ActionTypes.OpenClawInvokeTool => await ExecuteInvokeToolAsync(request, props, ct).ConfigureAwait(false),
            SpecialVariables.ActionTypes.OpenClawRunAgent => await ExecuteRunAgentAsync(request, props, ct).ConfigureAwait(false),
            SpecialVariables.ActionTypes.OpenClawWake => await ExecuteWakeAsync(request, props, ct).ConfigureAwait(false),
            SpecialVariables.ActionTypes.OpenClawWaitSession => await ExecuteWaitSessionAsync(request, props, ct).ConfigureAwait(false),
            SpecialVariables.ActionTypes.OpenClawAssert => ExecuteAssert(request, props),
            SpecialVariables.ActionTypes.OpenClawFetchResult => ExecuteFetchResult(request, props),
            _ => Fail($"Unknown OpenClaw action type: {request.ActionType}")
        };
    }

    private async Task<ScriptExecutionResult> ExecuteInvokeToolAsync(ScriptExecutionRequest request, Dictionary<string, string> props, CancellationToken ct)
    {
        var baseUrl = ResolveVariable(request, SpecialVariables.OpenClaw.BaseUrl);
        var token = ResolveVariable(request, SpecialVariables.OpenClaw.GatewayToken);
        var tool = GetProp(props, SpecialVariables.OpenClaw.PropTool);
        var action = GetProp(props, SpecialVariables.OpenClaw.PropToolAction);
        var argsJson = GetProp(props, SpecialVariables.OpenClaw.PropArgsJson);
        var sessionKey = GetProp(props, SpecialVariables.OpenClaw.PropSessionKey) ?? ResolveVariable(request, SpecialVariables.OpenClaw.SessionKey);
        var timeout = ResolveTimeout(props, request);

        Log.Information("[OpenClaw] InvokeTool: {Tool} action={Action} session={SessionKey}", tool, action, sessionKey);

        var response = await _client.InvokeToolAsync(baseUrl, token, tool, action, argsJson, sessionKey, timeout, ct).ConfigureAwait(false);

        var lines = new List<string>
        {
            $"OpenClaw InvokeTool: {tool}",
            $"Result ok: {response.Ok}"
        };

        EmitSetVariable(lines, SpecialVariables.OpenClaw.Ok, response.Ok.ToString());
        EmitSetVariable(lines, SpecialVariables.OpenClaw.ResultJson, response.ResultJson ?? string.Empty);

        if (!response.Ok)
            lines.Add($"Error: {response.Error}");

        return new ScriptExecutionResult
        {
            Success = response.Ok,
            ExitCode = response.Ok ? 0 : 1,
            LogLines = lines
        };
    }

    private async Task<ScriptExecutionResult> ExecuteRunAgentAsync(ScriptExecutionRequest request, Dictionary<string, string> props, CancellationToken ct)
    {
        var baseUrl = ResolveVariable(request, SpecialVariables.OpenClaw.BaseUrl);
        var token = ResolveVariable(request, SpecialVariables.OpenClaw.HooksToken);
        var timeout = ResolveTimeout(props, request);

        var body = new Dictionary<string, object>();
        AddIfPresent(body, "message", GetProp(props, SpecialVariables.OpenClaw.PropMessage));
        AddIfPresent(body, "agentId", GetProp(props, SpecialVariables.OpenClaw.PropAgentId));
        AddIfPresent(body, "sessionKey", GetProp(props, SpecialVariables.OpenClaw.PropSessionKey) ?? ResolveVariable(request, SpecialVariables.OpenClaw.SessionKey));
        AddIfPresent(body, "wakeMode", GetProp(props, SpecialVariables.OpenClaw.PropWakeMode));
        AddBoolIfPresent(body, "deliver", GetProp(props, SpecialVariables.OpenClaw.PropDeliver));
        AddIfPresent(body, "channel", GetProp(props, SpecialVariables.OpenClaw.PropChannel));
        AddIfPresent(body, "to", GetProp(props, SpecialVariables.OpenClaw.PropTo));
        AddIfPresent(body, "name", GetProp(props, SpecialVariables.OpenClaw.PropAgentName));
        AddIfPresent(body, "model", GetProp(props, SpecialVariables.OpenClaw.PropModel));
        AddIfPresent(body, "thinking", GetProp(props, SpecialVariables.OpenClaw.PropThinking));
        AddIntIfPresent(body, "timeoutSeconds", GetProp(props, SpecialVariables.OpenClaw.PropAgentTimeoutSeconds));

        Log.Information("[OpenClaw] RunAgent: message={Message} agentId={AgentId} name={Name}", GetProp(props, SpecialVariables.OpenClaw.PropMessage), GetProp(props, SpecialVariables.OpenClaw.PropAgentId), GetProp(props, SpecialVariables.OpenClaw.PropAgentName));

        var accepted = await _client.RunAgentAsync(baseUrl, token, body, timeout, ct).ConfigureAwait(false);

        var lines = new List<string> { $"OpenClaw RunAgent: accepted={accepted}" };
        EmitSetVariable(lines, SpecialVariables.OpenClaw.Accepted, accepted.ToString());

        return new ScriptExecutionResult
        {
            Success = accepted,
            ExitCode = accepted ? 0 : 1,
            LogLines = lines
        };
    }

    private async Task<ScriptExecutionResult> ExecuteWakeAsync(ScriptExecutionRequest request, Dictionary<string, string> props, CancellationToken ct)
    {
        var baseUrl = ResolveVariable(request, SpecialVariables.OpenClaw.BaseUrl);
        var token = ResolveVariable(request, SpecialVariables.OpenClaw.HooksToken);
        var text = GetProp(props, SpecialVariables.OpenClaw.PropWakeText) ?? string.Empty;
        var mode = GetProp(props, SpecialVariables.OpenClaw.PropWakeMode);
        var timeout = ResolveTimeout(props, request);

        Log.Information("[OpenClaw] Wake: text={Text} mode={Mode}", text, mode);

        var accepted = await _client.WakeAsync(baseUrl, token, text, mode, timeout, ct).ConfigureAwait(false);

        var lines = new List<string> { $"OpenClaw Wake: accepted={accepted}" };
        EmitSetVariable(lines, SpecialVariables.OpenClaw.Accepted, accepted.ToString());

        return new ScriptExecutionResult
        {
            Success = accepted,
            ExitCode = accepted ? 0 : 1,
            LogLines = lines
        };
    }

    private async Task<ScriptExecutionResult> ExecuteWaitSessionAsync(ScriptExecutionRequest request, Dictionary<string, string> props, CancellationToken ct)
    {
        var baseUrl = ResolveVariable(request, SpecialVariables.OpenClaw.BaseUrl);
        var token = ResolveVariable(request, SpecialVariables.OpenClaw.GatewayToken);
        var sessionKey = GetProp(props, SpecialVariables.OpenClaw.PropSessionKey) ?? ResolveVariable(request, SpecialVariables.OpenClaw.SessionKey);
        var successPattern = GetProp(props, SpecialVariables.OpenClaw.PropSuccessPattern);
        var failPattern = GetProp(props, SpecialVariables.OpenClaw.PropFailPattern);
        var maxWaitSeconds = int.TryParse(GetProp(props, SpecialVariables.OpenClaw.PropMaxWaitSeconds), out var mw) ? mw : 120;
        var pollSeconds = int.TryParse(GetProp(props, SpecialVariables.OpenClaw.PropPollSeconds), out var ps) ? ps : 5;

        Log.Information("[OpenClaw] WaitSession via HTTP polling: sessionKey={SessionKey} maxWait={MaxWait}s poll={Poll}s", sessionKey, maxWaitSeconds, pollSeconds);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSeconds);
        var lines = new List<string> { $"Polling session '{sessionKey}' every {pollSeconds}s (max {maxWaitSeconds}s)" };
        var lastStatus = "Pending";
        string lastSummary = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var response = await _client.InvokeToolAsync(baseUrl, token, "sessions_list", "json", null, sessionKey, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

            if (response.Ok && response.ResultJson != null)
            {
                lastSummary = response.ResultJson;

                if (!string.IsNullOrEmpty(successPattern) && Regex.IsMatch(response.ResultJson, successPattern, RegexOptions.IgnoreCase))
                {
                    lastStatus = "Success";
                    lines.Add($"Session matched success pattern: {successPattern}");
                    break;
                }

                if (!string.IsNullOrEmpty(failPattern) && Regex.IsMatch(response.ResultJson, failPattern, RegexOptions.IgnoreCase))
                {
                    lastStatus = "Failed";
                    lines.Add($"Session matched failure pattern: {failPattern}");
                    break;
                }
            }

            lines.Add($"Poll at {DateTimeOffset.UtcNow:HH:mm:ss}: status={lastStatus}");

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct).ConfigureAwait(false);
        }

        if (lastStatus == "Pending")
        {
            lastStatus = "Timeout";
            lines.Add($"Session wait timed out after {maxWaitSeconds}s");
        }

        EmitSetVariable(lines, SpecialVariables.OpenClaw.Status, lastStatus);
        EmitSetVariable(lines, SpecialVariables.OpenClaw.Summary, lastSummary ?? string.Empty);

        var success = lastStatus == "Success";

        return new ScriptExecutionResult
        {
            Success = success,
            ExitCode = success ? 0 : 1,
            LogLines = lines
        };
    }

    private static ScriptExecutionResult ExecuteAssert(ScriptExecutionRequest request, Dictionary<string, string> props)
    {
        var jsonPath = GetProp(props, SpecialVariables.OpenClaw.PropJsonPath);
        var op = GetProp(props, SpecialVariables.OpenClaw.PropOperator) ?? "equals";
        var expected = GetProp(props, SpecialVariables.OpenClaw.PropExpected) ?? string.Empty;
        var sourceVar = GetProp(props, SpecialVariables.OpenClaw.PropSourceVariable) ?? SpecialVariables.OpenClaw.ResultJson;

        var sourceJson = ResolveVariable(request, sourceVar);
        var actual = ExtractJsonPath(sourceJson, jsonPath);

        var passed = EvaluateAssertion(actual, op, expected);
        var lines = new List<string>
        {
            $"Assert: jsonPath='{jsonPath}' operator='{op}' expected='{expected}'",
            $"Actual: '{actual}'",
            passed ? "Assertion PASSED" : "Assertion FAILED"
        };

        return new ScriptExecutionResult
        {
            Success = passed,
            ExitCode = passed ? 0 : 1,
            LogLines = lines
        };
    }

    private static ScriptExecutionResult ExecuteFetchResult(ScriptExecutionRequest request, Dictionary<string, string> props)
    {
        var sourceVar = GetProp(props, SpecialVariables.OpenClaw.PropSourceVariable) ?? SpecialVariables.OpenClaw.ResultJson;
        var mappingsJson = GetProp(props, SpecialVariables.OpenClaw.PropFieldMappings);

        var sourceJson = ResolveVariable(request, sourceVar);
        var lines = new List<string> { $"FetchResult from variable '{sourceVar}'" };

        if (string.IsNullOrEmpty(mappingsJson))
            return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = lines };

        try
        {
            var mappings = JsonSerializer.Deserialize<List<FieldMapping>>(mappingsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (mappings == null)
                return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = lines };

            foreach (var mapping in mappings)
            {
                var value = ExtractJsonPath(sourceJson, mapping.JsonPath);
                EmitSetVariable(lines, mapping.OutputName, value ?? string.Empty);
                lines.Add($"  {mapping.OutputName} = {value ?? "(null)"}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Failed to parse field mappings: {ex.Message}");
            return new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = lines };
        }

        return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = lines };
    }

    private static string ResolveVariable(ScriptExecutionRequest request, string name)
    {
        return request.Variables?
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;
    }

    private static string GetProp(Dictionary<string, string> props, string key)
    {
        return props.TryGetValue(key, out var value) ? value : null;
    }

    private static TimeSpan ResolveTimeout(Dictionary<string, string> props, ScriptExecutionRequest request)
    {
        if (int.TryParse(GetProp(props, SpecialVariables.OpenClaw.PropTimeoutSeconds), out var seconds))
            return TimeSpan.FromSeconds(seconds);

        return request.Timeout ?? TimeSpan.FromSeconds(30);
    }

    private static void EmitSetVariable(List<string> lines, string name, string value)
    {
        var escapedValue = value?.Replace("'", "''") ?? string.Empty;
        lines.Add($"##squid[setVariable name='{name}' value='{escapedValue}' sensitive='False']");
    }

    internal static string ExtractJsonPath(string json, string path)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path)) return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;

            var segments = path.TrimStart('$', '.').Split('.');

            foreach (var segment in segments)
            {
                if (string.IsNullOrEmpty(segment)) continue;

                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var child))
                {
                    element = child;
                }
                else
                {
                    return null;
                }
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    internal static bool EvaluateAssertion(string actual, string op, string expected)
    {
        if (actual == null) return false;

        return op.ToLowerInvariant() switch
        {
            "equals" => string.Equals(actual, expected, StringComparison.Ordinal),
            "equalsignorecase" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(expected, StringComparison.Ordinal),
            "matches" => Regex.IsMatch(actual, expected, RegexOptions.IgnoreCase),
            "notequals" => !string.Equals(actual, expected, StringComparison.Ordinal),
            "greaterthan" => double.TryParse(actual, out var a) && double.TryParse(expected, out var b) && a > b,
            "lessthan" => double.TryParse(actual, out var la) && double.TryParse(expected, out var lb) && la < lb,
            _ => string.Equals(actual, expected, StringComparison.Ordinal)
        };
    }

    private static ScriptExecutionResult Fail(string message) => new()
    {
        Success = false,
        ExitCode = 1,
        LogLines = new List<string> { message }
    };

    private static void AddIfPresent(Dictionary<string, object> body, string key, string value)
    {
        if (!string.IsNullOrEmpty(value)) body[key] = value;
    }

    private static void AddBoolIfPresent(Dictionary<string, object> body, string key, string value)
    {
        if (bool.TryParse(value, out var b)) body[key] = b;
    }

    private static void AddIntIfPresent(Dictionary<string, object> body, string key, string value)
    {
        if (int.TryParse(value, out var n)) body[key] = n;
    }
}

internal record FieldMapping(string JsonPath, string OutputName);
