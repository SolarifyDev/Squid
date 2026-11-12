using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public sealed partial class OctopusImportConfirmationRunner
{
    // Every kind of resource the plan creates needs the owner's create permission in the destination space.
    private async Task EnsureImportPermissionsAsync(CancellationToken ct)
    {
        foreach (var permission in RequiredPermissions())
        {
            var request = new PermissionCheckRequest { UserId = _ctx.OwnerUserId, Permission = permission, SpaceId = _ctx.SpaceId };

            await authorizationService.EnsurePermissionAsync(request, ct).ConfigureAwait(false);
        }
    }

    private IEnumerable<Permission> RequiredPermissions()
    {
        var currentResources = _ctx.Snapshot.Graph.Resources.GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var previews = _ctx.ValidatedPlan.PreviewPlan.Resources;

        return previews
            .Where(r => r.PreviewAction == OctopusImportPreviewAction.Create)
            .Select(r => ResolvePermission(r, currentResources, previews))
            .Where(permission => permission.HasValue)
            .Select(permission => permission.Value)
            .Distinct();
    }

    private static Permission? ResolvePermission(OctopusImportResourceResultDto previewResource, IReadOnlyDictionary<string, OctopusResourceNode> currentResources, IReadOnlyList<OctopusImportResourceResultDto> previews)
    {
        if (!currentResources.TryGetValue(previewResource.SourceId, out var resource)) return null;

        return resource.Kind switch
        {
            OctopusResourceKind.ProjectGroup or OctopusResourceKind.Project => Permission.ProjectCreate,
            OctopusResourceKind.Environment => Permission.EnvironmentCreate,
            OctopusResourceKind.Lifecycle => Permission.LifecycleCreate,
            OctopusResourceKind.Feed => Permission.FeedEdit,
            OctopusResourceKind.Account => Permission.AccountCreate,
            OctopusResourceKind.Team => Permission.TeamCreate,
            OctopusResourceKind.LibraryVariableSet => Permission.VariableEdit,
            OctopusResourceKind.Channel => ResolveChannelPermission(resource, previews),
            OctopusResourceKind.VariableSet => Permission.VariableEdit,
            OctopusResourceKind.DeploymentProcess => Permission.ProcessEdit,
            OctopusResourceKind.Release => Permission.ReleaseCreate,
            _ => null
        };
    }

    // A default channel takes over the default channel created with its project (an edit); under a project imported
    // before, confirmation creates it as a regular channel.
    private static Permission ResolveChannelPermission(OctopusResourceNode resource, IReadOnlyList<OctopusImportResourceResultDto> previews)
    {
        var channel = resource.GetSource<OctopusChannelDto>();

        return channel?.IsDefault == true && !IsProjectAlreadyImported(channel.ProjectId, previews) ? Permission.ChannelEdit : Permission.ChannelCreate;
    }

    private static bool IsProjectAlreadyImported(string projectSourceId, IReadOnlyList<OctopusImportResourceResultDto> previews)
        => previews.Any(p => p.SourceType == nameof(OctopusResourceKind.Project) && string.Equals(p.SourceId, projectSourceId, StringComparison.OrdinalIgnoreCase) && p.PreviewAction == OctopusImportPreviewAction.AlreadyImported);
}
