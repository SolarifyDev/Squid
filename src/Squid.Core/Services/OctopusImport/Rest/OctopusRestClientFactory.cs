using System.Net;
using Squid.Core.Settings.Security;

namespace Squid.Core.Services.OctopusImport.Rest;

public interface IOctopusRestClientFactory : IScopedDependency
{
    OctopusRestClient Create(string serverUrl, string apiKey);
}

/// <summary>
/// Builds clients on a dedicated handler: no automatic redirects (the API key must never follow a redirect to
/// another host), no cookies, no proxy (a proxy would dial the target itself and bypass the address guard), and
/// a connect callback that resolves and validates every address before dialling it. Each client carries the
/// fingerprint of its key under the master key (<see cref="OctopusRestKeyBinding"/>).
/// </summary>
public sealed class OctopusRestClientFactory(SecuritySetting security, OctopusRestOptions options = null) : IOctopusRestClientFactory
{
    private readonly OctopusRestOptions _options = options ?? OctopusRestOptions.FromEnvironment();
    private readonly byte[] _masterKey = OctopusRestKeyBinding.MasterKeyBytes(security?.MasterKey);

    public OctopusRestClient Create(string serverUrl, string apiKey) => new(serverUrl, apiKey, _options, CreateHandler(_options)) { KeyFingerprint = OctopusRestKeyBinding.Fingerprint(_masterKey, apiKey) };

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
