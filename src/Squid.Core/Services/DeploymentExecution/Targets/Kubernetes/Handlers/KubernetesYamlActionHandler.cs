using System.Text;
using System.Text.Json;
using Serilog;
using Squid.Core.Extensions;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Constants;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

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

    public string ActionType => SpecialVariables.ActionTypes.KubernetesDeployContainers;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return string.Equals(action.ActionType, ActionType, StringComparison.OrdinalIgnoreCase)
               && _yamlGenerators.Any(g => g.CanHandle(action));
    }

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var generator = _yamlGenerators.FirstOrDefault(g => g.CanHandle(ctx.Action));

        if (generator == null)
            return null;

        var syntax = ScriptSyntax.Bash;

        var yamlFiles = await ResolveGeneratedYamlFilesAsync(ctx, generator, ct).ConfigureAwait(false);

        var scriptBody = BuildApplyScript(yamlFiles, ctx.Action, syntax);
        scriptBody += ExtractAndAppendShellScripts(yamlFiles);

        var namespace_ = GetNamespaceFromAction(ctx.Action);
        scriptBody += KubernetesResourceWaitBuilder.BuildWaitScript(yamlFiles, ctx.Action, namespace_, syntax);

        RemoveNonYamlFiles(yamlFiles);

        return new ActionExecutionResult
        {
            ScriptBody = scriptBody,
            Files = yamlFiles,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax
        };
    }

    /// <summary>
    /// Phase 9c.2 — direct intent emission. Bypasses <see cref="PrepareAsync"/> and the
    /// <c>LegacyIntentAdapter</c> seam, producing a <see cref="KubernetesApplyIntent"/>
    /// with a stable semantic name (<c>k8s-apply</c>). YAML generation (container image
    /// resolution, deployment id suffix injection, feed secret injection, generator
    /// invocation) is shared with <see cref="PrepareAsync"/> via
    /// <see cref="ResolveGeneratedYamlFilesAsync"/>; the intent's <c>YamlFiles</c> is
    /// then filtered to valid YAML entries only — non-YAML helper scripts (e.g. <c>.sh</c>)
    /// are a legacy kubectl-wrapping concern that do not belong in the intent model.
    /// </summary>
    async Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var generator = _yamlGenerators.FirstOrDefault(g => g.CanHandle(ctx.Action));
        var files = generator == null
            ? new Dictionary<string, byte[]>()
            : await ResolveGeneratedYamlFilesAsync(ctx, generator, ct).ConfigureAwait(false);

        var yamlOnly = files.Where(kvp => IsYamlFile(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var yamlFiles = DeploymentFileCollection.FromLegacyFiles(yamlOnly).ToList();
        var namespace_ = GetNamespaceFromAction(ctx.Action);

        var (serverSide, fieldManager, forceConflicts) = KubernetesApplyIntentFactory.ReadServerSideApply(ctx.Action);
        var (objectStatusCheck, statusCheckTimeout) = KubernetesApplyIntentFactory.ReadObjectStatusCheck(ctx.Action);

        return new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            YamlFiles = yamlFiles,
            Assets = yamlFiles,
            Namespace = namespace_,
            Syntax = ScriptSyntax.Bash,
            ServerSideApply = serverSide,
            FieldManager = fieldManager,
            ForceConflicts = forceConflicts,
            ObjectStatusCheck = objectStatusCheck,
            StatusCheckTimeoutSeconds = statusCheckTimeout
        };
    }

    private async Task<Dictionary<string, byte[]>> ResolveGeneratedYamlFilesAsync(
        ActionExecutionContext ctx, IActionYamlGenerator generator, CancellationToken ct)
    {
        await ResolveContainerImagesAsync(ctx, ct).ConfigureAwait(false);
        InjectDeploymentIdSuffix(ctx);

        var secretYaml = await GenerateFeedSecretAsync(ctx, ct).ConfigureAwait(false);

        var files = await generator.GenerateAsync(ctx.Step, ctx.Action, ct).ConfigureAwait(false)
            ?? new Dictionary<string, byte[]>();

        if (secretYaml != null)
            files["feedsecrets.yaml"] = Encoding.UTF8.GetBytes(secretYaml);

        return files;
    }

    private static string BuildApplyScript(Dictionary<string, byte[]> yamlFiles, DeploymentActionDto action, ScriptSyntax syntax)
    {
        var sb = new StringBuilder();

        foreach (var fileName in yamlFiles.Keys.Where(IsYamlFile).OrderBy(f => f, StringComparer.Ordinal))
        {
            var targetPath = syntax == ScriptSyntax.Bash ? $"./{fileName}" : $".\\{fileName}";
            sb.AppendLine(KubernetesApplyCommandBuilder.Build(targetPath, action, syntax));
        }

        return sb.ToString();
    }

    private static string ExtractAndAppendShellScripts(Dictionary<string, byte[]> yamlFiles)
    {
        var sb = new StringBuilder();

        foreach (var fileName in yamlFiles.Keys.Where(IsShellScript).OrderBy(f => f, StringComparer.Ordinal))
        {
            var content = Encoding.UTF8.GetString(yamlFiles[fileName]);

            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine();
                sb.AppendLine(content);
            }
        }

        return sb.ToString();
    }

    private static void RemoveNonYamlFiles(Dictionary<string, byte[]> yamlFiles)
    {
        var nonYamlKeys = yamlFiles.Keys.Where(k => !IsYamlFile(k)).ToList();

        foreach (var key in nonYamlKeys)
            yamlFiles.Remove(key);
    }

    private static bool IsYamlFile(string fileName)
        => fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static bool IsShellScript(string fileName)
        => fileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);

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
            using var doc = KubernetesPropertyParser.SafeParseJson(containersProp.PropertyValue);

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
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse container feed secrets from action");
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
            using var doc = KubernetesPropertyParser.SafeParseJson(containersProp.PropertyValue);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var container in doc.RootElement.EnumerateArray())
            {
                if (container.TryGetProperty(KubernetesContainerPayloadProperties.CreateFeedSecrets, out var prop)
                    && string.Equals(prop.GetString(), KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse containers for feed secrets check");
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
                existingProp.PropertyValue ?? KubernetesJsonLiterals.EmptyArray, SquidJsonDefaults.CaseInsensitive) ?? new();

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
        var escapedName = YamlSafeScalar.Escape(secretName);
        var escapedNamespace = YamlSafeScalar.Escape(namespaceName);

        return $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {escapedName}
              namespace: {escapedNamespace}
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

    private static void InjectDeploymentIdSuffix(ActionExecutionContext ctx)
    {
        var deploymentId = ctx.Variables?.FirstOrDefault(v => v.Name == SpecialVariables.Deployment.Id)?.Value;
        if (string.IsNullOrWhiteSpace(deploymentId)) return;

        ctx.Action.Properties ??= new List<DeploymentActionPropertyDto>();
        ctx.Action.Properties.Add(new DeploymentActionPropertyDto
        {
            ActionId = ctx.Action.Id,
            PropertyName = KubernetesProperties.DeploymentIdSuffix,
            PropertyValue = deploymentId.ToLowerInvariant()
        });
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
            var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(containersProp.PropertyValue, SquidJsonDefaults.CaseInsensitive);

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
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse container images for resolution");
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

        var version = PackageVersionResolver.Resolve(ctx, containerName);

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
