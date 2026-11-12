using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public class OctopusRestClientTests
{
    private const string ServerUrl = "https://octopus.example.com/";
    private const string ApiKey = "API-SECRETKEY0123456789";

    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task Paging_SkipTake()
    {
        var handler = new OctopusRestStubHandler(request => OctopusRestStubHandler.Json(OctopusRestStubHandler.Page(Enumerable.Range(0, PageLength(request)).Select(i => new { Id = $"Releases-{i}" }).ToList(), 250)));
        using var client = Client(handler);

        var items = await client.GetAllPagesAsync<Dictionary<string, string>>("api/Spaces-1/projects/Projects-1/releases", CancellationToken.None);

        items.Count.ShouldBe(250);
        handler.PathsAndQueries.ShouldBe(["/api/Spaces-1/projects/Projects-1/releases?skip=0&take=100", "/api/Spaces-1/projects/Projects-1/releases?skip=100&take=100", "/api/Spaces-1/projects/Projects-1/releases?skip=200&take=100"]);
        handler.Requests.ShouldAllBe(r => r.ApiKey == ApiKey);
    }

    [Fact]
    public async Task Retry429_HonorsRetryAfter()
    {
        var calls = 0;
        var handler = new OctopusRestStubHandler(_ =>
        {
            if (calls++ > 0) return OctopusRestStubHandler.Json(OctopusRestStubHandler.Root());

            var throttled = OctopusRestStubHandler.Status(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return throttled;
        });
        using var client = Client(handler);

        var root = await client.GetRootAsync(CancellationToken.None);

        root.InstallationId.ShouldBe("inst-1");
        _delays.ShouldBe([TimeSpan.FromSeconds(7)]);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task FiveXx_RetryCap()
    {
        var handler = new OctopusRestStubHandler(_ => OctopusRestStubHandler.Status(HttpStatusCode.ServiceUnavailable));
        using var client = Client(handler, new OctopusRestOptions { MaxRetries = 5, RetryBaseDelay = TimeSpan.FromSeconds(1), MaxRetryDelay = TimeSpan.FromSeconds(4) });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
        handler.Requests.Count.ShouldBe(6);
        _delays.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, OctopusRestErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, OctopusRestErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, OctopusRestErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, OctopusRestErrorKind.Unreachable)]
    public async Task Status401_TypedUnauthorized_AndNeverRetried(HttpStatusCode status, OctopusRestErrorKind expected)
    {
        var handler = new OctopusRestStubHandler(_ => OctopusRestStubHandler.Status(status));
        using var client = Client(handler);

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(expected);
        handler.Requests.Count.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://evil.example.net/api")]
    [InlineData("http://octopus.example.com/api")]
    [InlineData("https://octopus.example.com:8443/api")]
    public async Task Redirect_CrossHost_NotFollowed_KeyNotResent(string location)
    {
        var handler = new OctopusRestStubHandler(_ => Redirect(location));
        using var client = Client(handler);

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Denied);
        handler.Requests.ShouldHaveSingleItem().Uri.Host.ShouldBe("octopus.example.com");
    }

    [Fact]
    public async Task Redirect_SameOrigin_FollowedWithKey()
    {
        var handler = new OctopusRestStubHandler(request => request.RequestUri!.AbsolutePath == "/api" ? Redirect("/octopus/api") : OctopusRestStubHandler.Json(OctopusRestStubHandler.Root()));
        using var client = Client(handler);

        await client.GetRootAsync(CancellationToken.None);

        handler.PathsAndQueries.ShouldBe(["/api", "/octopus/api"]);
        handler.Requests.ShouldAllBe(r => r.ApiKey == ApiKey);
    }

    [Fact]
    public async Task Redirect_LoopBeyondCap_Unreachable()
    {
        var handler = new OctopusRestStubHandler(_ => Redirect("/api"));
        using var client = Client(handler, new OctopusRestOptions { MaxRedirects = 3, MaxRetries = 0 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
        handler.Requests.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://169.254.10.10/")]
    [InlineData("http://100.100.100.200/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00:ec2::254]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::]/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("ftp://octopus.example.com/")]
    [InlineData("https://user:pass@octopus.example.com/")]
    public void LinkLocalAndMetadata_Denied(string serverUrl)
    {
        var handler = new OctopusRestStubHandler(_ => OctopusRestStubHandler.Json(OctopusRestStubHandler.Root()));

        var ex = Should.Throw<OctopusRestException>(() => new OctopusRestClient(serverUrl, ApiKey, new OctopusRestOptions { AllowLoopback = true }, handler));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Denied);
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/", false, true)]
    [InlineData("http://[::1]/", false, true)]
    [InlineData("http://127.0.0.1:8080/", true, false)]
    public async Task Loopback_DeniedUnlessEnv(string serverUrl, bool allowLoopback, bool denied)
    {
        var handler = new OctopusRestStubHandler(_ => OctopusRestStubHandler.Json(OctopusRestStubHandler.Root()));
        var options = new OctopusRestOptions { AllowLoopback = allowLoopback };

        if (denied)
        {
            Should.Throw<OctopusRestException>(() => new OctopusRestClient(serverUrl, ApiKey, options, handler)).Kind.ShouldBe(OctopusRestErrorKind.Denied);
            return;
        }

        using var client = new OctopusRestClient(serverUrl, ApiKey, options, handler);

        (await client.GetRootAsync(CancellationToken.None)).IsOctopus.ShouldBeTrue();
    }

    // DNS names are validated at connect time, against the addresses the connection will actually dial.
    [Theory]
    [InlineData(false, OctopusRestErrorKind.Denied)]
    [InlineData(true, null)]
    public async Task DnsNameResolvingToLoopback_DeniedAtConnectUnlessEnv(bool allowLoopback, OctopusRestErrorKind? expected)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connect = OctopusRestEndpointGuard.ConnectAsync(new DnsEndPoint("localhost", port), new OctopusRestOptions { AllowLoopback = allowLoopback }, CancellationToken.None).AsTask();

        if (expected != null)
        {
            (await Should.ThrowAsync<OctopusRestException>(() => connect)).Kind.ShouldBe(expected.Value);
            return;
        }

        await using var stream = await connect;
        stream.CanWrite.ShouldBeTrue();
    }

    [Fact]
    public async Task ProductionHandler_DeniedConnect_SurfacesTypedDenied()
    {
        var options = new OctopusRestOptions { AllowLoopback = false };
        using var client = new OctopusRestClient("http://localhost:1/", ApiKey, options, OctopusRestClientFactory.CreateHandler(options));

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Denied);
        ex.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData("octopus.example.com", "https://octopus.example.com/", false)]
    [InlineData("OCTOPUS.example.com", "https://octopus.example.com/", false)]
    [InlineData("*.example.com", "https://deploy.example.com/", false)]
    [InlineData("octopus.example.com", "https://other.example.com/", true)]
    [InlineData("*.example.com", "https://example.org/", true)]
    public void AllowedHosts_Enforced(string allowed, string serverUrl, bool denied)
    {
        var options = new OctopusRestOptions { AllowedHosts = [allowed] };
        var handler = new OctopusRestStubHandler(_ => OctopusRestStubHandler.Json(OctopusRestStubHandler.Root()));

        var create = () => new OctopusRestClient(serverUrl, ApiKey, options, handler);

        if (denied) Should.Throw<OctopusRestException>(create).Kind.ShouldBe(OctopusRestErrorKind.Denied);
        else Should.NotThrow(create);
    }

    [Fact]
    public async Task DeclaredLengthOverCap_RefusedBeforeReadingTheBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Content.Headers.ContentLength = 4096;
        var handler = new OctopusRestStubHandler(_ => response);
        using var client = Client(handler, new OctopusRestOptions { MaxResponseBytes = 1024 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetAsync<Dictionary<string, string>>("api", CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseOverCap_TooLarge(bool declaresLength)
    {
        var body = Encoding.UTF8.GetBytes(new string('x', 2048));
        var handler = new OctopusRestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = declaresLength ? new ByteArrayContent(body) : new StreamContent(new UnknownLengthStream(body)) });
        using var client = Client(handler, new OctopusRestOptions { MaxResponseBytes = 1024 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
    }

    [Theory]
    [InlineData("<html><body>Welcome</body></html>")]
    [InlineData("{\"Application\":\"Octopus Deploy\",\"Version\":\"2023.4\"}")]
    [InlineData("{\"Version\":\"1.0\",\"InstallationId\":\"x\"}")]
    public async Task NonOctopusRoot_NotOctopus(string body)
    {
        var handler = new OctopusRestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var client = Client(handler);

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.NotOctopus);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ExceptionMessage_NeverContainsKeyOrHost(HttpStatusCode status)
    {
        var handler = new OctopusRestStubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent($"key {ApiKey} rejected by octopus.example.com") });
        using var client = Client(handler, new OctopusRestOptions { MaxRetries = 1 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Message.ShouldNotContain(ApiKey);
        ex.Message.ShouldNotContain("octopus.example.com");
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public void ExceptionMessages_AreFixedAndSecretFree_ForEveryKind()
    {
        foreach (var kind in Enum.GetValues<OctopusRestErrorKind>())
        {
            var ex = new OctopusRestException(kind);

            ex.Message.ShouldBe(OctopusRestException.MessageFor(kind));
            ex.Message.ShouldNotContain("http", Case.Insensitive);
            ex.Message.ShouldNotBe("The Octopus server request failed.", $"{kind} has no message of its own");
        }
    }

    [Fact]
    public async Task TransportFailure_RetriedThenUnreachable()
    {
        var handler = new OctopusRestStubHandler(_ => throw new HttpRequestException("connection reset by octopus.example.com"));
        using var client = Client(handler, new OctopusRestOptions { MaxRetries = 2 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => client.GetRootAsync(CancellationToken.None));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
        handler.Requests.Count.ShouldBe(3);
    }

    private OctopusRestClient Client(OctopusRestStubHandler handler, OctopusRestOptions options = null)
        => new(ServerUrl, ApiKey, options ?? new OctopusRestOptions(), handler, (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });

    private static int PageLength(HttpRequestMessage request)
    {
        var skip = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["skip"]!);

        return Math.Min(100, 250 - skip);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = OctopusRestStubHandler.Status(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class UnknownLengthStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
