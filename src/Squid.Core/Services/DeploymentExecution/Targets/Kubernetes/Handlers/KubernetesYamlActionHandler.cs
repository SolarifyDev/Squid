using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesYamlActionHandler : IActionHandler
{
    private readonly IEnumerable<IActionYamlGenerator> _yamlGenerators;
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public KubernetesYamlActionHandler(
        IEnumerable<IActionYamlGenerator> yamlGenerators,
        IExternalFeedDataProvider externalFeedDataProvider = null)
    {
        _yamlGenerators = yamlGenerators;
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public DeploymentActionType ActionType => DeploymentActionType.KubernetesDeployContainers;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return DeploymentActionTypeParser.Is(action.ActionType, ActionType)
               && _yamlGenerators.Any(g => g.CanHandle(action));
    }

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var generator = _yamlGenerators.FirstOrDefault(g => g.CanHandle(ctx.Action));

        if (generator == null)
            return null;

        await ResolveContainerImagesAsync(ctx, ct).ConfigureAwait(false);

        var secretYaml = await GenerateFeedSecretAsync(ctx, ct).ConfigureAwait(false);

        var yamlFiles = await generator.GenerateAsync(ctx.Step, ctx.Action, ct).ConfigureAwait(false)
            ?? new Dictionary<string, byte[]>();

        if (secretYaml != null)
            yamlFiles["feedsecrets.yaml"] = Encoding.UTF8.GetBytes(secretYaml);

        return new ActionExecutionResult
        {
            CalamariCommand = "calamari-kubernetes-deploy",
            ExecutionMode = ExecutionMode.PackagedPayload,
            PayloadKind = PayloadKind.YamlBundle,
            Files = yamlFiles,
            Syntax = ScriptSyntax.PowerShell
        };
    }

    private async Task<string> GenerateFeedSecretAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        if (_externalFeedDataProvider == null)
            return null;

        var feedIds = CollectFeedIdsRequiringSecrets(ctx.Action);

        if (feedIds.Count == 0)
            return null;

        var namespaceName = GetNamespaceFromAction(ctx.Action);
        var secretYamls = new List<string>();

        foreach (var feedId in feedIds)
        {
            var feed = await _externalFeedDataProvider
                .GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);

            if (feed == null || !feed.PasswordHasValue)
                continue;

            var registryUri = KubernetesApiEndpointVariableContributor.ResolveFeedUri(feed);
            var secretName = BuildFeedSecretName(feed);

            InjectImagePullSecret(ctx.Action, secretName);

            var dockerConfigJson = BuildDockerConfigJson(registryUri, feed.Username, feed.Password);
            secretYamls.Add(GenerateSecretYaml(secretName, namespaceName, dockerConfigJson));
        }

        return secretYamls.Count > 0 ? string.Join("\n---\n", secretYamls) : null;
    }

    public static List<int> CollectFeedIdsRequiringSecrets(DeploymentActionDto action)
    {
        var containersProp = action.Properties?
            .FirstOrDefault(p => p.PropertyName == KubernetesProperties.Containers);

        if (containersProp == null || string.IsNullOrWhiteSpace(containersProp.PropertyValue))
            return new List<int>();

        var feedIds = new HashSet<int>();

        try
        {
            using var doc = JsonDocument.Parse(containersProp.PropertyValue);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<int>();

            foreach (var container in doc.RootElement.EnumerateArray())
            {
                if (!container.TryGetProperty(KubernetesContainerPayloadProperties.CreateFeedSecrets, out var secretsProp)
                    || !string.Equals(secretsProp.GetString(), KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (container.TryGetProperty(KubernetesContainerPayloadProperties.FeedId, out var feedProp)
                    && TryGetFeedId(feedProp, out var feedId))
                    feedIds.Add(feedId);
            }
        }
        catch
        {
            // Parse failure should not block deployment
        }

        return feedIds.ToList();
    }

    public static bool HasCreateFeedSecrets(DeploymentActionDto action)
    {
        var containersProp = action.Properties?
            .FirstOrDefault(p => p.PropertyName == KubernetesProperties.Containers);

        if (containersProp == null || string.IsNullOrWhiteSpace(containersProp.PropertyValue))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(containersProp.PropertyValue);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var container in doc.RootElement.EnumerateArray())
            {
                if (container.TryGetProperty(KubernetesContainerPayloadProperties.CreateFeedSecrets, out var prop)
                    && string.Equals(prop.GetString(), KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Parse failure should not block deployment
        }

        return false;
    }

    public static void InjectImagePullSecret(DeploymentActionDto action, string secretName)
    {
        const string propName = KubernetesProperties.PodSecurityImagePullSecrets;

        var existingProp = action.Properties?
            .FirstOrDefault(p => p.PropertyName == propName);

        var entry = new { name = secretName };

        if (existingProp == null)
        {
            action.Properties ??= new List<DeploymentActionPropertyDto>();
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                ActionId = action.Id,
                PropertyName = propName,
                PropertyValue = JsonSerializer.Serialize(new[] { entry })
            });
            return;
        }

        try
        {
            var secrets = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                existingProp.PropertyValue ?? KubernetesJsonLiterals.EmptyArray) ?? new();

            if (secrets.Any(s => s.TryGetValue(KubernetesImagePullSecretPayloadProperties.Name, out var n) && n == secretName))
                return;

            secrets.Add(new Dictionary<string, string> { [KubernetesImagePullSecretPayloadProperties.Name] = secretName });
            existingProp.PropertyValue = JsonSerializer.Serialize(secrets);
        }
        catch
        {
            existingProp.PropertyValue = JsonSerializer.Serialize(new[] { entry });
        }
    }

    public static string BuildDockerConfigJson(string registryUri, string username, string password)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

        var config = new
        {
            auths = new Dictionary<string, object>
            {
                [registryUri] = new { username, password, auth }
            }
        };

        return JsonSerializer.Serialize(config);
    }

    public static string GenerateSecretYaml(string secretName, string namespaceName, string dockerConfigJson)
    {
        var encodedConfig = Convert.ToBase64String(Encoding.UTF8.GetBytes(dockerConfigJson));

        return $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {secretName}
              namespace: {namespaceName}
            type: kubernetes.io/dockerconfigjson
            data:
              .dockerconfigjson: {encodedConfig}
            """;
    }

    public static string BuildFeedSecretName(ExternalFeed feed)
    {
        var slug = feed.Slug ?? feed.Name ?? $"feed-{feed.Id}";

        return $"{slug}-registry-secret".ToLowerInvariant();
    }

    public static string GetNamespaceFromAction(DeploymentActionDto action)
    {
        var ns = action.Properties?
            .FirstOrDefault(p => p.PropertyName == KubernetesProperties.Namespace)?.PropertyValue;

        if (string.IsNullOrWhiteSpace(ns))
            ns = action.Properties?
                .FirstOrDefault(p => p.PropertyName == KubernetesProperties.LegacyNamespace)?.PropertyValue;

        return string.IsNullOrWhiteSpace(ns) ? KubernetesDefaultValues.Namespace : ns;
    }

    private async Task ResolveContainerImagesAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        if (_externalFeedDataProvider == null)
            return;

        var containersProp = ctx.Action.Properties?
            .FirstOrDefault(p => p.PropertyName == KubernetesProperties.Containers);

        if (containersProp == null || string.IsNullOrWhiteSpace(containersProp.PropertyValue))
            return;

        try
        {
            var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(containersProp.PropertyValue);

            if (containers == null) return;

            var feedCache = new Dictionary<int, ExternalFeed>();
            var modified = false;

            foreach (var container in containers)
            {
                var resolvedImage = await ResolveContainerImageAsync(container, ctx, feedCache, ct).ConfigureAwait(false);

                if (resolvedImage == null) continue;

                container[KubernetesContainerPayloadProperties.Image] = JsonSerializer.SerializeToElement(resolvedImage);
                modified = true;
            }

            if (modified)
                containersProp.PropertyValue = JsonSerializer.Serialize(containers);
        }
        catch
        {
            // Parse failure should not block deployment
        }
    }

    private async Task<string> ResolveContainerImageAsync(
        Dictionary<string, JsonElement> container, ActionExecutionContext ctx,
        Dictionary<int, ExternalFeed> feedCache, CancellationToken ct)
    {
        if (!container.TryGetValue(KubernetesContainerPayloadProperties.PackageId, out var packageIdProp))
            return null;

        var packageId = packageIdProp.GetString();

        if (string.IsNullOrEmpty(packageId))
            return null;

        if (!container.TryGetValue(KubernetesContainerPayloadProperties.FeedId, out var feedIdProp)
            || !TryGetFeedId(feedIdProp, out var feedId))
            return null;

        var containerName = container.TryGetValue(KubernetesContainerPayloadProperties.Name, out var nameProp)
            ? nameProp.GetString() ?? string.Empty
            : string.Empty;

        var version = ResolvePackageVersion(ctx, containerName);

        if (string.IsNullOrEmpty(version))
            return null;

        if (!feedCache.TryGetValue(feedId, out var feed))
        {
            feed = await _externalFeedDataProvider.GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);
            feedCache[feedId] = feed;
        }

        if (feed == null) return null;

        var feedUri = KubernetesApiEndpointVariableContributor.ResolveFeedUri(feed);
        return $"{feedUri}/{packageId}:{version}";
    }

    public static string ResolvePackageVersion(ActionExecutionContext ctx, string containerName)
    {
        if (ctx.SelectedPackages != null && ctx.SelectedPackages.Count > 0)
        {
            var match = ctx.SelectedPackages.FirstOrDefault(sp =>
                string.Equals(sp.ActionName, ctx.Action.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(sp.PackageReferenceName, containerName, StringComparison.OrdinalIgnoreCase));

            if (match != null) return match.Version;
        }

        return ctx.Variables?.FirstOrDefault(v => v.Name == SpecialVariables.Action.PackageVersion)?.Value;
    }

    private static bool TryGetFeedId(JsonElement element, out int feedId)
    {
        feedId = 0;

        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out feedId);

        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), out feedId);

        return false;
    }
}
