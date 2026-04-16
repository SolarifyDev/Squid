using System.Net;
using Serilog;

namespace Squid.Tentacle.Flavors.LinuxTentacle;

/// <summary>
/// Resolves the hostname or IP that a Listening Tentacle registers with the
/// Squid Server — i.e. the address the Server will later try to connect back
/// to. Aligned with Octopus's <c>PublicHostNameConfiguration</c> enum
/// (<see href="https://github.com/OctopusDeploy/OctopusTentacle">reference</see>).
/// </summary>
/// <remarks>
/// Cloud deployments (EC2, Azure VM, GCE) expose a private hostname via
/// <c>hostname</c> and a separate public IP reachable by the Server. Using
/// <see cref="Dns.GetHostName"/> alone breaks registration because the name
/// resolves only inside the VPC. The modes below let the operator (or a
/// cloud-provisioning hook) pick the right source:
///
/// <list type="bullet">
/// <item><c>Custom</c> — use the explicit <c>--listening-host</c> / <c>Tentacle:ListeningHostName</c> value</item>
/// <item><c>ComputerName</c> — <c>Dns.GetHostName()</c> (Squid's historical default)</item>
/// <item><c>FQDN</c> — reverse-lookup the local host to its fully-qualified domain name</item>
/// <item><c>PublicIp</c> — query the cloud instance-metadata service (AWS IMDSv2 / Azure / GCE)</item>
/// </list>
/// </remarks>
public enum PublicHostNameMode
{
    /// <summary>User-supplied <c>ListeningHostName</c>. Default when that setting is non-empty.</summary>
    Custom,

    /// <summary>Short hostname from <c>Dns.GetHostName()</c>. Squid's legacy default.</summary>
    ComputerName,

    /// <summary>Fully-qualified domain name resolved via reverse DNS.</summary>
    FQDN,

    /// <summary>Public IPv4 discovered via cloud instance-metadata services.</summary>
    PublicIp
}

public static class PublicHostNameResolver
{
    public static PublicHostNameMode ParseMode(string raw, bool hasExplicitHostName)
    {
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<PublicHostNameMode>(raw, ignoreCase: true, out var parsed))
            return parsed;

        // Default: if the user supplied --listening-host, honour it (Custom); otherwise fall back to ComputerName
        // so existing deployments don't change behaviour when they upgrade.
        return hasExplicitHostName ? PublicHostNameMode.Custom : PublicHostNameMode.ComputerName;
    }

    /// <summary>
    /// Resolves the hostname/IP according to <paramref name="mode"/>. Each mode
    /// gracefully degrades to <see cref="PublicHostNameMode.ComputerName"/> if
    /// its primary lookup fails — registration should almost always succeed
    /// with _some_ name, even if it's not the ideal one.
    /// </summary>
    public static string Resolve(PublicHostNameMode mode, string customValue, HttpClient httpClient = null)
    {
        return mode switch
        {
            PublicHostNameMode.Custom => !string.IsNullOrWhiteSpace(customValue) ? customValue : Dns.GetHostName(),
            PublicHostNameMode.ComputerName => Dns.GetHostName(),
            PublicHostNameMode.FQDN => ResolveFqdn(),
            PublicHostNameMode.PublicIp => ResolvePublicIp(httpClient) ?? Dns.GetHostName(),
            _ => Dns.GetHostName()
        };
    }

    private static string ResolveFqdn()
    {
        try
        {
            var hostName = Dns.GetHostName();
            var entry = Dns.GetHostEntry(hostName);

            // IHostEntry.HostName is the canonical name if reverse DNS returns one, else the short name.
            return string.IsNullOrWhiteSpace(entry?.HostName) ? hostName : entry.HostName;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FQDN resolution failed — falling back to short host name");
            return Dns.GetHostName();
        }
    }

    private static string ResolvePublicIp(HttpClient providedClient)
    {
        // Try AWS IMDSv2 first (most deployments), then Azure, then GCE. Each call is
        // bounded by a short timeout so running outside a cloud (or with metadata disabled)
        // doesn't stall registration for minutes.
        var http = providedClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        try
        {
            var aws = TryAwsImdsV2(http);
            if (!string.IsNullOrWhiteSpace(aws)) return aws;

            var azure = TryAzureIMDS(http);
            if (!string.IsNullOrWhiteSpace(azure)) return azure;

            var gce = TryGceMetadata(http);
            if (!string.IsNullOrWhiteSpace(gce)) return gce;

            Log.Warning("PublicIp mode requested but no cloud metadata service responded");
            return null;
        }
        finally
        {
            if (providedClient == null) http.Dispose();
        }
    }

    private static string TryAwsImdsV2(HttpClient http)
    {
        try
        {
            using var tokenReq = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token");
            tokenReq.Headers.Add("X-aws-ec2-metadata-token-ttl-seconds", "60");
            using var tokenResp = http.Send(tokenReq);

            if (!tokenResp.IsSuccessStatusCode) return null;

            var token = tokenResp.Content.ReadAsStringAsync().Result;

            using var ipReq = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/latest/meta-data/public-ipv4");
            ipReq.Headers.Add("X-aws-ec2-metadata-token", token);
            using var ipResp = http.Send(ipReq);

            return ipResp.IsSuccessStatusCode ? ipResp.Content.ReadAsStringAsync().Result.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TryAzureIMDS(HttpClient http)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "http://169.254.169.254/metadata/instance/network/interface/0/ipv4/ipAddress/0/publicIpAddress?api-version=2021-02-01&format=text");
            req.Headers.Add("Metadata", "true");
            using var resp = http.Send(req);

            return resp.IsSuccessStatusCode ? resp.Content.ReadAsStringAsync().Result.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TryGceMetadata(HttpClient http)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "http://metadata.google.internal/computeMetadata/v1/instance/network-interfaces/0/access-configs/0/external-ip");
            req.Headers.Add("Metadata-Flavor", "Google");
            using var resp = http.Send(req);

            return resp.IsSuccessStatusCode ? resp.Content.ReadAsStringAsync().Result.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
