using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public static class DockerRegistryAuthHelper
{
    private static readonly Regex WwwAuthenticateParameterRegex = new(@"(?<key>[A-Za-z][A-Za-z0-9_-]*)=""(?<value>[^""]*)""", RegexOptions.Compiled);

    public static bool IsContainerRegistryFeed(ExternalFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed?.FeedType))
            return false;

        return feed.FeedType.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
               feed.FeedType.Contains("Container Registry", StringComparison.OrdinalIgnoreCase) ||
               feed.FeedType.Contains("Elastic Container Registry", StringComparison.OrdinalIgnoreCase) ||
               feed.FeedType.Contains("OCI Registry", StringComparison.OrdinalIgnoreCase) ||
               feed.FeedType.Contains("ECR", StringComparison.OrdinalIgnoreCase) ||
               feed.FeedType.Contains("ACR", StringComparison.OrdinalIgnoreCase) ||
               feed.FeedType.Contains("GCR", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasCredentials(ExternalFeed feed) =>
        !string.IsNullOrWhiteSpace(feed?.Username) && !string.IsNullOrWhiteSpace(feed.Password);

    public static string ToBasicAuthValue(string username, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    public static Dictionary<string, string> ParseChallengeParameters(string parameter)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(parameter))
            return values;

        foreach (Match match in WwwAuthenticateParameterRegex.Matches(parameter))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value;

            if (!string.IsNullOrWhiteSpace(key))
                values[key] = value;
        }

        return values;
    }

    public static bool TryBuildDockerTokenEndpoint(HttpResponseMessage response, out Uri tokenEndpoint)
    {
        tokenEndpoint = null;

        var bearerChallenge = response.Headers.WwwAuthenticate
            .FirstOrDefault(x => x.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (bearerChallenge == null)
            return false;

        var parameters = ParseChallengeParameters(bearerChallenge.Parameter);
        if (!parameters.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
            return false;

        if (!Uri.TryCreate(realm, UriKind.Absolute, out var realmUri))
            return false;

        var builder = new UriBuilder(realmUri);
        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (parameters.TryGetValue("service", out var service) && !string.IsNullOrWhiteSpace(service))
            queryParts.Add($"service={Uri.EscapeDataString(service)}");

        if (parameters.TryGetValue("scope", out var scope) && !string.IsNullOrWhiteSpace(scope))
            queryParts.Add($"scope={Uri.EscapeDataString(scope)}");

        builder.Query = string.Join("&", queryParts);
        tokenEndpoint = builder.Uri;
        return true;
    }

    public static bool TryBuildDockerTokenEndpoint(HttpResponseMessage response, string scopeOverride, out Uri tokenEndpoint)
    {
        tokenEndpoint = null;

        var bearerChallenge = response.Headers.WwwAuthenticate
            .FirstOrDefault(x => x.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (bearerChallenge == null)
            return false;

        var parameters = ParseChallengeParameters(bearerChallenge.Parameter);
        if (!parameters.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
            return false;

        if (!Uri.TryCreate(realm, UriKind.Absolute, out var realmUri))
            return false;

        var builder = new UriBuilder(realmUri);
        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (parameters.TryGetValue("service", out var service) && !string.IsNullOrWhiteSpace(service))
            queryParts.Add($"service={Uri.EscapeDataString(service)}");

        if (!string.IsNullOrWhiteSpace(scopeOverride))
            queryParts.Add($"scope={Uri.EscapeDataString(scopeOverride)}");
        else if (parameters.TryGetValue("scope", out var scope) && !string.IsNullOrWhiteSpace(scope))
            queryParts.Add($"scope={Uri.EscapeDataString(scope)}");

        builder.Query = string.Join("&", queryParts);
        tokenEndpoint = builder.Uri;
        return true;
    }

    public static string ExtractBearerToken(string tokenJson)
    {
        if (string.IsNullOrWhiteSpace(tokenJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(tokenJson);
            var root = document.RootElement;

            if (root.TryGetProperty("token", out var tokenElement) &&
                tokenElement.ValueKind == JsonValueKind.String)
                return tokenElement.GetString();

            if (root.TryGetProperty("access_token", out var accessTokenElement) &&
                accessTokenElement.ValueKind == JsonValueKind.String)
                return accessTokenElement.GetString();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<(bool Success, string Token, string FailureMessage)> RequestBearerTokenAsync(
        ISquidHttpClientFactory httpClientFactory, Uri tokenEndpoint, string username, string password, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(timeout: TimeSpan.FromSeconds(30));

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenEndpoint);
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", ToBasicAuthValue(username, password));

        using var tokenResponse = await client
            .SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (tokenResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (false, null, $"Registry credentials were rejected by token service (HTTP {(int)tokenResponse.StatusCode}).");

        if (!tokenResponse.IsSuccessStatusCode)
            return (false, null, $"Token service responded with HTTP {(int)tokenResponse.StatusCode} {tokenResponse.ReasonPhrase}.");

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var token = ExtractBearerToken(tokenJson);

        return string.IsNullOrWhiteSpace(token)
            ? (false, null, "Token service response did not contain a bearer token.")
            : (true, token, null);
    }
}
