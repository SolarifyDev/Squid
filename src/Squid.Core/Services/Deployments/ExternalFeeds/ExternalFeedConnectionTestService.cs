using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;
using Squid.Message.Commands.Deployments.ExternalFeed;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public interface IExternalFeedConnectionTestService : IScopedDependency
{
    Task<TestExternalFeedResponseData> TestAsync(int feedId, CancellationToken cancellationToken);
}

public class ExternalFeedConnectionTestService(
    IExternalFeedDataProvider dataProvider,
    ISquidHttpClientFactory httpClientFactory,
    IEnumerable<IExternalFeedProbeRule<ExternalFeedProbePlan>> probeRules) : IExternalFeedConnectionTestService
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex WwwAuthenticateParameterRegex = new(@"(?<key>[A-Za-z][A-Za-z0-9_-]*)=""(?<value>[^""]*)""", RegexOptions.Compiled);
    
    private readonly IReadOnlyList<IExternalFeedProbeRule<ExternalFeedProbePlan>> _probeRules = probeRules.OrderBy(x => x.Order).ToList();

    public async Task<TestExternalFeedResponseData> TestAsync(int feedId, CancellationToken cancellationToken)
    {
        var feed = await dataProvider.GetFeedByIdAsync(feedId, cancellationToken).ConfigureAwait(false);
        
        var validation = ValidateFeed(feed);
        
        if (validation != null) return validation;

        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var normalizedBaseUri)) return Fail("Feed URL is invalid.");

        var probePlan = ResolveProbePlan(feed, normalizedBaseUri);
        var authHeaders = BuildAuthHeaders(feed.Username, feed.Password);

        return await ProbeAsync(feed, probePlan, authHeaders, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TestExternalFeedResponseData> ProbeAsync(ExternalFeed feed, ExternalFeedProbePlan probePlan, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (probePlan?.ProbeUris == null || probePlan.ProbeUris.Count == 0)
            return Fail("No probe endpoint is configured for this feed type.");

        var client = httpClientFactory.CreateClient(timeout: TestTimeout, headers: headers);

        var timedOut = false;
        string lastFailureMessage = null;

        foreach (var probeUri in probePlan.ProbeUris)
        {
            try
            {
                using var response = await client
                    .GetAsync(probeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                var dockerBearerResult = await TryProbeDockerRegistryWithBearerTokenAsync(
                    feed, probePlan, probeUri, response, cancellationToken).ConfigureAwait(false);

                if (dockerBearerResult != null)
                    return dockerBearerResult;

                if (probePlan.IsReachable(response.StatusCode))
                    return Ok($"Connected successfully (HTTP {(int)response.StatusCode}).");

                lastFailureMessage = $"Server responded with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                lastFailureMessage = "Connection timed out.";
            }
            catch (HttpRequestException ex)
            {
                lastFailureMessage = $"Connection failed: {ex.Message}";
            }
        }

        return timedOut
            ? Fail("Connection timed out.")
            : Fail(lastFailureMessage ?? "Connection failed.");
    }

    private async Task<TestExternalFeedResponseData> TryProbeDockerRegistryWithBearerTokenAsync(
        ExternalFeed feed, ExternalFeedProbePlan probePlan, Uri probeUri, HttpResponseMessage challengeResponse, CancellationToken cancellationToken)
    {
        if (!ShouldUseDockerBearerTokenFlow(feed, challengeResponse))
            return null;

        if (!TryBuildDockerTokenEndpoint(challengeResponse, out var tokenEndpoint))
            return Fail("Registry authentication challenge is invalid.");

        var tokenRequest = await RequestDockerBearerTokenAsync(
            tokenEndpoint, feed.Username, feed.Password, cancellationToken).ConfigureAwait(false);

        if (!tokenRequest.Success)
            return Fail(tokenRequest.FailureMessage);

        using var authClient = httpClientFactory.CreateClient(timeout: TestTimeout);
        using var authenticatedProbeRequest = new HttpRequestMessage(HttpMethod.Get, probeUri);
        authenticatedProbeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenRequest.Token);

        using var authenticatedProbeResponse = await authClient
            .SendAsync(authenticatedProbeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (authenticatedProbeResponse.StatusCode == HttpStatusCode.Unauthorized)
            return Fail("Registry credentials were rejected by registry endpoint (HTTP 401).");

        if (probePlan.IsReachable(authenticatedProbeResponse.StatusCode))
            return Ok($"Connected successfully (HTTP {(int)authenticatedProbeResponse.StatusCode}).");

        return Fail($"Server responded with HTTP {(int)authenticatedProbeResponse.StatusCode} {authenticatedProbeResponse.ReasonPhrase}.");
    }

    private async Task<(bool Success, string Token, string FailureMessage)> RequestDockerBearerTokenAsync(
        Uri tokenEndpoint, string username, string password, CancellationToken cancellationToken)
    {
        using var authClient = httpClientFactory.CreateClient(timeout: TestTimeout);
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenEndpoint);
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", ToBasicAuthValue(username, password));

        using var tokenResponse = await authClient
            .SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (tokenResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (false, null, $"Registry credentials were rejected by token service (HTTP {(int)tokenResponse.StatusCode}).");

        if (!tokenResponse.IsSuccessStatusCode)
            return (false, null, $"Token service responded with HTTP {(int)tokenResponse.StatusCode} {tokenResponse.ReasonPhrase}.");

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var token = ExtractBearerToken(tokenJson);

        return string.IsNullOrWhiteSpace(token)
            ? (false, null, "Token service response did not contain a bearer token.")
            : (true, token, null);
    }

    private static bool ShouldUseDockerBearerTokenFlow(ExternalFeed feed, HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized && HasCredentials(feed) && IsContainerRegistryFeed(feed) &&
        response.Headers.WwwAuthenticate.Any(x => x.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));

    private static bool HasCredentials(ExternalFeed feed) => !string.IsNullOrWhiteSpace(feed?.Username) && !string.IsNullOrWhiteSpace(feed.Password);

    private static bool IsContainerRegistryFeed(ExternalFeed feed)
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

    private static bool TryBuildDockerTokenEndpoint(HttpResponseMessage response, out Uri tokenEndpoint)
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

    private static Dictionary<string, string> ParseChallengeParameters(string parameter)
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

    private static string ExtractBearerToken(string tokenJson)
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

    private ExternalFeedProbePlan ResolveProbePlan(ExternalFeed feed, Uri normalizedBaseUri)
    {
        var rule = _probeRules.FirstOrDefault(x => x.Matches(feed));
        
        return (rule ?? ExternalFeedDefaultProbeRule.Instance).Resolve(feed, normalizedBaseUri);
    }

    private static TestExternalFeedResponseData ValidateFeed(ExternalFeed feed)
    {
        if (feed == null) return Fail("Feed not found.");

        return string.IsNullOrWhiteSpace(feed.FeedUri) ? Fail("Feed URL is not configured.") : null;
    }

    private static Dictionary<string, string> BuildAuthHeaders(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        
        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }

    private static string ToBasicAuthValue(string username, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    private static TestExternalFeedResponseData Ok(string message) => new() { Success = true, Message = message };

    private static TestExternalFeedResponseData Fail(string message) => new() { Success = false, Message = message };
}
