using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;

namespace Squid.Core.Services.Deployments.Process;

public interface IDeploymentPackageReferenceService : IScopedDependency
{
    Task<List<PackageReferenceDto>> GetPackageReferencesAsync(int projectId, int? channelId = null, CancellationToken ct = default);
}

public class PackageReferenceDto
{
    public string ActionName { get; set; }
    public string PackageReferenceName { get; set; }
    public string PackageId { get; set; }
    public int FeedId { get; set; }
    public string FeedName { get; set; }
    public string LastReleaseVersion { get; set; }
}

public class DeploymentPackageReferenceService : IDeploymentPackageReferenceService
{
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IDeploymentStepDataProvider _stepDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IReleaseSelectedPackageDataProvider _selectedPackageDataProvider;

    public DeploymentPackageReferenceService(
        IProjectDataProvider projectDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IActionChannelDataProvider actionChannelDataProvider,
        IExternalFeedDataProvider externalFeedDataProvider,
        IReleaseDataProvider releaseDataProvider,
        IReleaseSelectedPackageDataProvider selectedPackageDataProvider)
    {
        _projectDataProvider = projectDataProvider;
        _processDataProvider = processDataProvider;
        _stepDataProvider = stepDataProvider;
        _actionDataProvider = actionDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _externalFeedDataProvider = externalFeedDataProvider;
        _releaseDataProvider = releaseDataProvider;
        _selectedPackageDataProvider = selectedPackageDataProvider;
    }

    public async Task<List<PackageReferenceDto>> GetPackageReferencesAsync(int projectId, int? channelId = null, CancellationToken ct = default)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(projectId, ct).ConfigureAwait(false);

        if (project == null) return new List<PackageReferenceDto>();

        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(project.DeploymentProcessId, ct).ConfigureAwait(false);

        if (process == null) return new List<PackageReferenceDto>();

        var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(process.Id, ct).ConfigureAwait(false);
        var stepIds = steps.Select(s => s.Id).ToList();

        var allActions = await _actionDataProvider.GetDeploymentActionsByStepIdsAsync(stepIds, ct).ConfigureAwait(false);
        var actions = await FilterEligibleActionsAsync(allActions, channelId, ct).ConfigureAwait(false);
        var actionIds = actions.Select(a => a.Id).ToList();

        var allProperties = await _actionPropertyDataProvider
            .GetDeploymentActionPropertiesByActionIdsAsync(actionIds, ct).ConfigureAwait(false);

        var propertiesDict = allProperties.GroupBy(p => p.ActionId).ToDictionary(g => g.Key, g => g.ToList());

        var references = new List<PackageReferenceDto>();
        var feedIds = new HashSet<int>();

