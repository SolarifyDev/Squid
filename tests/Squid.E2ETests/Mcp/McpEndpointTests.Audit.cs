using System.Net;
using Serilog;
using Shouldly;
using Squid.Api.Extensions;
using Squid.E2ETests.Infrastructure;
using Xunit;
using Environment = System.Environment;

namespace Squid.E2ETests.Mcp;

public partial class McpEndpointTests
{
    [Fact]
    public async Task Audit_DoesNotLogClientControlledToolName()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();
        var headerSecret = $"API-{Guid.NewGuid():N}".ToUpperInvariant();
        var payloadSecret = $"PAYLOAD-{Guid.NewGuid():N}".ToUpperInvariant();

        _fixture.Enabled.LogSink.Clear();

        var result = await SendToolsListAsync(
            client,
            apiKey: key.ApiKey,
            mcpName: headerSecret,
            payloadMarker: payloadSecret);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        _fixture.Enabled.LogSink.ContainsMessage(headerSecret).ShouldBeFalse();
        _fixture.Enabled.LogSink.ContainsMessage(payloadSecret).ShouldBeFalse();
        _fixture.Enabled.LogSink.ContainsMessage("ProtocolMethod=\"tools/list\"").ShouldBeTrue(
            string.Join(Environment.NewLine, _fixture.Enabled.LogSink.Messages));
    }

    [Fact]
    public async Task Audit_ProductionLoggingHidesMcpSecretsAcrossSuccessAndErrorPaths()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();
        var keySecret = key.ApiKey;
        var headerSecret = $"HEADER-{Guid.NewGuid():N}".ToUpperInvariant();
        var payloadSecret = $"PAYLOAD-{Guid.NewGuid():N}".ToUpperInvariant();
        var argumentSecret = $"ARGUMENT-{Guid.NewGuid():N}".ToUpperInvariant();
        var unknownToolSecret = $"UNKNOWN-{Guid.NewGuid():N}".ToUpperInvariant();
        var malformedSecret = $"MALFORMED-{Guid.NewGuid():N}".ToUpperInvariant();

        _fixture.Enabled.LogSink.Clear();
        _fixture.Enabled.WriteSdkWarningProbe();

        var tools = await SendToolsListAsync(
            client,
            apiKey: key.ApiKey,
            mcpName: headerSecret,
            payloadMarker: payloadSecret);
        var unknown = await SendToolCallAsync(
            client,
            key.ApiKey,
            unknownToolSecret,
            new { marker = argumentSecret });
        var malformed = await SendRawAsync(
            client,
            key.ApiKey,
            $"{{ \"jsonrpc\": \"2.0\", \"id\": \"{malformedSecret}\" ");

        tools.StatusCode.ShouldBe(HttpStatusCode.OK);
        unknown.StatusCode.ShouldBe(HttpStatusCode.OK);
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var auditLog = string.Join(Environment.NewLine, _fixture.Enabled.LogSink.Messages);
        foreach (var secret in new[]
                 {
                     keySecret, headerSecret, payloadSecret, argumentSecret, unknownToolSecret,
                     malformedSecret
                 })
        {
            _fixture.Enabled.LogSink.ContainsMessage(secret).ShouldBeFalse(
                customMessage: $"client-controlled value {secret} must not reach application logs");
        }

        _fixture.Enabled.LogSink.ContainsMessage("ProtocolMethod=\"tools/call\"").ShouldBeTrue(auditLog);
        _fixture.Enabled.LogSink.ContainsMessage("ToolName=\"unknown\"").ShouldBeTrue(auditLog);
        _fixture.Enabled.LogSink.ContainsMessage("Result=\"invalid_argument\"").ShouldBeTrue(auditLog);
    }

    [Fact]
    public void McpLogging_ExcludesSdkSourceContextButKeepsSquidAudit()
    {
        var sink = new CapturingLogSink();
        var logger = new LoggerConfiguration()
            .ConfigureMcpLogging()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var probeSecret = $"SDK-PROBE-{Guid.NewGuid():N}".ToUpperInvariant();

        logger.ForContext("SourceContext", "ModelContextProtocol.Server")
            .Warning("sdk warning {Probe}", probeSecret);
        logger.ForContext("SourceContext", "Squid.Mcp.SquidMcpAudit")
            .Information("squid audit {Marker}", "audit-visible");

        sink.ContainsMessage(probeSecret).ShouldBeFalse();
        sink.ContainsMessage("audit-visible").ShouldBeTrue();
    }

    [Fact]
    public async Task ProtocolError_AuditUsesStableCategoryWithoutLoggingPayload()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();
        var marker = $"PROTOCOL-{Guid.NewGuid():N}".ToUpperInvariant();
        var body = $$"""
            {
              "jsonrpc": "2.0",
              "id": "{{marker}}",
              "method": "unknown/method",
              "params": {
                "_meta": {
                  "io.modelcontextprotocol/protocolVersion": "2026-07-28",
                  "io.modelcontextprotocol/clientCapabilities": {}
                },
                "message": "{{marker}}"
              }
            }
            """;

        _fixture.Enabled.LogSink.Clear();
        var result = await SendRawAsync(client, key.ApiKey, body, mcpMethod: "unknown/method");

        result.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        result.Body.ShouldContain("\"error\"");
        _fixture.Enabled.LogSink.ContainsMessage(marker).ShouldBeFalse();
        _fixture.Enabled.LogSink.ContainsMessage(key.ApiKey).ShouldBeFalse();
        var auditLog = string.Join(Environment.NewLine, _fixture.Enabled.LogSink.Messages);
        _fixture.Enabled.LogSink.ContainsMessage("Result=\"protocol_error\"").ShouldBeTrue(auditLog);
        _fixture.Enabled.LogSink.ContainsMessage("ToolName=\"unknown\"").ShouldBeTrue(auditLog);
    }
}
