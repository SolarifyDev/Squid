using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Mcp;

public partial class McpEndpointTests
{
    [Fact]
    public async Task ToolsCall_UsesPerRequestMetadataOnlyForJuly2026Protocol()
    {
        using var client = _fixture.Enabled.CreateClient();
        var seed = await _fixture.CreateReadToolSeedAsync();

        var legacy = await SendToolCallAsync(
            client,
            seed.ApiKey,
            "list_projects",
            new { spaceId = seed.SpaceId, pageSize = 1 },
            protocolVersion: "2025-11-25",
            includePerRequestMetadata: false);
        _fixture.Enabled.LogSink.Clear();
        var modern = await SendToolCallAsync(
            client,
            seed.ApiKey,
            "list_projects",
            new { spaceId = seed.SpaceId, pageSize = 1 },
            protocolVersion: McpEndpointFixture.ProtocolVersion,
            includePerRequestMetadata: false);

        legacy.StatusCode.ShouldBe(HttpStatusCode.OK, legacy.Body);
        GetMcpResult(legacy.Body).GetProperty("isError").GetBoolean().ShouldBeFalse();
        modern.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        modern.Body.ShouldContain("must include", Case.Insensitive);
        _fixture.Enabled.LogSink.ContainsMessage("Result=\"protocol_error\"").ShouldBeTrue(
            string.Join(Environment.NewLine, _fixture.Enabled.LogSink.Messages));
    }

    [Fact]
    public async Task ValidKey_ListsTools()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();

        var result = await SendToolsListAsync(client, apiKey: key.ApiKey);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        GetTools(result.Body).EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ShouldBe(
        [
            "list_spaces", "list_projects", "list_environments",
            "list_releases", "list_deployments", "get_task_status"
        ]);

        foreach (var tool in GetTools(result.Body).EnumerateArray())
        {
            tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().ShouldBeTrue();
            tool.TryGetProperty("outputSchema", out var outputSchema).ShouldBeTrue();
            outputSchema.ValueKind.ShouldBe(JsonValueKind.Object);
            outputSchema.GetProperty("type").GetString().ShouldBe("object");
            outputSchema.GetProperty("oneOf").GetArrayLength().ShouldBe(2);
        }
    }

    [Fact]
    public async Task ReadTools_UseAuthenticatedScopeAndStableErrors()
    {
        using var client = _fixture.Enabled.CreateClient();
        var seed = await _fixture.CreateReadToolSeedAsync();
        _fixture.Enabled.LogSink.Clear();

        var success = await SendToolCallAsync(client, seed.ApiKey, "list_projects", new
        {
            spaceId = seed.SpaceId,
            keyword = seed.Marker,
            pageSize = 1
        });

        success.StatusCode.ShouldBe(HttpStatusCode.OK, success.Body);
        var successResult = GetMcpResult(success.Body);
        successResult.GetProperty("isError").GetBoolean().ShouldBeFalse();
        var successData = successResult.GetProperty("structuredContent");
        successData.GetProperty("items").GetArrayLength().ShouldBe(1);
        var item = successData.GetProperty("items")[0];
        item.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "name", "slug"]);
        item.GetProperty("name").GetString().ShouldContain(seed.Marker);
        successData.GetProperty("nextCursor").GetString().ShouldNotBeNullOrWhiteSpace();

        var invalidParent = await SendToolCallAsync(client, seed.ApiKey, "list_projects", new
        {
            spaceId = 999999
        });
        var invalidParentResult = GetMcpResult(invalidParent.Body);
        AssertToolError(invalidParentResult, "NOT_FOUND_OR_FORBIDDEN");

        var cursor = successData.GetProperty("nextCursor").GetString();
        var reusedCursor = await SendToolCallAsync(client, seed.ApiKey, "list_environments", new
        {
            spaceId = seed.SpaceId,
            projectId = seed.ProjectId,
            cursor
        });
        AssertToolError(GetMcpResult(reusedCursor.Body), "INVALID_ARGUMENT");

        var wrongType = await SendToolCallAsync(client, seed.ApiKey, "list_projects", new
        {
            spaceId = "not-an-integer"
        });
        AssertToolError(GetMcpResult(wrongType.Body), "INVALID_ARGUMENT");

        _fixture.Enabled.LogSink.ContainsMessage(seed.Marker).ShouldBeFalse(
            customMessage: "tool input and projected output must not be written to MCP audit logs");
        _fixture.Enabled.LogSink.ContainsMessage(seed.ProjectName).ShouldBeFalse(
            customMessage: "tool output must not be written to MCP audit logs");
        var auditLog = string.Join(Environment.NewLine, _fixture.Enabled.LogSink.Messages);
        _fixture.Enabled.LogSink.ContainsMessage("ProtocolMethod=\"tools/call\"").ShouldBeTrue(auditLog);
        _fixture.Enabled.LogSink.ContainsMessage("ToolName=\"list_projects\"").ShouldBeTrue(auditLog);
        _fixture.Enabled.LogSink.ContainsMessage("Result=\"success\"").ShouldBeTrue(auditLog);
        _fixture.Enabled.LogSink.ContainsMessage("Result=\"not_found_or_forbidden\"").ShouldBeTrue(auditLog);
        _fixture.Enabled.LogSink.ContainsMessage("Result=\"invalid_argument\"").ShouldBeTrue(auditLog);
    }
}
