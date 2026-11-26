using System.Text;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;

internal static class FeedAuthHeaders
{
    public static Dictionary<string, string> Build(ExternalFeed feed)
    {
        var headers = new Dictionary<string, string>();
        // Deliberately the raw substring check, not the classified kind: a name such as "GitHub Helm" classifies
        // as Helm (index lookup) but has always sent GitHub headers and token auth, and must keep doing so.
        var isGitHub =PackageFeedKinds.Mentions(feed.FeedType, "GitHub");

        if (isGitHub)
        {
            headers["Accept"] = "application/vnd.github+json";
            headers["X-GitHub-Api-Version"] = "2022-11-28";
        }

        if (!string.IsNullOrWhiteSpace(feed.Username) && !string.IsNullOrWhiteSpace(feed.Password))
            headers["Authorization"] = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{feed.Username}:{feed.Password}"))}";
        else if (!string.IsNullOrWhiteSpace(feed.Password))
            headers["Authorization"] = isGitHub ? $"token {feed.Password}" : $"Bearer {feed.Password}";

        return headers;
    }
}
