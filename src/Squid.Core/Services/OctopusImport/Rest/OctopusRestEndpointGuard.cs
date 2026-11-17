using System.Net;
using System.Net.Sockets;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// SSRF guard for the Octopus REST source. The server URL is checked when the client is built, and every
/// connection resolves DNS itself and dials only an address it has just validated, so a name that re-resolves
/// to a forbidden address (DNS rebinding) is still refused.
/// Private ranges (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7) are allowed on purpose: Octopus servers usually live
/// on the intranet. An operator who wants to narrow that sets <see cref="OctopusRestOptions.AllowedHostsEnvVar"/>.
/// </summary>
public static class OctopusRestEndpointGuard
{
    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("169.254.169.254"),
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("168.63.129.16"),
        IPAddress.Parse("fd00:ec2::254")
    ];

    public static Uri ParseServerUri(string serverUrl, OctopusRestOptions options)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri))
            throw new OctopusRestException(OctopusRestErrorKind.NotOctopus);

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new OctopusRestException(OctopusRestErrorKind.Denied);

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);

        EnsureHostAllowed(uri.Host, options);
        EnsureLiteralAddressAllowed(uri.Host, options);

        return BaseUri(uri);
    }

    public static bool IsDenied(IPAddress address, OctopusRestOptions options)
    {
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(candidate)) return !options.AllowLoopback;

        return IsUnspecified(candidate) || IsLinkLocal(candidate) || IsMulticastOrBroadcast(candidate) || MetadataAddresses.Contains(candidate);
    }

    public static ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, OctopusRestOptions options, CancellationToken ct) => ConnectAsync(endPoint, options, Dns.GetHostAddressesAsync, ct);

    /// <summary>Connects through <paramref name="resolve"/> (the system resolver in production; a test injects its failures).</summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, OctopusRestOptions options, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var addresses = await ResolveAsync(endPoint.Host, resolve, ct).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new OctopusRestException(OctopusRestErrorKind.Unreachable);

        if (addresses.Any(a => IsDenied(a, options)))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);

        return await DialAsync(addresses, endPoint.Port, ct).ConfigureAwait(false);
    }

    private static void EnsureHostAllowed(string host, OctopusRestOptions options)
    {
        if (options.AllowedHosts.Count == 0) return;

        if (!options.AllowedHosts.Any(allowed => HostMatches(host, allowed)))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);
    }

    private static bool HostMatches(string host, string allowed)
    {
        if (allowed.StartsWith("*.", StringComparison.Ordinal))
            return host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase);

        return string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureLiteralAddressAllowed(string host, OctopusRestOptions options)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return;

        if (IsDenied(address, options))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);
    }

    private static Uri BaseUri(Uri uri)
    {
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";

        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, path).Uri;
    }

    private static bool IsUnspecified(IPAddress address) => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None);

    private static bool IsLinkLocal(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? address.IsIPv6LinkLocal : address.GetAddressBytes() is [169, 254, _, _];

    private static bool IsMulticastOrBroadcast(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? address.IsIPv6Multicast : address.GetAddressBytes()[0] >= 224;

    // A resolver that could not answer now (EAI_AGAIN) is left to propagate, so the client retries it like a dropped connection;
    // a name that does not resolve fails at once.
    private static async Task<IPAddress[]> ResolveAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return [literal];

        try
        {
            return await resolve(host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode != SocketError.TryAgain)
        {
            throw new OctopusRestException(OctopusRestErrorKind.Unreachable);
        }
    }

    private static async Task<Stream> DialAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(addresses, port, ct).ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
