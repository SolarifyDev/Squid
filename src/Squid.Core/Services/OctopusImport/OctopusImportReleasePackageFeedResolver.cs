using System.Text.Json;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

internal static class OctopusImportReleasePackageFeedResolver
{
    // A release package carries its feed (REST bundles stamp it); otherwise it is read from the release's process snapshot,
    // then from the project's current actions.
    internal static string GetSourceFeedId(OctopusSelectedPackageDto selectedPackage, Func<string, OctopusDeploymentProcessDto> snapshotOf, Func<string, IEnumerable<OctopusDeploymentActionDto>> currentActionsOf, OctopusReleaseDto release)
        => GetExtensionString(selectedPackage.ExtensionData, "FeedId") ?? GetSourceFeedIdFromAction(snapshotOf(release.ProjectDeploymentProcessSnapshotId), currentActionsOf(release.ProjectId), selectedPackage);

    internal static bool IsActionableFeed(OctopusImportResourceResultDto resource)
    {
        if (!string.Equals(resource.SourceType, OctopusResourceKind.Feed.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;

        return OctopusImportPreviewActions.ResolvesToDestination(resource.PreviewAction)
               && (resource.PreviewAction == OctopusImportPreviewAction.Create || resource.DestinationId is > 0);
    }

    private static string GetSourceFeedIdFromAction(OctopusDeploymentProcessDto snapshot, IEnumerable<OctopusDeploymentActionDto> currentActions, OctopusSelectedPackageDto selectedPackage)
    {
        var action = FindReleaseAction(snapshot?.Steps.SelectMany(step => step.Actions) ?? [], selectedPackage) ?? FindReleaseAction(currentActions, selectedPackage);

        return action == null ? null : FeedIdOf(action, selectedPackage);
    }

    /// <summary>The feed of a release's selected package, read from the release's deployment process snapshot.</summary>
    public static string GetSourceFeedIdFromProcess(OctopusDeploymentProcessDto process, OctopusSelectedPackageDto selectedPackage)
    {
        var action = FindReleaseAction(process?.Steps?.SelectMany(step => step.Actions ?? []) ?? [], selectedPackage);

        return action == null ? null : FeedIdOf(action, selectedPackage);
    }

    private static string FeedIdOf(OctopusDeploymentActionDto action, OctopusSelectedPackageDto selectedPackage)
    {
        var package = FindActionPackage(action, selectedPackage.PackageReferenceName);
        if (!string.IsNullOrWhiteSpace(package?.FeedId))
            return package.FeedId;

        if (Mapping.Actions.OctopusImportKubernetesActionMapperSupport.TryGetProperty(
                action,
                OctopusPropertyNames.ActionPackageFeedId,
                out var actionFeedId))
        {
            return actionFeedId;
        }

        return action.Container?.FeedId;
    }

    private static OctopusDeploymentActionDto FindReleaseAction(
        IEnumerable<OctopusDeploymentActionDto> actions,
        OctopusSelectedPackageDto selectedPackage)
    {
        var matchingActions = actions
            .Where(action => string.Equals(
                action.Name,
                selectedPackage.ActionName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matchingActions.FirstOrDefault(action => FindActionPackage(action, selectedPackage.PackageReferenceName) != null)
               ?? matchingActions.FirstOrDefault();
    }

    private static OctopusActionPackageDto FindActionPackage(
        OctopusDeploymentActionDto action,
        string packageReferenceName)
    {
        var packages = action.Packages ?? [];
        if (packages.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(packageReferenceName))
            return packages.Count == 1 ? packages[0] : null;

        return packages.FirstOrDefault(package =>
            string.Equals(package.Name, packageReferenceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(package.Id, packageReferenceName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetExtensionString(
        Dictionary<string, JsonElement> extensionData,
        string propertyName)
    {
        if (extensionData == null)
            return null;

        foreach (var (key, value) in extensionData)
        {
            if (!string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }

        return null;
    }
}

/// <summary>
/// What resolving a release package's source feed reads (process snapshots by id, current actions by project), indexed once per
/// plan: a lookup per package would scan the whole graph for each of thousands of releases.
/// </summary>
internal sealed class OctopusImportReleasePackageFeedLookup
{
    private readonly Dictionary<string, OctopusResourceNode> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILookup<string, OctopusResourceNode> _actionsByProject;

    public OctopusImportReleasePackageFeedLookup(OctopusResourceGraph graph, IEnumerable<OctopusResourceNode> currentResources)
    {
        foreach (var snapshot in graph.Resources.Where(resource => resource.Kind == OctopusResourceKind.DeploymentProcessSnapshot && resource.SourceId != null))
            _snapshots.TryAdd(snapshot.SourceId, snapshot);

        _actionsByProject = currentResources.Where(resource => resource.Kind == OctopusResourceKind.DeploymentAction).ToLookup(resource => resource.OwnerProjectId ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    public string GetSourceFeedId(OctopusReleaseDto release, OctopusSelectedPackageDto selectedPackage)
        => OctopusImportReleasePackageFeedResolver.GetSourceFeedId(selectedPackage, SnapshotOf, CurrentActionsOf, release);

    private OctopusDeploymentProcessDto SnapshotOf(string snapshotId)
        => snapshotId != null && _snapshots.TryGetValue(snapshotId, out var snapshot) ? snapshot.GetSource<OctopusDeploymentProcessDto>() : null;

    private IEnumerable<OctopusDeploymentActionDto> CurrentActionsOf(string projectId)
        => _actionsByProject[projectId ?? string.Empty].Select(resource => resource.GetSource<OctopusDeploymentActionDto>()).Where(action => action != null);
}

/// <summary>
/// Whether a release package's source feed resolves to a Squid feed, indexed once per validation over the plan's actionable feeds:
/// an exact source id, or a unique feed whose id starts with "{id}-" (an export id with its suffix).
/// </summary>
internal sealed class OctopusImportPlannedFeedIndex
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _prefixCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly OctopusImportPriorMappings _prior;

    public OctopusImportPlannedFeedIndex(IReadOnlyDictionary<string, OctopusImportResourceResultDto> previewResources, OctopusImportPriorMappings prior = null)
    {
        _prior = prior;

        foreach (var feed in previewResources.Values.Where(OctopusImportReleasePackageFeedResolver.IsActionableFeed).Where(feed => feed.SourceId != null))
            Add(feed.SourceId);
    }

    public bool HasPlannedDestination(string sourceFeedId)
    {
        if (string.IsNullOrWhiteSpace(sourceFeedId))
            return false;

        if (int.TryParse(sourceFeedId, out var destinationFeedId) && destinationFeedId > 0)
            return true;

        // A feed imported earlier resolves through the source map even when this source no longer carries it.
        if (_prior != null && _prior.TryGet(OctopusResourceKind.Feed.ToString(), sourceFeedId, out _))
            return true;

        return _exact.Contains(sourceFeedId) || _prefixCounts.GetValueOrDefault(sourceFeedId.Trim()) == 1;
    }

    // Every "{prefix}-" the id starts with counts once for that prefix.
    private void Add(string sourceId)
    {
        _exact.Add(sourceId);

        for (var dash = sourceId.IndexOf('-'); dash >= 0; dash = sourceId.IndexOf('-', dash + 1))
            _prefixCounts[sourceId[..dash]] = _prefixCounts.GetValueOrDefault(sourceId[..dash]) + 1;
    }
}