        foreach (var action in actions)
        {
            var props = propertiesDict.TryGetValue(action.Id, out var p) ? p : new();
            var containersProp = props.FirstOrDefault(x => x.PropertyName == KubernetesProperties.Containers);

            if (containersProp == null || string.IsNullOrWhiteSpace(containersProp.PropertyValue))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(containersProp.PropertyValue);

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

                    int feedId;

                    if (feedProp.ValueKind == JsonValueKind.Number)
                    {
                        if (!feedProp.TryGetInt32(out feedId)) continue;
                    }
                    else if (feedProp.ValueKind == JsonValueKind.String)
                    {
                        if (!int.TryParse(feedProp.GetString(), out feedId)) continue;
                    }
                    else continue;

                    var containerName = container.TryGetProperty(KubernetesContainerPayloadProperties.Name, out var nameProp)
                        ? nameProp.GetString() ?? string.Empty
                        : string.Empty;

                    feedIds.Add(feedId);

                    references.Add(new PackageReferenceDto
                    {
                        ActionName = action.Name,
                        PackageReferenceName = containerName,
                        PackageId = packageId,
                        FeedId = feedId
                    });
                }
            }
            catch
            {
                // Skip actions with invalid container JSON
            }
        }

        DetectActionLevelPackageReferences(actions, propertiesDict, feedIds, references);

        await EnrichFeedNamesAsync(references, feedIds, ct).ConfigureAwait(false);
        await EnrichLastReleaseVersionsAsync(references, projectId, ct).ConfigureAwait(false);

        return references;
    }

    private async Task<List<DeploymentAction>> FilterEligibleActionsAsync(List<DeploymentAction> actions, int? channelId, CancellationToken ct)
    {
        var eligible = actions.Where(a => !a.IsDisabled).ToList();

        if (channelId == null) return eligible;

        var actionIds = eligible.Select(a => a.Id).ToList();
        var allChannels = await _actionChannelDataProvider.GetActionChannelsByActionIdsAsync(actionIds, ct).ConfigureAwait(false);

        var channelsDict = allChannels.GroupBy(c => c.ActionId).ToDictionary(g => g.Key, g => g.Select(c => c.ChannelId).ToList());

        return eligible.Where(a => MatchesChannel(a.Id, channelsDict, channelId.Value)).ToList();
    }

    private static bool MatchesChannel(int actionId, Dictionary<int, List<int>> channelsDict, int channelId)
    {
        if (!channelsDict.TryGetValue(actionId, out var channels) || channels.Count == 0)
            return true;

        return channels.Contains(channelId);
    }

    private static void DetectActionLevelPackageReferences(
        List<DeploymentAction> actions, Dictionary<int, List<DeploymentActionProperty>> propertiesDict,
        HashSet<int> feedIds, List<PackageReferenceDto> references)
    {
        foreach (var action in actions)
        {
            var props = propertiesDict.TryGetValue(action.Id, out var p) ? p : new();
            var feedIdProp = props.FirstOrDefault(x => x.PropertyName == SpecialVariables.Action.PackageFeedId);
            var packageIdProp = props.FirstOrDefault(x => x.PropertyName == SpecialVariables.Action.PackageId);

            if (feedIdProp == null || string.IsNullOrWhiteSpace(feedIdProp.PropertyValue)) continue;
            if (packageIdProp == null || string.IsNullOrWhiteSpace(packageIdProp.PropertyValue)) continue;
            if (!int.TryParse(feedIdProp.PropertyValue, out var actionFeedId)) continue;

            feedIds.Add(actionFeedId);

            references.Add(new PackageReferenceDto
            {
                ActionName = action.Name,
                PackageReferenceName = string.Empty,
                PackageId = packageIdProp.PropertyValue,
                FeedId = actionFeedId
            });
        }
    }

    private async Task EnrichLastReleaseVersionsAsync(List<PackageReferenceDto> references, int projectId, CancellationToken ct)
    {
        if (references.Count == 0) return;

        var (_, releases) = await _releaseDataProvider.GetReleasesAsync(1, 1, projectId, cancellationToken: ct).ConfigureAwait(false);

        if (releases.Count == 0) return;

        var latestRelease = releases[0];
        var selectedPackages = await _selectedPackageDataProvider.GetByReleaseIdAsync(latestRelease.Id, ct).ConfigureAwait(false);

        if (selectedPackages.Count == 0) return;

        var versionLookup = selectedPackages.ToDictionary(
            sp => (sp.ActionName, sp.PackageReferenceName),
            sp => sp.Version);

        foreach (var reference in references)
        {
            if (versionLookup.TryGetValue((reference.ActionName, reference.PackageReferenceName), out var version))
                reference.LastReleaseVersion = version;
        }
    }

    private async Task EnrichFeedNamesAsync(List<PackageReferenceDto> references, HashSet<int> feedIds, CancellationToken ct)
    {
        if (feedIds.Count == 0) return;

        var feeds = await _externalFeedDataProvider
            .GetExternalFeedsByIdsAsync(feedIds.ToList(), ct).ConfigureAwait(false);

        var feedNameDict = feeds.ToDictionary(f => f.Id, f => f.Name);

        foreach (var reference in references)
        {
            if (feedNameDict.TryGetValue(reference.FeedId, out var name))
                reference.FeedName = name;
        }
    }
}
