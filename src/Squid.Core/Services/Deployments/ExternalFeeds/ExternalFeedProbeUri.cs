namespace Squid.Core.Services.Deployments.ExternalFeeds;

public static class ExternalFeedProbeUri
{
    public static bool TryNormalize(string rawFeedUri, out Uri normalizedBaseUri)
    {
        normalizedBaseUri = null;

        if (string.IsNullOrWhiteSpace(rawFeedUri))
            return false;

        if (!Uri.TryCreate(rawFeedUri, UriKind.Absolute, out var parsed))
            return false;

        if (!IsHttpOrHttps(parsed))
            return false;

        normalizedBaseUri = Normalize(parsed);
        return true;
    }

    public static Uri AppendPath(Uri normalizedBaseUri, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return normalizedBaseUri;

        return new Uri(normalizedBaseUri, relativePath.TrimStart('/'));
    }

    public static Uri EnsureEndsWithPathSegment(Uri normalizedBaseUri, string segment)
    {
        if (normalizedBaseUri == null || string.IsNullOrWhiteSpace(segment))
            return normalizedBaseUri;

        return EndsWithPathSegment(normalizedBaseUri, segment)
            ? normalizedBaseUri
            : AppendPath(normalizedBaseUri, segment);
    }

    public static bool EndsWithPathSegment(Uri normalizedBaseUri, string segment)
    {
        if (normalizedBaseUri == null || string.IsNullOrWhiteSpace(segment))
            return false;

        var normalizedSegment = $"/{segment.Trim().Trim('/')}";
        var path = normalizedBaseUri.AbsolutePath.TrimEnd('/');

        return path.EndsWith(normalizedSegment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttpOrHttps(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static Uri Normalize(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            return uri;

        if (LooksLikeFilePath(uri.AbsolutePath))
            return uri;

        var builder = new UriBuilder(uri) { Path = $"{uri.AbsolutePath}/" };
        return builder.Uri;
    }

    private static bool LooksLikeFilePath(string absolutePath)
    {
        var lastSlash = absolutePath.LastIndexOf('/');
        var segment = lastSlash >= 0 ? absolutePath[(lastSlash + 1)..] : absolutePath;

        return segment.Contains('.', StringComparison.Ordinal);
    }
}
