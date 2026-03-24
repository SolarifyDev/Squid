using System.Text.Json;
using Serilog;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiEndpointVariableContributor : IEndpointVariableContributor
{
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public KubernetesApiEndpointVariableContributor(IExternalFeedDataProvider externalFeedDataProvider = null)
    {
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public EndpointResourceReferences ParseResourceReferences(string endpointJson)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(endpointJson);

        if (endpoint == null) return new EndpointResourceReferences();

        return new EndpointResourceReferences
        {
            References = endpoint.ResourceReferences ?? new()
        };
    }

    public List<VariableDto> ContributeVariables(EndpointContext context)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(context.EndpointJson);

        if (endpoint == null) return new List<VariableDto>();

        var accountData = context.GetAccountData();
        var accountTypeStr = accountData?.AuthenticationAccountType.ToString() ?? "Token";

        var vars = new List<VariableDto>
        {
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.ClusterUrl, endpoint.ClusterUrl ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Account.AccountType, accountTypeStr),
            EndpointVariableFactory.Make(SpecialVariables.Account.CredentialsJson, accountData?.CredentialsJson ?? string.Empty, isSensitive: true),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.SkipTlsVerification, endpoint.SkipTlsVerification ?? KubernetesBooleanValues.False),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.Namespace, endpoint.Namespace ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.ClusterCertificate, ResolveClusterCertificate(context)),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.SuppressEnvironmentLogging, KubernetesBooleanValues.False),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.OutputKubectlVersion, KubernetesBooleanValues.True),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.PrintEvaluatedVariables, KubernetesBooleanValues.True)
        };

        vars.AddRange(AccountVariableExpander.Expand(accountData));

        return vars;
    }

    private static string ResolveClusterCertificate(EndpointContext context)
    {
        return context.GetCertificate(EndpointResourceType.ClusterCertificate) ?? string.Empty;
    }

    public async Task<List<VariableDto>> ContributeAdditionalVariablesAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
    {
        var containerImage = await BuildContainerImageAsync(processSnapshot, release, ct).ConfigureAwait(false);
        return new List<VariableDto> { EndpointVariableFactory.Make(SpecialVariables.Kubernetes.ContainerImage, containerImage) };
    }

    private async Task<string> BuildContainerImageAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
    {
        var fallback = release?.Version ?? string.Empty;

        try
        {
            if (_externalFeedDataProvider == null)
                return fallback;

            var (feedId, packageId) = FindFirstContainerPackage(processSnapshot);

            if (feedId == null || string.IsNullOrEmpty(packageId))
                return fallback;

            var feed = await _externalFeedDataProvider
                .GetFeedByIdAsync(feedId.Value, ct).ConfigureAwait(false);

            if (feed == null)
                return fallback;

            var feedUri = ResolveFeedUri(feed);
            return $"{feedUri}/{packageId}:{release?.Version ?? string.Empty}";
        }
        catch
        {
            return fallback;
        }
    }

    public static (int? FeedId, string PackageId) FindFirstContainerPackage(DeploymentProcessSnapshotDto processSnapshot)
    {
        var actions = processSnapshot?.Data?.StepSnapshots?.SelectMany(s => s.ActionSnapshots);

        if (actions == null)
            return (null, null);

        foreach (var action in actions)
        {
            if (action.Properties == null)
                continue;

            if (!action.Properties.TryGetValue(KubernetesProperties.Containers, out var containersJson))
                continue;

            if (string.IsNullOrWhiteSpace(containersJson))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(containersJson);

                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var container in doc.RootElement.EnumerateArray())
                {
                    if (!container.TryGetProperty(KubernetesContainerPayloadProperties.PackageId, out var pkgProp))
                        continue;

                    var packageId = pkgProp.GetString();

                    if (string.IsNullOrEmpty(packageId))
                        continue;

                    if (!container.TryGetProperty(KubernetesContainerPayloadProperties.FeedId, out var feedProp))
                        continue;

                    int? feedId = feedProp.ValueKind == JsonValueKind.Number
                        ? feedProp.GetInt32()
                        : int.TryParse(feedProp.GetString(), out var parsed) ? parsed : null;

                    if (feedId.HasValue)
                        return (feedId, packageId);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse container package from action");
            }
        }

        return (null, null);
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
