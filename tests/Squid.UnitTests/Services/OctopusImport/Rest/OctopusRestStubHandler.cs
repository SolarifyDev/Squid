using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>In-memory Octopus server: answers each GET from a route function and records what was asked.</summary>
internal sealed class OctopusRestStubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(Uri Uri, string ApiKey)> Requests { get; } = [];

    public IEnumerable<string> PathsAndQueries => Requests.Select(r => r.Uri.PathAndQuery);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = request.Headers.TryGetValues(OctopusRestClient.ApiKeyHeader, out var values) ? values.Single() : null;

        lock (Requests) Requests.Add((request.RequestUri, apiKey));

        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    public static object Root(bool spaces = true)
        => new { Application = "Octopus Deploy", Version = "2023.4.1", InstallationId = "inst-1", Links = spaces ? new Dictionary<string, string> { ["Spaces"] = "/api/spaces{/id}" } : new Dictionary<string, string>() };

    public static object Page<T>(IReadOnlyCollection<T> items, int total) => new { Items = items, TotalResults = total, ItemsPerPage = 100 };
}
