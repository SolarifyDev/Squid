using System.Net;
using System.Text.Json;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// GET-only client for one Octopus server. Only paths <see cref="OctopusRestPathPolicy"/> allows are requested.
/// Redirects are followed by hand, only within the same origin and only to the same resource, so the API key header
/// is never sent to another host or another endpoint. Bodies are bounded, never logged, and every failure is an
/// <see cref="OctopusRestException"/> with a fixed message.
/// </summary>
public sealed class OctopusRestClient : IDisposable
{
    public const string ApiKeyHeader = "X-Octopus-ApiKey";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly OctopusRestOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OctopusRestClient(string serverUrl, string apiKey, OctopusRestOptions options, HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new OctopusRestException(OctopusRestErrorKind.Unauthorized);

        _options = options ?? throw new ArgumentNullException(nameof(options));
        BaseUri = OctopusRestEndpointGuard.ParseServerUri(serverUrl, options);
        _apiKey = apiKey.Trim();
        _delay = delay ?? Task.Delay;
        _http = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler))) { Timeout = options.RequestTimeout };
    }

    /// <summary>The server address; a same-host redirect from http to https upgrades it, so later requests go to https directly.</summary>
    public Uri BaseUri { get; private set; }

    /// <summary>Response bytes read by this client so far; the history fetch caps its total on it.</summary>
    public long BytesRead { get; private set; }

    /// <summary>The key as <see cref="OctopusRestKeyBinding.Fingerprint"/> sees it; set by the factory, so the key never has to leave the client.</summary>
    public string KeyFingerprint { get; init; }

    /// <summary>The last path requested (relative, with its query); names the request a chunk was waiting on when it was abandoned.</summary>
    public string LastRequestedPath { get; private set; }

    /// <summary>The most bytes a paged list may read in total (<see cref="BytesRead"/> included); the history fetch sets what is left of its budget.</summary>
    public long ByteBudget { get; set; } = long.MaxValue;

    public async Task<OctopusRestRootDto> GetRootAsync(CancellationToken ct)
    {
        var root = await GetAsync<OctopusRestRootDto>("api", ct).ConfigureAwait(false);

        if (root == null || !root.IsOctopus)
            throw new OctopusRestException(OctopusRestErrorKind.NotOctopus);

        return root;
    }

    /// <summary>The Octopus user the API key belongs to.</summary>
    public async Task<OctopusRestUserDto> GetCurrentUserAsync(CancellationToken ct)
    {
        var user = await GetAsync<OctopusRestUserDto>("api/users/me", ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(user?.Id))
            throw new OctopusRestException(OctopusRestErrorKind.NotOctopus);

        return user;
    }

    public async Task<T> GetAsync<T>(string relativePath, CancellationToken ct)
    {
        var body = await GetBytesAsync(relativePath, ct).ConfigureAwait(false);

        return Deserialize<T>(body);
    }

    public Task<OctopusRestPage<T>> GetPageAsync<T>(string relativePath, int skip, CancellationToken ct) => GetAsync<OctopusRestPage<T>>(PagePath(relativePath, skip), ct);

    // Bounded against a hostile or broken server: it ends on an empty page or the reported total, on a page that
    // repeats the previous one byte for byte (skip ignored), and fails past the item cap or the byte budget.
    public async Task<List<T>> GetAllPagesAsync<T>(string relativePath, CancellationToken ct)
    {
        var items = new List<T>();
        byte[] previous = null;

        while (true)
        {
            var body = await GetBytesAsync(PagePath(relativePath, items.Count), ct).ConfigureAwait(false);

            if (previous != null && body.AsSpan().SequenceEqual(previous)) return items;

            var page = Deserialize<OctopusRestPage<T>>(body);
            var pageItems = page?.Items ?? [];

            items.AddRange(pageItems);
            EnsureWithinListLimits(items.Count);

            if (IsLastPage(pageItems.Count, items.Count, page?.TotalResults ?? 0)) return items;

            previous = body;
        }
    }

    /// <summary>The reported total is authoritative: some Octopus endpoints ignore <c>take</c>, so a short page is not the end.</summary>
    public static bool IsLastPage(int pageCount, int fetchedSoFar, int totalResults) => pageCount == 0 || fetchedSoFar >= totalResults;

    public static string WithQuery(string relativePath, string query) => relativePath + (relativePath.Contains('?') ? "&" : "?") + query;

    public void Dispose() => _http.Dispose();

    private string PagePath(string relativePath, int skip) => WithQuery(relativePath, $"skip={skip}&take={_options.PageSize}");

    private void EnsureWithinListLimits(int itemCount)
    {
        if (itemCount > _options.MaxListItems || BytesRead > ByteBudget)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge);
    }

    private static T Deserialize<T>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new OctopusRestException(OctopusRestErrorKind.NotOctopus);
        }
    }

    // The path is checked before it is combined with the base address, which would collapse "." and ".." segments.
    private async Task<byte[]> GetBytesAsync(string relativePath, CancellationToken ct)
    {
        OctopusRestPathPolicy.EnsureAllowed(relativePath);

        LastRequestedPath = relativePath.TrimStart('/');

        for (var attempt = 0; ; attempt++)
        {
            var (body, retryDelay) = await AttemptAsync(new Uri(BaseUri, LastRequestedPath), attempt, ct).ConfigureAwait(false);

            if (body != null) return body;

            await _delay(retryDelay, ct).ConfigureAwait(false);
        }
    }

    // One request under its own timeout, which covers the body as well as the headers. Returns the body, or the
    // delay before the next attempt when the failure is transient (a 429/5xx, or a transport failure at any point).
    private async Task<(byte[] Body, TimeSpan RetryDelay)> AttemptAsync(Uri uri, int attempt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        timeout.CancelAfter(_options.RequestTimeout);

        try
        {
            return await SendAndReadAsync(uri, attempt, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is OctopusRestException rest)
        {
            throw new OctopusRestException(rest.Kind);
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            if (attempt >= _options.MaxRetries)
                throw new OctopusRestException(OctopusRestErrorKind.Unreachable);

            return (null, RetryDelay(null, attempt));
        }
    }

    private async Task<(byte[] Body, TimeSpan RetryDelay)> SendAndReadAsync(Uri uri, int attempt, CancellationToken ct)
    {
        using var response = await SendFollowingRedirectsAsync(uri, ct).ConfigureAwait(false);

        if (IsRetryable(response) && attempt < _options.MaxRetries) return (null, RetryDelay(response, attempt));

        EnsureSuccess(response, LastRequestedPath);

        return (await ReadBoundedAsync(response, ct).ConfigureAwait(false), TimeSpan.Zero);
    }

    // Refused, reset or timed out, before the headers or part way through the body; the caller's own cancellation is not one.
    private static bool IsTransportFailure(Exception ex, CancellationToken ct) => ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri uri, CancellationToken ct)
    {
        for (var hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            var response = await SendOnceAsync(uri, ct).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode)) return response;

            var target = RedirectTarget(uri, response);

            EnsureSameResource(target);

            response.Dispose();
            UpgradeBaseUriIfSecured(uri, target);
            uri = target;
        }

        throw new OctopusRestException(OctopusRestErrorKind.Unreachable);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        request.Headers.Add(ApiKeyHeader, _apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static Uri RedirectTarget(Uri current, HttpResponseMessage response)
    {
        var location = response.Headers.Location;

        if (location == null)
            throw new OctopusRestException(OctopusRestErrorKind.Unreachable);

        var target = location.IsAbsoluteUri ? location : new Uri(current, location);

        if (!IsSameOrigin(current, target) && !IsHttpsUpgrade(current, target))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);

        return target;
    }

    // A redirect may move the API under a prefix or onto https, never to another endpoint: the requested path must end the
    // target's, and the target's own query must pass the same allowlist as a request this client makes.
    private void EnsureSameResource(Uri target)
    {
        var requested = OctopusRestPathPolicy.PathOf(LastRequestedPath);

        if (!target.AbsolutePath.EndsWith("/" + requested, StringComparison.OrdinalIgnoreCase) || !OctopusRestPathPolicy.IsAllowed(requested + target.Query))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);
    }

    private static bool IsSameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && IsSameHost(a, b) && a.Port == b.Port;

    // http://host → https://host (same port, or both on their default port) is the server insisting on TLS, not another origin.
    private static bool IsHttpsUpgrade(Uri from, Uri to)
        => from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps && IsSameHost(from, to) && (from.Port == to.Port || (from.IsDefaultPort && to.IsDefaultPort));

    private static bool IsSameHost(Uri a, Uri b) => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

    private void UpgradeBaseUriIfSecured(Uri current, Uri target)
    {
        if (!IsHttpsUpgrade(current, target) || !IsSameOrigin(BaseUri, current)) return;

        BaseUri = new UriBuilder(BaseUri) { Scheme = Uri.UriSchemeHttps, Port = target.Port }.Uri;
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static bool IsRetryable(HttpResponseMessage response) => response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var requested = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        var backoff = TimeSpan.FromTicks(_options.RetryBaseDelay.Ticks * (1L << Math.Min(attempt, 10)));
        var delay = requested is { } honoured && honoured > TimeSpan.Zero ? honoured : backoff;

        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string relativePath)
    {
        if (IsRetryable(response))
            throw new OctopusRestException(OctopusRestErrorKind.Unreachable);

        var kind = FailureKind(response, OctopusRestPathPolicy.IsIdentityPath(relativePath));

        if (kind != null)
            throw new OctopusRestException(kind.Value);
    }

    // 401 always means the key; 403 means the key only on its own identity, elsewhere that one item is refused (as are 400/409/422).
    private static OctopusRestErrorKind? FailureKind(HttpResponseMessage response, bool identityPath)
        => response.StatusCode switch
        {
            _ when response.IsSuccessStatusCode => null,
            HttpStatusCode.Unauthorized => OctopusRestErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => identityPath ? OctopusRestErrorKind.Unauthorized : OctopusRestErrorKind.Rejected,
            HttpStatusCode.NotFound => OctopusRestErrorKind.NotFound,
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity when !identityPath => OctopusRestErrorKind.Rejected,
            _ => OctopusRestErrorKind.Unreachable
        };

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge);

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        await CopyBoundedAsync(body, buffer, ct).ConfigureAwait(false);

        BytesRead += buffer.Length;

        return buffer.ToArray();
    }

    private async Task CopyBoundedAsync(Stream source, MemoryStream destination, CancellationToken ct)
    {
        var chunk = new byte[81920];

        while (true)
        {
            var read = await source.ReadAsync(chunk, ct).ConfigureAwait(false);

            if (read == 0) return;

            if (destination.Length + read > _options.MaxResponseBytes)
                throw new OctopusRestException(OctopusRestErrorKind.TooLarge);

            destination.Write(chunk, 0, read);
        }
    }
}
