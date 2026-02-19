using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments.Kubernetes;

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

    public string ActionType => "Squid.KubernetesDeployContainers";

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return _yamlGenerators.Any(g => g.CanHandle(action));
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
            Files = yamlFiles,
            Syntax = ScriptSyntax.PowerShell
        };
    }

    private async Task<string> GenerateFeedSecretAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        if (_externalFeedDataProvider == null || !ctx.Action.FeedId.HasValue)
            return null;

        if (!HasCreateFeedSecrets(ctx.Action))
            return null;

        var feed = await _externalFeedDataProvider
            .GetFeedByIdAsync(ctx.Action.FeedId.Value, ct).ConfigureAwait(false);

        if (feed == null || !feed.PasswordHasValue)
            return null;

        var registryUri = KubernetesEndpointVariableContributor.ResolveFeedUri(feed);
        var secretName = BuildFeedSecretName(feed);
        var namespaceName = GetNamespaceFromAction(ctx.Action);

        InjectImagePullSecret(ctx.Action, secretName);

        var dockerConfigJson = BuildDockerConfigJson(registryUri, feed.Username, feed.Password);

        return GenerateSecretYaml(secretName, namespaceName, dockerConfigJson);
    }

    public static bool HasCreateFeedSecrets(DeploymentActionDto action)
    {
        var containersProp = action.Properties?
            .FirstOrDefault(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");

        if (containersProp == null || string.IsNullOrWhiteSpace(containersProp.PropertyValue))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(containersProp.PropertyValue);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var container in doc.RootElement.EnumerateArray())
            {
                if (container.TryGetProperty("CreateFeedSecrets", out var prop)
                    && string.Equals(prop.GetString(), "True", StringComparison.OrdinalIgnoreCase))
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
        const string propName = "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets";

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
                existingProp.PropertyValue ?? "[]") ?? new();

            if (secrets.Any(s => s.TryGetValue("name", out var n) && n == secretName))
                return;

            secrets.Add(new Dictionary<string, string> { ["name"] = secretName });
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
            .FirstOrDefault(p => p.PropertyName == "Squid.Action.KubernetesContainers.Namespace")?.PropertyValue;

        if (string.IsNullOrWhiteSpace(ns))
            ns = action.Properties?
                .FirstOrDefault(p => p.PropertyName == "Squid.Action.Kubernetes.Namespace")?.PropertyValue;

        return string.IsNullOrWhiteSpace(ns) ? "default" : ns;
    }

    private async Task ResolveContainerImagesAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var packageVersion = ctx.Variables?
            .FirstOrDefault(v => v.Name == SpecialVariables.Action.PackageVersion)?.Value;

        if (string.IsNullOrEmpty(packageVersion) || !ctx.Action.FeedId.HasValue)
            return;

        if (_externalFeedDataProvider == null)
            return;

        var feed = await _externalFeedDataProvider
            .GetFeedByIdAsync(ctx.Action.FeedId.Value, ct).ConfigureAwait(false);

        if (feed == null) return;

        var feedUri = KubernetesEndpointVariableContributor.ResolveFeedUri(feed);
        var resolvedImage = $"{feedUri}/{ctx.Action.PackageId}:{packageVersion}";

        UpdateContainerImages(ctx.Action, resolvedImage);
    }

    public static void UpdateContainerImages(DeploymentActionDto action, string resolvedImage)
    {
        var containersProp = action.Properties?
            .FirstOrDefault(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");

        if (containersProp == null || string.IsNullOrWhiteSpace(containersProp.PropertyValue))
            return;

        try
        {
            using var doc = JsonDocument.Parse(containersProp.PropertyValue);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
                containersProp.PropertyValue);

            if (containers == null) return;

            foreach (var container in containers)
                container["Image"] = JsonSerializer.SerializeToElement(resolvedImage);

            containersProp.PropertyValue = JsonSerializer.Serialize(containers);
        }
        catch
        {
            // Parse failure should not block deployment
        }
    }
}
