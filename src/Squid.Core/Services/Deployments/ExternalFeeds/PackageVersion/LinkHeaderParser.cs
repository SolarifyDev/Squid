namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

/// <summary>
/// RFC 5988 Link-header parser, focused on the <c>rel="next"</c> form used by
/// paginated registry / API endpoints (Docker registry v2 tags/list, GitHub
/// Releases, Helm OCI manifest list).
///
/// <para><b>Format</b>: a single Link header may contain multiple links, comma-
/// separated, each <c>&lt;url&gt;; rel="next"</c>. Whitespace between segments is
/// allowed. Relative URLs in the header are resolved against the request URL
/// per RFC 3986. Absolute URLs are used verbatim.</para>
///
/// <para><b>Why a shared helper</b>: both Docker and GitHub use Link headers but
/// each strategy's auth flow is bespoke (Docker bearer-token retry, GitHub PAT).
/// Extracting only the *parsing* into a pure helper avoids duplicating the regex
/// + URL-resolution logic across strategies; each strategy's pagination loop
/// stays inline so its auth context is obvious to a reader.</para>
/// </summary>
internal static class LinkHeaderParser
{
    /// <summary>
    /// Tries to extract the next-page URI from an HTTP response's Link header.
    /// </summary>
    /// <param name="response">The HTTP response just received.</param>
    /// <param name="requestUri">
    /// The URI of the request that produced <paramref name="response"/>. Used to
    /// resolve relative Link header URLs (Docker registry returns relative paths
    /// like <c>/v2/repo/tags/list?n=100&amp;last=tag</c>; GitHub returns absolute).
    /// </param>
    /// <param name="nextUri">The resolved absolute next-page URI, when found.</param>
    /// <returns>True if a <c>rel="next"</c> link was found; false otherwise.</returns>
    public static bool TryGetNextUri(HttpResponseMessage response, Uri requestUri, out Uri nextUri)
    {
        nextUri = null;

        if (response == null || requestUri == null) return false;

        if (!response.Headers.TryGetValues("Link", out var values)) return false;

        // Multiple Link headers may concatenate; join then split on `,` (commas
        // inside angle brackets aren't permitted by RFC, so a plain split is safe).
        var combined = string.Join(",", values);

        foreach (var segment in combined.Split(','))
        {
            if (!TryParseSegment(segment, out var url, out var rel)) continue;

            if (!string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase)) continue;

            return TryResolveAbsolute(url, requestUri, out nextUri);
        }

        return false;
    }

    /// <summary>
    /// Pure parser for a single Link header segment. Exposed for unit testing
    /// edge cases (whitespace variants, missing rel, malformed urls) without
    /// having to construct an HttpResponseMessage.
    /// </summary>
    internal static bool TryParseSegment(string segment, out string url, out string rel)
    {
        url = null;
        rel = null;

        if (string.IsNullOrWhiteSpace(segment)) return false;

        var trimmed = segment.Trim();

        var openBracket = trimmed.IndexOf('<');
        var closeBracket = trimmed.IndexOf('>');

        if (openBracket < 0 || closeBracket <= openBracket) return false;

        url = trimmed.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();

        if (string.IsNullOrWhiteSpace(url)) return false;

        rel = ExtractRel(trimmed[(closeBracket + 1)..]);

        return true;
    }

    private static string ExtractRel(string parameters)
    {
        // Find `rel=` parameter; value may be quoted or bare.
        const string relMarker = "rel=";

        var idx = parameters.IndexOf(relMarker, StringComparison.OrdinalIgnoreCase);

        if (idx < 0) return null;

        var valueStart = idx + relMarker.Length;

        if (valueStart >= parameters.Length) return null;

        var rest = parameters[valueStart..].TrimStart();

        if (rest.Length == 0) return null;

        if (rest[0] == '"')
        {
            var endQuote = rest.IndexOf('"', 1);
            return endQuote > 0 ? rest.Substring(1, endQuote - 1) : null;
        }

        // Bare token — until `;` or `,` or whitespace
        var end = rest.IndexOfAny([';', ',', ' ', '\t']);

        return end > 0 ? rest[..end] : rest;
    }

    private static bool TryResolveAbsolute(string url, Uri requestUri, out Uri absolute)
    {
        // The (baseUri, relativeUri) overload handles BOTH cases:
        //   • Absolute URL string → result = the absolute URI (baseUri ignored)
        //   • Relative URL string → result = baseUri.Scheme://baseUri.Authority/<relative>
        // Verifying IsAbsoluteUri afterwards guards against the .NET quirk where
        // some malformed inputs produce a relative result without scheme/host —
        // observed e.g. with `/v2/...` strings on certain frameworks. Returning
        // false in that case lets the caller treat the link as missing.
        if (!Uri.TryCreate(requestUri, url, out absolute)) return false;

        if (!absolute.IsAbsoluteUri || string.IsNullOrEmpty(absolute.Host))
        {
            absolute = null;
            return false;
        }

        return true;
    }
}
