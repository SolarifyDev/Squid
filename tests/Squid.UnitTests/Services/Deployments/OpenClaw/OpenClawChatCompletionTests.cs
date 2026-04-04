using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawChatCompletionTests
{
    // ========================================================================
    // BuildChatHeaders
    // ========================================================================

    [Fact]
    public void BuildChatHeaders_MinimalRequest_OnlyBearerAuth()
    {
        var request = MakeChatRequest();

        var headers = OpenClawApiClient.BuildChatHeaders(request);

        headers.ShouldContainKeyAndValue("Authorization", "Bearer test-token");
        headers.Count.ShouldBe(1);
    }

    [Fact]
    public void BuildChatHeaders_WithModelOverride_AddsXOpenClawModel()
    {
        var request = MakeChatRequest(modelOverride: "anthropic/claude-sonnet-4-20250514");

        var headers = OpenClawApiClient.BuildChatHeaders(request);

        headers.ShouldContainKeyAndValue("x-openclaw-model", "anthropic/claude-sonnet-4-20250514");
    }

    [Fact]
    public void BuildChatHeaders_WithAllOptional_AddsFourCustomHeaders()
    {
        var request = MakeChatRequest(modelOverride: "openai/gpt-5", sessionKey: "sess-1", agentId: "research", channel: "slack");

        var headers = OpenClawApiClient.BuildChatHeaders(request);

        headers.ShouldContainKeyAndValue("x-openclaw-model", "openai/gpt-5");
        headers.ShouldContainKeyAndValue("x-openclaw-agent-id", "research");
        headers.ShouldContainKeyAndValue("x-openclaw-session-key", "sess-1");
        headers.ShouldContainKeyAndValue("x-openclaw-message-channel", "slack");
        headers.Count.ShouldBe(5); // Bearer + 4 custom
    }

    // ========================================================================
    // BuildChatBody
    // ========================================================================

    [Fact]
    public void BuildChatBody_BuildsCorrectStructure()
    {
        var request = MakeChatRequest(model: "openclaw/research");

        var body = OpenClawApiClient.BuildChatBody(request);
        var json = JsonSerializer.Serialize(body);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().ShouldBe("openclaw/research");
        root.GetProperty("stream").GetBoolean().ShouldBeFalse();
        root.GetProperty("messages").GetArrayLength().ShouldBe(1);
        root.GetProperty("messages")[0].GetProperty("role").GetString().ShouldBe("user");
        root.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe("hello");
        root.TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public void BuildChatBody_WithUser_IncludesUserField()
    {
        var request = MakeChatRequest(user: "deploy-session-42");

        var body = OpenClawApiClient.BuildChatBody(request);
        var json = JsonSerializer.Serialize(body);

        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("user").GetString().ShouldBe("deploy-session-42");
    }

    [Fact]
    public void BuildChatBody_DefaultModel_IsOpenclaw()
    {
        var request = MakeChatRequest(model: null);

        var body = OpenClawApiClient.BuildChatBody(request);
        var json = JsonSerializer.Serialize(body);

        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("model").GetString().ShouldBe("openclaw");
    }

    // ========================================================================
    // ParseChatResponse
    // ========================================================================

    [Fact]
    public void ParseChatResponse_ValidOpenAIFormat_ExtractsContentAndModel()
    {
        var json = """
        {
            "id": "chatcmpl-abc",
            "object": "chat.completion",
            "model": "openclaw/default",
            "choices": [{
                "index": 0,
                "message": { "role": "assistant", "content": "Release notes for v2.1..." },
                "finish_reason": "stop"
            }],
            "usage": { "prompt_tokens": 10, "completion_tokens": 20, "total_tokens": 30 }
        }
        """;

        var response = OpenClawApiClient.ParseChatResponse(ParseJson(json));

        response.Ok.ShouldBeTrue();
        response.Content.ShouldBe("Release notes for v2.1...");
        response.Model.ShouldBe("openclaw/default");
        response.FinishReason.ShouldBe("stop");
        response.Error.ShouldBeNull();
    }

    [Fact]
    public void ParseChatResponse_ErrorResponse_ReturnsOkFalse()
    {
        var json = """{"error":{"type":"invalid_request_error","message":"model not found"}}""";

        var response = OpenClawApiClient.ParseChatResponse(ParseJson(json));

        response.Ok.ShouldBeFalse();
        response.Content.ShouldBeNull();
        response.Error.ShouldBe("model not found");
    }

    [Fact]
    public void ParseChatResponse_EmptyChoices_ReturnsOkFalse()
    {
        var json = """{"model":"openclaw","choices":[]}""";

        var response = OpenClawApiClient.ParseChatResponse(ParseJson(json));

        response.Ok.ShouldBeFalse();
        response.Content.ShouldBeNull();
    }

    [Fact]
    public void ParseChatResponse_UndefinedElement_ReturnsEmptyResponseError()
    {
        var response = OpenClawApiClient.ParseChatResponse(default);

        response.Ok.ShouldBeFalse();
        response.Error.ShouldContain("Empty response");
    }

    [Fact]
    public void ParseChatResponse_NullContent_ReturnsOkFalse()
    {
        var json = """{"model":"openclaw","choices":[{"index":0,"message":{"role":"assistant","content":null},"finish_reason":"stop"}]}""";

        var response = OpenClawApiClient.ParseChatResponse(ParseJson(json));

        response.Ok.ShouldBeFalse();
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static OpenClawChatRequest MakeChatRequest(string model = "openclaw", string modelOverride = null, string sessionKey = null, string agentId = null, string channel = null, string user = null)
    {
        return new OpenClawChatRequest(
            "https://claw.example.com",
            "test-token",
            new List<OpenClawChatMessage> { new("user", "hello") },
            model,
            modelOverride,
            sessionKey,
            agentId,
            channel,
            user,
            TimeSpan.FromSeconds(30));
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
