using System.Net;

namespace Squid.Core.Services.OctopusImport.Rest;

public interface IOctopusRestClientFactory : IScopedDependency
{
    OctopusRestClient Create(string serverUrl, string apiKey);
}

/// <summary>
/// Builds clients on a dedicated handler: no automatic redirects (the API key must never follow a redirect to
/// another host), no cookies, no proxy (a proxy would dial the target itself and bypass the address guard), and
/// a connect callback that resolves and validates every address before dialling it.
/// </summary>
public sealed class OctopusRestClientFactory(OctopusRestOptions options = null) : IOctopusRestClientFactory
{
    private readonly OctopusRestOptions _options = options ?? OctopusRestOptions.FromEnvironment();

    public OctopusRestClient Create(string serverUrl, string apiKey) => new(serverUrl, apiKey, _options, CreateHandler(_options));

    public static SocketsHttpHandler CreateHandler(OctopusRestOptions options)
        => new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = (context, ct) => OctopusRestEndpointGuard.ConnectAsync(context.DnsEndPoint, options, ct)
        };
}
