using System.Text;
using System.Text.Json;
using Squid.Core.Extensions;
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

public class HelmUpgradeActionHandler : IActionHandler
{
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public HelmUpgradeActionHandler(IExternalFeedDataProvider externalFeedDataProvider = null)
    {
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public string ActionType => SpecialVariables.ActionTypes.HelmChartUpgrade;

    internal class HelmValueSource
    {
        public string Type { get; set; }
        public string Value { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyInlineValues = new Dictionary<string, string>();

    private record IntentChartSource(string ChartReference, string ChartVersion, HelmRepository Repository);

    /// <summary>
    /// Direct intent emission. Produces a <see cref="HelmUpgradeIntent"/> with a stable
    /// semantic name (<c>helm-upgrade</c>). Every action property is projected onto a
    /// semantic intent field: flags (<c>ResetValues</c>, <c>Wait</c>, <c>WaitForJobs</c>),
    /// options (<c>Timeout</c>, <c>AdditionalArgs</c>, <c>CustomHelmExecutable</c>),
    /// rendered values files (<see cref="HelmUpgradeIntent.ValuesFiles"/>), inline
    /// <c>--set</c> overrides (<see cref="HelmUpgradeIntent.InlineValues"/>), and any
    /// attached feed-backed chart repository (<see cref="HelmRepository"/>).
    /// </summary>
    async Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var action = ctx.Action;
        var syntax = ScriptSyntaxHelper.ResolveSyntax(action);
        var releaseName = BuildReleaseName(action);
        var chartSource = await ResolveIntentChartSourceAsync(ctx, ct).ConfigureAwait(false);
        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(action);
        var valuesFiles = BuildValuesFilesForIntent(action);
        var inlineValues = BuildInlineValuesForIntent(action);

        return new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = action?.Name ?? string.Empty,
            Syntax = syntax,
            ReleaseName = releaseName,
            ChartReference = chartSource.ChartReference,
            ChartVersion = chartSource.ChartVersion,
            Repository = chartSource.Repository,
            Namespace = namespace_,
            ValuesFiles = valuesFiles,
            InlineValues = inlineValues,
            CustomHelmExecutable = action?.GetProperty(KubernetesHelmProperties.CustomHelmExecutable) ?? string.Empty,
            ResetValues = ReadBoolProperty(action, KubernetesHelmProperties.ResetValues, defaultValue: true),
            Wait = ReadWaitFlag(action),
            WaitForJobs = ReadBoolProperty(action, KubernetesHelmProperties.WaitForJobs, defaultValue: false),
            Timeout = action?.GetProperty(KubernetesHelmProperties.Timeout) ?? string.Empty,
            AdditionalArgs = action?.GetProperty(KubernetesHelmProperties.AdditionalArgs) ?? string.Empty
        };
    }

    private static string BuildReleaseName(DeploymentActionDto action)
    {
        if (action == null) return "release";

        return action.GetProperty(KubernetesHelmProperties.ReleaseName)
            ?? action.Name
            ?? "release";
    }

    private async Task<IntentChartSource> ResolveIntentChartSourceAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var action = ctx.Action;
        var localReference = action?.GetProperty(KubernetesHelmProperties.ChartPath) ?? ".";
        var localSource = new IntentChartSource(localReference, string.Empty, null);

        var feedIdStr = action?.GetProperty(KubernetesHelmProperties.ChartFeedId);

        if (string.IsNullOrWhiteSpace(feedIdStr) || !int.TryParse(feedIdStr, out var feedId) || _externalFeedDataProvider == null)
            return localSource;

        var packageId = action.GetProperty(KubernetesHelmProperties.ChartPackageId);

        if (string.IsNullOrWhiteSpace(packageId))
            return localSource;

        var feed = await _externalFeedDataProvider.GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);

        if (feed == null)
            return localSource;

        var repository = new HelmRepository
        {
            Name = "squid-helm-repo",
            Url = feed.FeedUri ?? string.Empty,
            Username = string.IsNullOrWhiteSpace(feed.Username) ? null : feed.Username,
            Password = string.IsNullOrWhiteSpace(feed.Password) ? null : feed.Password
        };

