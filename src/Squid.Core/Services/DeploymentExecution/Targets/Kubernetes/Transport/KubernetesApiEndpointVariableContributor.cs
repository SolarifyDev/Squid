using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiEndpointVariableContributor : IEndpointVariableContributor
{
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public KubernetesApiEndpointVariableContributor(IExternalFeedDataProvider externalFeedDataProvider = null)
    {
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public int? ParseAccountId(string endpointJson)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(endpointJson);
        if (endpoint == null) return null;
        return int.TryParse(endpoint.AccountId, out var id) ? id : null;
    }

    public List<VariableDto> ContributeVariables(string endpointJson, DeploymentAccount account)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(endpointJson);
        if (endpoint == null) return new List<VariableDto>();

        var accountType = account?.AccountType.ToString() ?? "Token";

        return new List<VariableDto>
        {
            EndpointVariableFactory.Make("Squid.Action.Kubernetes.ClusterUrl", endpoint.ClusterUrl ?? string.Empty),
            EndpointVariableFactory.Make("Squid.Account.AccountType", accountType),
            EndpointVariableFactory.Make("Squid.Account.Token", account?.Token ?? string.Empty, isSensitive: true),
            EndpointVariableFactory.Make("Squid.Account.Username", account?.Username ?? string.Empty),
            EndpointVariableFactory.Make("Squid.Account.Password", account?.Password ?? string.Empty, isSensitive: true),
            EndpointVariableFactory.Make("Squid.Account.ClientCertificateData", account?.ClientCertificateData ?? string.Empty, isSensitive: true),
            EndpointVariableFactory.Make("Squid.Account.ClientCertificateKeyData", account?.ClientCertificateKeyData ?? string.Empty, isSensitive: true),
            EndpointVariableFactory.Make("Squid.Account.AccessKey", account?.AccessKey ?? string.Empty),
            EndpointVariableFactory.Make("Squid.Account.SecretKey", account?.SecretKey ?? string.Empty, isSensitive: true),
            EndpointVariableFactory.Make("Squid.Action.Kubernetes.SkipTlsVerification", endpoint.SkipTlsVerification ?? "False"),
            EndpointVariableFactory.Make("Squid.Action.Kubernetes.Namespace", endpoint.Namespace ?? "default"),
            EndpointVariableFactory.Make("Squid.Action.Kubernetes.ClusterCertificate", endpoint.ClusterCertificate ?? string.Empty),
            EndpointVariableFactory.Make("Squid.Action.Script.SuppressEnvironmentLogging", "False"),
            EndpointVariableFactory.Make("Squid.Action.Kubernetes.OutputKubectlVersion", "True"),
            EndpointVariableFactory.Make("SquidPrintEvaluatedVariables", "True")
        };
    }

    public async Task<List<VariableDto>> ContributeAdditionalVariablesAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
    {
        var containerImage = await BuildContainerImageAsync(processSnapshot, release, ct).ConfigureAwait(false);
        return new List<VariableDto> { EndpointVariableFactory.Make("ContainerImage", containerImage) };
    }

    private async Task<string> BuildContainerImageAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
    {
        var fallback = release?.Version ?? string.Empty;

        try
        {
            var firstAction = processSnapshot?.Data?.StepSnapshots?
                .SelectMany(s => s.ActionSnapshots)
                .FirstOrDefault(a => a.FeedId.HasValue && !string.IsNullOrEmpty(a.PackageId));

            if (firstAction == null || _externalFeedDataProvider == null)
                return fallback;

            var feed = await _externalFeedDataProvider
                .GetFeedByIdAsync(firstAction.FeedId.Value, ct).ConfigureAwait(false);

            if (feed == null)
                return fallback;

            var feedUri = ResolveFeedUri(feed);
            return $"{feedUri}/{firstAction.PackageId ?? string.Empty}:{release?.Version ?? string.Empty}";
        }
        catch
        {
            return fallback;
        }
    }

    public static string ResolveFeedUri(ExternalFeed feed)
    {
        if (!string.IsNullOrEmpty(feed.RegistryPath))
            return feed.RegistryPath;

        var uri = new Uri(feed.FeedUri ?? string.Empty);
        return uri.Port != 443 && uri.Port != -1
            ? $"{uri.Host}:{uri.Port}"
            : uri.Host;
    }
}
