using System.Text;
using Squid.Core.Extensions;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployYamlActionHandler : IActionHandler
{
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;
    private readonly IPackageContentFetcher _packageContentFetcher;

    public KubernetesDeployYamlActionHandler(
        IExternalFeedDataProvider externalFeedDataProvider = null,
        IPackageContentFetcher packageContentFetcher = null)
    {
        _externalFeedDataProvider = externalFeedDataProvider;
        _packageContentFetcher = packageContentFetcher;
    }

    public string ActionType => SpecialVariables.ActionTypes.KubernetesDeployRawYaml;

    /// <summary>
    /// Phase 9c.1 — direct intent emission. Bypasses the default <c>PrepareAsync</c> +
    /// <c>LegacyIntentAdapter</c> seam and produces a <see cref="KubernetesApplyIntent"/>
    /// with a stable semantic name (<c>k8s-apply</c>) and the namespace resolved from the
    /// action properties. The YAML source resolution (inline or external feed) is shared
    /// with <see cref="PrepareAsync"/> via <see cref="ResolveYamlSourceAsync"/>.
    /// </summary>
    async Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var syntax = ScriptSyntaxHelper.ResolveSyntax(ctx.Action);
        var source = await ResolveYamlSourceAsync(ctx, syntax, ct).ConfigureAwait(false);

        var yamlFiles = DeploymentFileCollection.FromLegacyFiles(source.Files).ToList();
        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(ctx.Action);

        return new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            YamlFiles = yamlFiles,
            Assets = yamlFiles,
            Namespace = namespace_,
            ServerSideApply = false
        };
    }

    private record YamlDeploySource(Dictionary<string, byte[]> Files, string TargetPath, List<string> Warnings);

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var syntax = ScriptSyntaxHelper.ResolveSyntax(ctx.Action);
        var source = await ResolveYamlSourceAsync(ctx, syntax, ct).ConfigureAwait(false);

        var targetPath = syntax == ScriptSyntax.Bash ? source.TargetPath : source.TargetPath.Replace("/", "\\");
        var scriptBody = KubernetesApplyCommandBuilder.Build(targetPath, ctx.Action, syntax);

        var namespace_ = KubernetesYamlActionHandler.GetNamespaceFromAction(ctx.Action);
        scriptBody += KubernetesResourceWaitBuilder.BuildWaitScript(source.Files, ctx.Action, namespace_, syntax);

        return new ActionExecutionResult
        {
            ScriptBody = scriptBody,
            Files = source.Files,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax,
            Warnings = source.Warnings
        };
    }

    private async Task<YamlDeploySource> ResolveYamlSourceAsync(ActionExecutionContext ctx, ScriptSyntax syntax, CancellationToken ct)
    {
        var inlineYaml = ctx.Action.GetProperty(KubernetesRawYamlProperties.InlineYaml) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(inlineYaml))
            return ResolveInlineYamlSource(inlineYaml);

        var feedIdStr = ctx.Action.GetProperty(SpecialVariables.Action.PackageFeedId);
        var packageId = ctx.Action.GetProperty(SpecialVariables.Action.PackageId);

        if (string.IsNullOrWhiteSpace(feedIdStr) || !int.TryParse(feedIdStr, out var feedId))
            return ContentDirSource();

        if (string.IsNullOrWhiteSpace(packageId))
            return ContentDirSource();

        if (_externalFeedDataProvider == null || _packageContentFetcher == null)
            return ContentDirSource();

        var feed = await _externalFeedDataProvider.GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);

        if (feed == null)
            return ContentDirSource();

        if (IsHelmFeed(feed))
            throw new InvalidOperationException($"Feed '{feed.FeedUri}' is a Helm chart repository. Use the 'Helm Chart Upgrade' action type to deploy Helm charts — 'Deploy Kubernetes YAML' only supports packages containing raw Kubernetes manifests.");

        var version = PackageVersionResolver.Resolve(ctx);
        var fetchResult = await _packageContentFetcher.FetchAsync(feed, packageId, version, ct).ConfigureAwait(false);

        if (fetchResult.Files.Count == 0)
        {
            var detail = fetchResult.Warnings.Count > 0 ? string.Join("; ", fetchResult.Warnings) : "no YAML files found in package";
            throw new InvalidOperationException($"Package {packageId} v{version} from feed {feed.Id} contained no deployable files: {detail}");
        }

        var prefixedFiles = new Dictionary<string, byte[]>();

        foreach (var kvp in fetchResult.Files)
            prefixedFiles[$"content/{kvp.Key}"] = kvp.Value;

        return new YamlDeploySource(prefixedFiles, "./content/", fetchResult.Warnings);
    }

    private static YamlDeploySource ResolveInlineYamlSource(string inlineYaml)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["inline-deployment.yaml"] = Encoding.UTF8.GetBytes(inlineYaml)
        };

        return new YamlDeploySource(files, "./inline-deployment.yaml", new List<string>());
    }

    private static YamlDeploySource ContentDirSource()
        => new(new Dictionary<string, byte[]>(), "./content/", new List<string>());

    private static bool IsHelmFeed(Persistence.Entities.Deployments.ExternalFeed feed)
    {
        var feedType = feed.FeedType ?? string.Empty;
        return feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase);
    }
}
