using System.Net;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Mcp;

[Trait("Category", "E2E")]
public partial class McpEndpointTests : IClassFixture<McpEndpointFixture>
{
    private readonly McpEndpointFixture _fixture;

    public McpEndpointTests(McpEndpointFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EndpointDisabled_Returns404()
    {
        using var client = _fixture.Disabled.CreateClient();

        var result = await SendToolsListAsync(client);
        using var restRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/api-keys");
        var rest = await client.SendAsync(restRequest);

        result.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        rest.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void EnabledWithoutApiKeyScheme_FailsStartup()
    {
        var exception = Should.Throw<Exception>(() => _fixture.MissingApiKeyScheme.CreateClient());

        exception.ToString().ShouldContain("API Key", Case.Insensitive);
    }

    [Fact]
    public async Task InvalidOrMissingKey_Returns401()
    {
        using var client = _fixture.Enabled.CreateClient();

        var missing = await SendToolsListAsync(client);
        var invalid = await SendToolsListAsync(client, apiKey: "not-a-valid-key");

        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        invalid.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JwtOnly_Returns401()
    {
        using var client = _fixture.Enabled.CreateClient();
        var token = await _fixture.CreateJwtAsync();

        var result = await SendToolsListAsync(client, bearerToken: token);

        result.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokedAfterCacheHit_Returns401()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();

        var first = await SendToolsListAsync(client, apiKey: key.ApiKey);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _fixture.RevokeApiKeyAsync(key.UserId, key.ApiKeyId);

        var second = await SendToolsListAsync(client, apiKey: key.ApiKey);
        second.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AllowedOrigin_PreflightAllowsMcpHeaders()
    {
        using var client = _fixture.Enabled.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        request.Headers.TryAddWithoutValidation("Origin", McpEndpointFixture.AllowedOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "content-type, mcp-protocol-version, mcp-method, mcp-name, x-api-key");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single()
            .ShouldBe(McpEndpointFixture.AllowedOrigin);
        GetAllowedHeaders(response).ShouldContain("Mcp-Name");
        GetAllowedHeaders(response).ShouldContain("X-API-KEY");
    }

    [Fact]
    public async Task InvalidOrigin_Returns403()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();

        var result = await SendToolsListAsync(
            client,
            apiKey: key.ApiKey,
            origin: "https://not-allowed.example");

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AuthenticatedRestRoute_StillReturnsSuccess()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.TryAddWithoutValidation("X-API-KEY", key.ApiKey);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain(key.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task McpGet_RequiresApiKeyAndReturnsMethodNotAllowedForValidKey()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();

        using var missing = await client.GetAsync("/mcp");
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        validRequest.Headers.TryAddWithoutValidation("X-API-KEY", key.ApiKey);
        using var valid = await client.SendAsync(validRequest);

        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        valid.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task MalformedJson_IsRejectedByProtocolLayerWithoutBusinessCode()
    {
        using var client = _fixture.Enabled.CreateClient();
        var key = await _fixture.CreateApiKeyAsync();

        _fixture.Enabled.LogSink.Clear();
        var result = await SendRawAsync(client, key.ApiKey, "{");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.Body.ShouldNotContain("INVALID_ARGUMENT", Case.Insensitive);
        result.Body.ShouldNotContain("NOT_FOUND_OR_FORBIDDEN", Case.Insensitive);
        result.Body.ShouldNotContain("TEMPORARY_FAILURE", Case.Insensitive);
        _fixture.Enabled.LogSink.ContainsMessage("Result=\"protocol_error\"").ShouldBeTrue(
            string.Join(Environment.NewLine, _fixture.Enabled.LogSink.Messages));
    }
}
