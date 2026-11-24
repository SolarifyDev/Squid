using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Squid.Api.Setup;
using Squid.Core.Settings.Setup;
using Squid.Core.Setup.Embedded;
using Squid.Message.Models.Setup;

namespace Squid.UnitTests.Setup;

/// <summary>Drives the real setup host over HTTP on a loopback port (no database is touched).</summary>
public sealed class SetupHostEndpointTests : IAsyncLifetime
{
    private readonly TempDataDirectory _dir = new();
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"squid-content-{Guid.NewGuid():N}");
    private WebApplication _app;
    private SetupSession _session;
    private HttpClient _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot"));
        File.WriteAllText(Path.Combine(_contentRoot, "wwwroot", "index.html"), "<html>squid-spa</html>");

        var configuration = _dir.Configuration(new() { ["EmbeddedServices:Postgres:BinDirectory"] = Path.Combine(_dir.Path, "no-pg"), ["EmbeddedServices:Redis:ServerPath"] = Path.Combine(_dir.Path, "no-redis"), ["Halibut:Polling:Port"] = "10943" });
        var port = FreePort();

        _session = new SetupSession(configuration, new EmbeddedServiceHost(new ProcessRunner(), _dir.Path), SetupToken.LoadOrCreate(new DataDirectorySetting(configuration)));
        _app = SetupHost.Build(Array.Empty<string>(), configuration, _session, _contentRoot, b => b.WebHost.UseUrls($"http://127.0.0.1:{port}"));

        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _dir.Dispose();
        Directory.Delete(_contentRoot, true);
    }

    [Fact]
    public async Task Status_IsPublic_AndReportsWhatThisImageCanOffer()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/setup/status");

        status.GetProperty("required").GetBoolean().ShouldBeTrue();
        status.GetProperty("builtinDatabaseAvailable").GetBoolean().ShouldBeFalse();
        status.GetProperty("builtinRedisAvailable").GetBoolean().ShouldBeFalse();
        status.GetProperty("defaultPollingPort").GetInt32().ShouldBe(10943);
    }

    [Theory]
    [InlineData("/api/setup/progress", "GET")]
    [InlineData("/api/setup/install", "POST")]
    [InlineData("/api/setup/database/test", "POST")]
    [InlineData("/api/setup/redis/test", "POST")]
    public async Task EveryOtherEndpoint_RequiresTheToken(string path, string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? JsonContent.Create(new { }) : null };
        request.Headers.Add(SetupToken.HeaderName, "wrong");

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Install_InvalidRequest_Returns400WithFieldErrors_AndStartsNothing()
    {
        var response = await PostAsync("/api/setup/install", new { database = new { mode = "Builtin" }, redis = new { mode = "Builtin" }, admin = new { userName = "admin", password = "x" }, server = new { externalUrl = "" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();

        errors.ShouldBe(new[] { "database.mode", "redis.mode", "admin.password", "server.externalUrl" }, ignoreOrder: true);
        _session.Tracker.Snapshot().State.ShouldBe(SetupInstallState.Idle);
    }

    [Fact]
    public async Task Install_UnreachableDatabase_FailsOnTheDatabaseStep_AndCanBeRetried()
    {
        var request = new { database = new { mode = "External", connectionString = "Host=127.0.0.1;Port=1;Database=squid;Username=u;Password=p;Timeout=2" }, redis = new { mode = "External", connectionString = "127.0.0.1:1" }, admin = new { userName = "admin", password = "longenough" }, server = new { externalUrl = "http://localhost:8080" } };

        (await PostAsync("/api/setup/install", request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var progress = await WaitForAsync(p => p.State == SetupInstallState.Failed);

        progress.Steps.Single(s => s.Key == "database").Status.ShouldBe(SetupStepStatus.Failed);
        progress.Steps.Single(s => s.Key == "persist").Status.ShouldBe(SetupStepStatus.Pending);
        File.Exists(_dir.ConfigFile).ShouldBeFalse();
        _session.Completed.IsCompleted.ShouldBeFalse();

        (await PostAsync("/api/setup/install", request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DatabaseTest_GarbageConnectionString_ExplainsInsteadOfThrowing()
    {
        var response = await PostAsync("/api/setup/database/test", new { mode = "External", connectionString = "garbage" });
        var result = await response.Content.ReadFromJsonAsync<DatabaseConnectionTestResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Success.ShouldBeFalse();
        result.Message.ShouldStartWith("Invalid connection string");
    }

    [Theory]
    [InlineData("/setup", HttpStatusCode.OK, true)]
    [InlineData("/projects/123", HttpStatusCode.OK, true)]
    [InlineData("/api/unknown", HttpStatusCode.NotFound, false)]
    [InlineData("/hangfire", HttpStatusCode.NotFound, false)]
    [InlineData("/auth/login", HttpStatusCode.NotFound, false)]
    [InlineData("/missing.js", HttpStatusCode.NotFound, false)]
    public async Task SpaFallback_ServesClientRoutes_ButNeverServerPrefixes(string path, HttpStatusCode expectedStatus, bool expectSpa)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.ShouldBe(expectedStatus);
        (await response.Content.ReadAsStringAsync()).Contains("squid-spa").ShouldBe(expectSpa);
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(SetupToken.HeaderName, _session.Token.Value);

        return _client.SendAsync(request);
    }

    private async Task<SetupProgress> WaitForAsync(Func<SetupProgress, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var progress = _session.Tracker.Snapshot();

            if (condition(progress)) return progress;

            await Task.Delay(100);
        }

        throw new TimeoutException($"Setup progress never reached the expected state; last: {JsonSerializer.Serialize(_session.Tracker.Snapshot())}");
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
