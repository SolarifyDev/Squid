using System.Text;
using System.Text.Json;
using Squid.Core.Extensions;
using Squid.Core.Services.Common;
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

    private record HelmChartSource(string ChartPath, string RepoSetupBlock, string ChartVersion);

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var syntax = ScriptSyntaxHelper.ResolveSyntax(ctx.Action);
        var template = LoadTemplate(syntax);
        var chartSource = await ResolveChartSourceAsync(ctx, syntax, ct).ConfigureAwait(false);

        var releaseName = ctx.Action.GetProperty(KubernetesHelmProperties.ReleaseName) ?? ctx.Action.Name ?? "release";
        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(ctx.Action);
        var helmExe = ctx.Action.GetProperty(KubernetesHelmProperties.CustomHelmExecutable) ?? string.Empty;
        var resetValues = ctx.Action.GetProperty(KubernetesHelmProperties.ResetValues) ?? KubernetesBooleanValues.True;
        var helmWait = ctx.Action.GetProperty(KubernetesHelmProperties.HelmWait)
            ?? (ctx.Action.GetProperty(KubernetesHelmProperties.ClientVersion) != null ? KubernetesBooleanValues.True : KubernetesBooleanValues.False);
        var waitForJobs = ctx.Action.GetProperty(KubernetesHelmProperties.WaitForJobs) ?? KubernetesBooleanValues.False;
        var timeout = ctx.Action.GetProperty(KubernetesHelmProperties.Timeout) ?? string.Empty;
        var additionalArgs = ctx.Action.GetProperty(KubernetesHelmProperties.AdditionalArgs) ?? string.Empty;

        var files = new Dictionary<string, byte[]>();
        var valuesFilesBlock = BuildValuesFilesBlock(ctx.Action, syntax, files);
        var setValuesBlock = BuildSetValuesBlock(ctx.Action, syntax);

        string B64(string value) => ShellEscapeHelper.Base64Encode(value ?? string.Empty);

        var scriptBody = template
            .Replace("{{ReleaseName}}", B64(releaseName), StringComparison.Ordinal)
            .Replace("{{ChartPath}}", B64(chartSource.ChartPath), StringComparison.Ordinal)
            .Replace("{{Namespace}}", B64(namespace_), StringComparison.Ordinal)
            .Replace("{{HelmExe}}", B64(helmExe), StringComparison.Ordinal)
            .Replace("{{ResetValues}}", B64(resetValues), StringComparison.Ordinal)
            .Replace("{{HelmWait}}", B64(helmWait), StringComparison.Ordinal)
            .Replace("{{WaitForJobs}}", B64(waitForJobs), StringComparison.Ordinal)
            .Replace("{{Timeout}}", B64(timeout), StringComparison.Ordinal)
            .Replace("{{AdditionalArgs}}", B64(additionalArgs), StringComparison.Ordinal)
            .Replace("{{ChartVersion}}", B64(chartSource.ChartVersion), StringComparison.Ordinal)
            .Replace("{{RepoSetupBlock}}", chartSource.RepoSetupBlock, StringComparison.Ordinal)
            .Replace("{{ValuesFilesBlock}}", valuesFilesBlock, StringComparison.Ordinal)
            .Replace("{{SetValuesBlock}}", setValuesBlock, StringComparison.Ordinal);

        return new ActionExecutionResult
        {
            ScriptBody = scriptBody,
            Files = files,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax
        };
    }

    private static string LoadTemplate(ScriptSyntax syntax)
    {
        var templateName = syntax == ScriptSyntax.Bash ? "HelmUpgrade.sh" : "HelmUpgrade.ps1";
        return UtilService.GetEmbeddedScriptContent(templateName);
    }

    private async Task<HelmChartSource> ResolveChartSourceAsync(ActionExecutionContext ctx, ScriptSyntax syntax, CancellationToken ct)
    {
        var feedIdStr = ctx.Action.GetProperty(KubernetesHelmProperties.ChartFeedId);

        if (string.IsNullOrWhiteSpace(feedIdStr) || !int.TryParse(feedIdStr, out var feedId) || _externalFeedDataProvider == null)
            return DefaultChartSource(ctx);

        var packageId = ctx.Action.GetProperty(KubernetesHelmProperties.ChartPackageId);

        if (string.IsNullOrWhiteSpace(packageId))
            return DefaultChartSource(ctx);

        var feed = await _externalFeedDataProvider.GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);

        if (feed == null)
            return DefaultChartSource(ctx);

        var chartPath = $"squid-helm-repo/{packageId}";
        var repoSetupBlock = BuildRepoSetupBlock(feed, syntax);
        var chartVersion = PackageVersionResolver.Resolve(ctx);

        return new HelmChartSource(chartPath, repoSetupBlock, chartVersion);
    }

    private static HelmChartSource DefaultChartSource(ActionExecutionContext ctx)
    {
        var chartPath = ctx.Action.GetProperty(KubernetesHelmProperties.ChartPath) ?? ".";
        return new HelmChartSource(chartPath, string.Empty, string.Empty);
    }

    private static string BuildRepoSetupBlock(Persistence.Entities.Deployments.ExternalFeed feed, ScriptSyntax syntax)
    {
        var hasCredentials = !string.IsNullOrWhiteSpace(feed.Username) && !string.IsNullOrWhiteSpace(feed.Password);

        return syntax == ScriptSyntax.Bash
            ? BuildBashRepoSetupBlock(feed, hasCredentials)
            : BuildPowerShellRepoSetupBlock(feed, hasCredentials);
    }

    private static string BuildBashRepoSetupBlock(Persistence.Entities.Deployments.ExternalFeed feed, bool hasCredentials)
    {
        string B64(string value) => ShellEscapeHelper.Base64Encode(value ?? string.Empty);

        var sb = new StringBuilder();
        sb.AppendLine($"SQUID_REPO_URL=\"$(b64d '{B64(feed.FeedUri)}')\"");

        sb.AppendLine("echo \"Adding Helm repo: $SQUID_REPO_URL\"");

        if (hasCredentials)
        {
            sb.AppendLine($"SQUID_REPO_USER=\"$(b64d '{B64(feed.Username)}')\"");
            sb.AppendLine($"SQUID_REPO_PASS=\"$(b64d '{B64(feed.Password)}')\"");
            sb.AppendLine("\"$HELM_EXE\" repo add squid-helm-repo \"$SQUID_REPO_URL\" --username \"$SQUID_REPO_USER\" --password \"$SQUID_REPO_PASS\" --force-update");
        }
        else
        {
            sb.AppendLine("\"$HELM_EXE\" repo add squid-helm-repo \"$SQUID_REPO_URL\" --force-update");
        }

        sb.AppendLine("echo \"Updating Helm repo...\"");
        sb.AppendLine("\"$HELM_EXE\" repo update squid-helm-repo");

        return sb.ToString();
    }

    private static string BuildPowerShellRepoSetupBlock(Persistence.Entities.Deployments.ExternalFeed feed, bool hasCredentials)
    {
        var sb = new StringBuilder();
        var repoUrl = ShellEscapeHelper.EscapePowerShell(feed.FeedUri ?? string.Empty);
        sb.AppendLine($"$squidRepoUrl = \"{repoUrl}\"");

        if (hasCredentials)
        {
            var repoUser = ShellEscapeHelper.EscapePowerShell(feed.Username ?? string.Empty);
            var repoPass = ShellEscapeHelper.EscapePowerShell(feed.Password ?? string.Empty);
            sb.AppendLine($"$squidRepoUser = \"{repoUser}\"");
            sb.AppendLine($"$squidRepoPass = \"{repoPass}\"");
            sb.AppendLine("& $helmExe repo add squid-helm-repo $squidRepoUrl --username $squidRepoUser --password $squidRepoPass --force-update 2>$null");
        }
        else
        {
            sb.AppendLine("& $helmExe repo add squid-helm-repo $squidRepoUrl --force-update 2>$null");
        }

        sb.AppendLine("& $helmExe repo update squid-helm-repo 2>$null");

        return sb.ToString();
    }

    private static string BuildValuesFilesBlock(DeploymentActionDto action, ScriptSyntax syntax, Dictionary<string, byte[]> files)
    {
        var sb = new StringBuilder();

        var valueSources = action.GetProperty(KubernetesHelmProperties.ValueSources);

        if (!string.IsNullOrWhiteSpace(valueSources))
        {
            BuildMultiSourceValues(valueSources, syntax, files, sb);
            return sb.ToString();
        }

        var rawYaml = action.GetProperty(KubernetesHelmProperties.YamlValues);

        if (string.IsNullOrWhiteSpace(rawYaml)) return string.Empty;

        const string rawYamlFileName = "rawYamlValues.yaml";
        files[rawYamlFileName] = Encoding.UTF8.GetBytes(rawYaml);

        if (syntax == ScriptSyntax.Bash)
            sb.AppendLine($"HELM_CMD+=(\"--values\" \"./{rawYamlFileName}\")");
        else
            sb.AppendLine($"$helmArgs += \"--values\"; $helmArgs += \".\\{rawYamlFileName}\"");

        return sb.ToString();
    }

    private static void BuildMultiSourceValues(string valueSources, ScriptSyntax syntax, Dictionary<string, byte[]> files, StringBuilder sb)
    {
        try
        {
            var sources = JsonSerializer.Deserialize<List<HelmValueSource>>(valueSources, SquidJsonDefaults.CaseInsensitive);

            if (sources == null) return;

            var fileIndex = 0;

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source.Value)) continue;

                if (string.Equals(source.Type, "InlineYaml", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = $"values-{fileIndex++}.yaml";
                    files[fileName] = Encoding.UTF8.GetBytes(source.Value);

                    if (syntax == ScriptSyntax.Bash)
                        sb.AppendLine($"HELM_CMD+=(\"--values\" \"./{fileName}\")");
                    else
                        sb.AppendLine($"$helmArgs += \"--values\"; $helmArgs += \".\\{fileName}\"");
                }
                else if (string.Equals(source.Type, "KeyValues", StringComparison.OrdinalIgnoreCase))
                {
                    var keyValues = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Value, SquidJsonDefaults.CaseInsensitive);

                    if (keyValues == null) continue;

                    foreach (var kvp in keyValues)
                        AppendSetValue(sb, syntax, kvp.Key, kvp.Value);
                }
            }
        }
        catch (JsonException)
        {
            // Parse failure should not block deployment
        }
    }

    private static string BuildSetValuesBlock(DeploymentActionDto action, ScriptSyntax syntax)
    {
        var keyValuesRaw = action.GetProperty(KubernetesHelmProperties.KeyValues);

        if (string.IsNullOrWhiteSpace(keyValuesRaw)) return string.Empty;

        var sb = new StringBuilder();

        try
        {
            var keyValues = JsonSerializer.Deserialize<Dictionary<string, string>>(keyValuesRaw, SquidJsonDefaults.CaseInsensitive);

            if (keyValues == null) return string.Empty;

            foreach (var kvp in keyValues)
            {
                AppendSetValue(sb, syntax, kvp.Key, kvp.Value);
            }
        }
        catch (JsonException)
        {
            var pairs = keyValuesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var pair in pairs)
            {
                AppendSetValueRaw(sb, syntax, pair);
            }
        }

        return sb.ToString();
    }

    private static void AppendSetValue(StringBuilder sb, ScriptSyntax syntax, string key, string value)
    {
        if (syntax == ScriptSyntax.Bash)
        {
            var escapedValue = value.Replace("'", "'\\''", StringComparison.Ordinal);
            sb.AppendLine($"HELM_CMD+=(\"--set\" \"{key}='{escapedValue}'\")");
        }
        else
        {
            var escapedValue = ShellEscapeHelper.EscapePowerShell(value);
            sb.AppendLine($"$helmArgs += \"--set\"; $helmArgs += \"{key}={escapedValue}\"");
        }
    }

    private static void AppendSetValueRaw(StringBuilder sb, ScriptSyntax syntax, string setValue)
    {
        if (syntax == ScriptSyntax.Bash)
            sb.AppendLine($"HELM_CMD+=(\"--set\" \"{setValue}\")");
        else
        {
            var escapedSetValue = ShellEscapeHelper.EscapePowerShell(setValue);
            sb.AppendLine($"$helmArgs += \"--set\"; $helmArgs += \"{escapedSetValue}\"");
        }
    }

    internal class HelmValueSource
    {
        public string Type { get; set; }
        public string Value { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyInlineValues = new Dictionary<string, string>();

    private record IntentChartSource(string ChartReference, string ChartVersion, HelmRepository Repository);

    /// <summary>
    /// Phase 9d — direct intent emission. Bypasses the default <c>PrepareAsync</c> +
    /// <c>LegacyIntentAdapter</c> seam and produces a <see cref="HelmUpgradeIntent"/>
    /// with a stable semantic name (<c>helm-upgrade</c>). Every legacy action property
    /// is projected onto a semantic intent field: flags (<c>ResetValues</c>, <c>Wait</c>,
    /// <c>WaitForJobs</c>), options (<c>Timeout</c>, <c>AdditionalArgs</c>, <c>CustomHelmExecutable</c>),
    /// rendered values files (<see cref="HelmUpgradeIntent.ValuesFiles"/>), inline <c>--set</c>
    /// overrides (<see cref="HelmUpgradeIntent.InlineValues"/>), and any attached feed-backed
    /// chart repository (<see cref="HelmRepository"/>). The legacy <c>PrepareAsync</c>
    /// path is preserved until Phase 9g flips the pipeline.
    /// </summary>
    async Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var action = ctx.Action;
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

        var rawYaml = action.GetProperty(KubernetesHelmProperties.YamlValues);

        if (string.IsNullOrWhiteSpace(rawYaml))
            return Array.Empty<DeploymentFile>();

        var content = Encoding.UTF8.GetBytes(rawYaml);

        return new[]
        {
            DeploymentFile.Asset("rawYamlValues.yaml", content)
        };
    }

    private static IReadOnlyDictionary<string, string> BuildInlineValuesForIntent(DeploymentActionDto action)
    {
        if (action == null) return EmptyInlineValues;

        var keyValuesRaw = action.GetProperty(KubernetesHelmProperties.KeyValues);

        if (string.IsNullOrWhiteSpace(keyValuesRaw)) return EmptyInlineValues;

        var parsed = TryParseJsonKeyValues(keyValuesRaw);

        return parsed ?? ParseCommaSeparatedKeyValues(keyValuesRaw);
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