        return new IntentChartSource(
            ChartReference: $"squid-helm-repo/{packageId}",
            ChartVersion: PackageVersionResolver.Resolve(ctx),
            Repository: repository);
    }

    private static IReadOnlyList<DeploymentFile> BuildValuesFilesForIntent(DeploymentActionDto action)
    {
        if (action == null) return Array.Empty<DeploymentFile>();

        var valueSources = action.GetProperty(KubernetesHelmProperties.ValueSources);

        if (!string.IsNullOrWhiteSpace(valueSources))
            return BuildMultiSourceValuesForIntent(valueSources);

        return BuildSingleYamlValuesForIntent(action);
    }

    private static IReadOnlyList<DeploymentFile> BuildMultiSourceValuesForIntent(string valueSources)
    {
        try
        {
            var sources = JsonSerializer.Deserialize<List<HelmValueSource>>(valueSources, SquidJsonDefaults.CaseInsensitive);

            if (sources == null) return Array.Empty<DeploymentFile>();

            var files = new List<DeploymentFile>();
            var fileIndex = 0;

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source.Value)) continue;
                if (!string.Equals(source.Type, "InlineYaml", StringComparison.OrdinalIgnoreCase)) continue;

                files.Add(DeploymentFile.Asset($"values-{fileIndex++}.yaml", Encoding.UTF8.GetBytes(source.Value)));
            }

            return files;
        }
        catch (JsonException)
        {
            return Array.Empty<DeploymentFile>();
        }
    }

    private static IReadOnlyList<DeploymentFile> BuildSingleYamlValuesForIntent(DeploymentActionDto action)
    {
        var rawYaml = action.GetProperty(KubernetesHelmProperties.YamlValues);

        if (string.IsNullOrWhiteSpace(rawYaml))
            return Array.Empty<DeploymentFile>();

        return new[] { DeploymentFile.Asset("rawYamlValues.yaml", Encoding.UTF8.GetBytes(rawYaml)) };
    }

    private static IReadOnlyDictionary<string, string> BuildInlineValuesForIntent(DeploymentActionDto action)
    {
        if (action == null) return EmptyInlineValues;

        var result = new Dictionary<string, string>();

        MergeKeyValuesFromValueSources(action, result);
        MergeKeyValuesFromProperty(action, result);

        return result.Count > 0 ? result : EmptyInlineValues;
    }

    private static void MergeKeyValuesFromValueSources(DeploymentActionDto action, Dictionary<string, string> target)
    {
        var valueSources = action.GetProperty(KubernetesHelmProperties.ValueSources);

        if (string.IsNullOrWhiteSpace(valueSources)) return;

        try
        {
            var sources = JsonSerializer.Deserialize<List<HelmValueSource>>(valueSources, SquidJsonDefaults.CaseInsensitive);

            if (sources == null) return;

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source.Value)) continue;
                if (!string.Equals(source.Type, "KeyValues", StringComparison.OrdinalIgnoreCase)) continue;

                var kvp = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Value, SquidJsonDefaults.CaseInsensitive);

                if (kvp == null) continue;

                foreach (var entry in kvp)
                    target[entry.Key] = entry.Value;
            }
        }
        catch (JsonException)
        {
            // Parse failure should not block deployment
        }
    }

    private static void MergeKeyValuesFromProperty(DeploymentActionDto action, Dictionary<string, string> target)
    {
        var keyValuesRaw = action.GetProperty(KubernetesHelmProperties.KeyValues);

        if (string.IsNullOrWhiteSpace(keyValuesRaw)) return;

        var parsed = TryParseJsonKeyValues(keyValuesRaw) ?? ParseCommaSeparatedKeyValues(keyValuesRaw);

        foreach (var entry in parsed)
            target[entry.Key] = entry.Value;
    }

    private static Dictionary<string, string> TryParseJsonKeyValues(string raw)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw, SquidJsonDefaults.CaseInsensitive);
            return dict ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseCommaSeparatedKeyValues(string raw)
    {
        var result = new Dictionary<string, string>();
        var pairs = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);

            if (eq <= 0) continue;

            var key = pair.Substring(0, eq);
            var value = pair.Substring(eq + 1);
            result[key] = value;
        }

        return result;
    }

    private static bool ReadBoolProperty(DeploymentActionDto action, string propertyName, bool defaultValue)
    {
        if (action == null) return defaultValue;

        var raw = action.GetProperty(propertyName);

        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;

        return string.Equals(raw, KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadWaitFlag(DeploymentActionDto action)
    {
        if (action == null) return false;

        var explicitValue = action.GetProperty(KubernetesHelmProperties.HelmWait);

        if (!string.IsNullOrWhiteSpace(explicitValue))
            return string.Equals(explicitValue, KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase);

        // Legacy quirk: ClientVersion presence defaults Wait to true (matches PrepareAsync).
        return action.GetProperty(KubernetesHelmProperties.ClientVersion) != null;
    }
}
