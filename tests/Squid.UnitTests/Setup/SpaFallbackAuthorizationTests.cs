using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Squid.Api.Extensions;

namespace Squid.UnitTests.Setup;

/// <summary>
/// The bundled UI on the NORMAL API host, wired with the API's real authentication setup (whose
/// FallbackPolicy requires an authenticated user). A browser deep link / refresh carries no token
/// and must still get index.html; API routes must still be protected and unknown ones still 404.
/// </summary>
public sealed class SpaFallbackAuthorizationTests : IAsyncLifetime
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"squid-spa-auth-{Guid.NewGuid():N}");
    private WebApplication _app;
    private HttpClient _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
        File.WriteAllText(Path.Combine(_contentRoot, "wwwroot", "index.html"), "<html>squid-spa</html>");

        var port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _contentRoot });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Authentication:Jwt:SymmetricKey"] = "unit-test-key-that-is-long-enough-1234567890" }).Build();

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddCustomAuthentication(configuration);

        _app = builder.Build();
        _app.UseBundledSpaFiles(_app.Environment);
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGet("/api/protected", () => "secret");
        _app.MapBundledSpaFallback(_app.Environment);

        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_contentRoot, true);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/users/sign-in")]
    [InlineData("/Spaces-1/projects/123")]
    [InlineData("/setup")]
    public async Task ClientRoute_WithoutToken_ServesTheUi(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("squid-spa");
    }

    [Fact]
    public async Task ApiRoute_WithoutToken_IsStillProtected()
    {
        (await _client.GetAsync("/api/protected")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownApiRoute_IsNotAnsweredWithTheUi()
    {
        var response = await _client.GetAsync("/api/does-not-exist");

        (await response.Content.ReadAsStringAsync()).ShouldNotContain("squid-spa");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
