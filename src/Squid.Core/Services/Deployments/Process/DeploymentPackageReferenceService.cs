using System.Text.Json;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.DeploymentExecution.Kubernetes;

namespace Squid.Core.Services.Deployments.Process;

public interface IDeploymentPackageReferenceService : IScopedDependency
{
    Task<List<PackageReferenceDto>> GetPackageReferencesAsync(int projectId, CancellationToken ct = default);
}

public class PackageReferenceDto
{
    public string ActionName { get; set; }
    public string PackageReferenceName { get; set; }
    public string PackageId { get; set; }
    public int FeedId { get; set; }
    public string FeedName { get; set; }
}

public class DeploymentPackageReferenceService : IDeploymentPackageReferenceService
{
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IDeploymentStepDataProvider _stepDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public DeploymentPackageReferenceService(
        IProjectDataProvider projectDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IExternalFeedDataProvider externalFeedDataProvider)
    {
        _projectDataProvider = projectDataProvider;
        _processDataProvider = processDataProvider;
        _stepDataProvider = stepDataProvider;
        _actionDataProvider = actionDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public async Task<List<PackageReferenceDto>> GetPackageReferencesAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(projectId, ct).ConfigureAwait(false);

        if (project == null) return new List<PackageReferenceDto>();

        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(project.DeploymentProcessId, ct).ConfigureAwait(false);

        if (process == null) return new List<PackageReferenceDto>();

        var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(process.Id, ct).ConfigureAwait(false);
        var stepIds = steps.Select(s => s.Id).ToList();

        var actions = await _actionDataProvider.GetDeploymentActionsByStepIdsAsync(stepIds, ct).ConfigureAwait(false);
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

        await EnrichFeedNamesAsync(references, feedIds, ct).ConfigureAwait(false);

        return references;
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
