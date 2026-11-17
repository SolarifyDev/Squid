namespace Squid.Core.Services.OctopusImport;

/// <summary>The Octopus server URL as an import session may keep it: what a reopened page needs to resume the read, and nothing that can carry a credential.</summary>
public static class OctopusImportServerUrl
{
    /// <summary>Scheme, host, port and path of an absolute http(s) URL, without a trailing slash; null for anything else.</summary>
    public static string Sanitize(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri) || !IsHttp(uri)) return null;

        return $"{uri.Scheme}://{uri.Authority.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
