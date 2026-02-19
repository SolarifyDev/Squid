using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesEndpointVariableContributor : IEndpointVariableContributor
{
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public KubernetesEndpointVariableContributor(IExternalFeedDataProvider externalFeedDataProvider = null)
    {
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public bool CanHandle(string communicationStyle)
        => string.Equals(communicationStyle, "Kubernetes", StringComparison.OrdinalIgnoreCase);

    public int? ParseAccountId(string endpointJson)
    {
        var endpoint = Deserialize(endpointJson);
        if (endpoint == null) return null;
        return int.TryParse(endpoint.AccountId, out var id) ? id : null;
    }

    public List<VariableDto> ContributeVariables(string endpointJson, DeploymentAccount account)
    {
        var endpoint = Deserialize(endpointJson);
        if (endpoint == null) return new List<VariableDto>();

        var accountType = account?.AccountType.ToString() ?? "Token";

        return new List<VariableDto>
        {
            MakeVariable("Squid.Action.Kubernetes.ClusterUrl", endpoint.ClusterUrl ?? string.Empty),
            MakeVariable("Squid.Account.AccountType", accountType),
            MakeVariable("Squid.Account.Token", account?.Token ?? string.Empty, isSensitive: true),
            MakeVariable("Squid.Account.Username", account?.Username ?? string.Empty),
            MakeVariable("Squid.Account.Password", account?.Password ?? string.Empty, isSensitive: true),
            MakeVariable("Squid.Account.ClientCertificateData", account?.ClientCertificateData ?? string.Empty, isSensitive: true),
            MakeVariable("Squid.Account.ClientCertificateKeyData", account?.ClientCertificateKeyData ?? string.Empty, isSensitive: true),
            MakeVariable("Squid.Account.AccessKey", account?.AccessKey ?? string.Empty),
            MakeVariable("Squid.Account.SecretKey", account?.SecretKey ?? string.Empty, isSensitive: true),
            MakeVariable("Squid.Action.Kubernetes.SkipTlsVerification", endpoint.SkipTlsVerification ?? "False"),
            MakeVariable("Squid.Action.Kubernetes.Namespace", endpoint.Namespace ?? "default"),
            MakeVariable("Squid.Action.Kubernetes.ClusterCertificate", endpoint.ClusterCertificate ?? string.Empty),
            MakeVariable("Squid.Action.Script.SuppressEnvironmentLogging", "False"),
            MakeVariable("Squid.Action.Kubernetes.OutputKubectlVersion", "True"),
            MakeVariable("SquidPrintEvaluatedVariables", "True")
        };
    }

    public async Task<List<VariableDto>> ContributeAdditionalVariablesAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
    {
        var containerImage = await BuildContainerImageAsync(processSnapshot, release, ct).ConfigureAwait(false);
        return new List<VariableDto> { MakeVariable("ContainerImage", containerImage) };
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

    private static KubernetesEndpointDto Deserialize(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<KubernetesEndpointDto>(endpointJson);
        }
        catch
        {
            return null;
        }
    }

    private static VariableDto MakeVariable(string name, string value, bool isSensitive = false) => new()
    {
        Name = name,
        Value = value,
        Description = string.Empty,
        Type = Message.Enums.VariableType.String,
        IsSensitive = isSensitive,
        LastModifiedOn = DateTimeOffset.UtcNow,
        LastModifiedBy = "System"
    };
}
