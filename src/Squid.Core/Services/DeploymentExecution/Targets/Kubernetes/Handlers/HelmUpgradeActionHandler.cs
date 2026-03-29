using System.Text;
using System.Text.Json;
using Squid.Core.Extensions;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
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
}
