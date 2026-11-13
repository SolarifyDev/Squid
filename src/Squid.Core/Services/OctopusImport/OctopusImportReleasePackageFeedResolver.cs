using System.Text.Json;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

internal static class OctopusImportReleasePackageFeedResolver
{
    public static string GetSourceFeedId(
        OctopusResourceGraph graph,
        IEnumerable<OctopusResourceNode> currentResources,
        OctopusReleaseDto release,
        OctopusSelectedPackageDto selectedPackage)
    {
        return GetExtensionString(selectedPackage.ExtensionData, "FeedId")
               ?? GetSourceFeedIdFromAction(graph, currentResources, release, selectedPackage);
    }

    public static bool HasPlannedDestination(
        string sourceFeedId,
        IReadOnlyDictionary<string, OctopusImportResourceResultDto> previewResources,
        OctopusImportPriorMappings prior = null)
    {
        if (string.IsNullOrWhiteSpace(sourceFeedId))
            return false;

        if (int.TryParse(sourceFeedId, out var destinationFeedId) && destinationFeedId > 0)
            return true;

        // A feed imported earlier resolves through the source map even when this source no longer carries it.
        if (prior != null && prior.TryGet(OctopusResourceKind.Feed.ToString(), sourceFeedId, out _))
            return true;

        var actionableFeeds = previewResources.Values
            .Where(IsActionableFeed)
            .ToList();
        var exactMatch = actionableFeeds.FirstOrDefault(feed =>
            string.Equals(feed.SourceId, sourceFeedId, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
            return true;

        var sourcePrefix = sourceFeedId.Trim();
        return actionableFeeds.Count(feed =>
            feed.SourceId.StartsWith($"{sourcePrefix}-", StringComparison.OrdinalIgnoreCase)) == 1;
    }

    private static bool IsActionableFeed(OctopusImportResourceResultDto resource)
    {
        if (!string.Equals(resource.SourceType, OctopusResourceKind.Feed.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;

        return OctopusImportPreviewActions.ResolvesToDestination(resource.PreviewAction)
               && (resource.PreviewAction == OctopusImportPreviewAction.Create || resource.DestinationId is > 0);
    }

    private static string GetSourceFeedIdFromAction(
        OctopusResourceGraph graph,
        IEnumerable<OctopusResourceNode> currentResources,
        OctopusReleaseDto release,
        OctopusSelectedPackageDto selectedPackage)
    {
        var snapshot = graph.Resources
            .Where(resource => resource.Kind == OctopusResourceKind.DeploymentProcessSnapshot)
            .FirstOrDefault(resource => string.Equals(
                resource.SourceId,
                release.ProjectDeploymentProcessSnapshotId,
                StringComparison.OrdinalIgnoreCase))
            ?.GetSource<OctopusDeploymentProcessDto>();
        var action = FindReleaseAction(
                         snapshot?.Steps.SelectMany(step => step.Actions) ?? [],
                         selectedPackage)
                     ?? FindReleaseAction(
                         currentResources
                             .Where(resource => resource.Kind == OctopusResourceKind.DeploymentAction)
                             .Where(resource => string.Equals(
                                 resource.OwnerProjectId,
                                 release.ProjectId,
                                 StringComparison.OrdinalIgnoreCase))
                             .Select(resource => resource.GetSource<OctopusDeploymentActionDto>())
                             .Where(action => action != null),
                         selectedPackage);

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
