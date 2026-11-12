using System.Net;
using System.Text.Json;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// GET-only client for one Octopus server. Redirects are followed by hand and only within the same origin, so
/// the API key header is never sent to another host. Bodies are bounded, never logged, and every failure is an
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

    public Uri BaseUri { get; }

    /// <summary>Response bytes read by this client so far; the history fetch caps its total on it.</summary>
    public long BytesRead { get; private set; }

    public async Task<OctopusRestRootDto> GetRootAsync(CancellationToken ct)
    {
        var root = await GetAsync<OctopusRestRootDto>("api", ct).ConfigureAwait(false);

        if (root == null || !root.IsOctopus)
            throw new OctopusRestException(OctopusRestErrorKind.NotOctopus);

        return root;
    }

    public async Task<T> GetAsync<T>(string relativePath, CancellationToken ct)
    {
        var body = await GetBytesAsync(relativePath, ct).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new OctopusRestException(OctopusRestErrorKind.NotOctopus);
        }
    }

    public Task<OctopusRestPage<T>> GetPageAsync<T>(string relativePath, int skip, CancellationToken ct)
        => GetAsync<OctopusRestPage<T>>(WithQuery(relativePath, $"skip={skip}&take={_options.PageSize}"), ct);

    public async Task<List<T>> GetAllPagesAsync<T>(string relativePath, CancellationToken ct)
    {
        var items = new List<T>();

        while (true)
        {
            var page = await GetPageAsync<T>(relativePath, items.Count, ct).ConfigureAwait(false);
            var pageItems = page?.Items ?? [];

            items.AddRange(pageItems);

            if (IsLastPage(pageItems.Count, items.Count, page?.TotalResults ?? 0)) return items;
        }
    }

    public bool IsLastPage(int pageCount, int fetchedSoFar, int totalResults) => pageCount < _options.PageSize || fetchedSoFar >= totalResults;

    public static string WithQuery(string relativePath, string query) => relativePath + (relativePath.Contains('?') ? "&" : "?") + query;

    public void Dispose() => _http.Dispose();

    private async Task<byte[]> GetBytesAsync(string relativePath, CancellationToken ct)
    {
        var uri = new Uri(BaseUri, relativePath.TrimStart('/'));

        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendOrNullAsync(uri, ct).ConfigureAwait(false);

            if (IsRetryable(response) && attempt < _options.MaxRetries)
            {
                await _delay(RetryDelay(response, attempt), ct).ConfigureAwait(false);
                continue;
            }

            EnsureSuccess(response);

            return await ReadBoundedAsync(response, ct).ConfigureAwait(false);
        }
    }

    // A transport failure (refused, reset, timed out) is reported as a null response so it is retried like a 5xx.
    private async Task<HttpResponseMessage> SendOrNullAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            return await SendFollowingRedirectsAsync(uri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is OctopusRestException rest)
        {
            throw new OctopusRestException(rest.Kind);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri uri, CancellationToken ct)
    {
        for (var hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            var response = await SendOnceAsync(uri, ct).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode)) return response;

            var target = RedirectTarget(uri, response);

            response.Dispose();
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

        if (!IsSameOrigin(current, target))
            throw new OctopusRestException(OctopusRestErrorKind.Denied);

        return target;
    }

    private static bool IsSameOrigin(Uri a, Uri b)
        => a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static bool IsRetryable(HttpResponseMessage response) => response == null || response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var requested = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        var backoff = TimeSpan.FromTicks(_options.RetryBaseDelay.Ticks * (1L << Math.Min(attempt, 10)));
        var delay = requested is { } honoured && honoured > TimeSpan.Zero ? honoured : backoff;

        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response == null || IsRetryable(response))
            throw new OctopusRestException(OctopusRestErrorKind.Unreachable);

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => OctopusRestErrorKind.Unauthorized,
            HttpStatusCode.NotFound => OctopusRestErrorKind.NotFound,
            _ when response.IsSuccessStatusCode => (OctopusRestErrorKind?)null,
            _ => OctopusRestErrorKind.Unreachable
        };

        if (kind != null)
            throw new OctopusRestException(kind.Value);
    }

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
