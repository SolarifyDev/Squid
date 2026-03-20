using System.Net;
using System.Net.Http.Headers;
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

        if (!DockerRegistryAuthHelper.TryBuildDockerTokenEndpoint(challengeResponse, out var tokenEndpoint))
            return Fail("Registry authentication challenge is invalid.");

        var tokenRequest = await DockerRegistryAuthHelper.RequestBearerTokenAsync(
            httpClientFactory, tokenEndpoint, feed.Username, feed.Password, cancellationToken).ConfigureAwait(false);

        if (!tokenRequest.Success)
            return Fail(tokenRequest.FailureMessage);

        var authClient = httpClientFactory.CreateClient(timeout: TestTimeout);
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

    private static bool ShouldUseDockerBearerTokenFlow(ExternalFeed feed, HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized && DockerRegistryAuthHelper.HasCredentials(feed) && DockerRegistryAuthHelper.IsContainerRegistryFeed(feed) &&
        response.Headers.WwwAuthenticate.Any(x => x.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));

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

        var encoded = DockerRegistryAuthHelper.ToBasicAuthValue(username, password);

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }

    private static TestExternalFeedResponseData Ok(string message) => new() { Success = true, Message = message };

    private static TestExternalFeedResponseData Fail(string message) => new() { Success = false, Message = message };
}
