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

        return _ctx.ValidatedPlan.PreviewPlan.Resources
            .Where(r => r.PreviewAction == OctopusImportPreviewAction.Create)
            .Select(r => ResolvePermission(r, currentResources))
            .Where(permission => permission.HasValue)
            .Select(permission => permission.Value)
            .Distinct();
    }

    private static Permission? ResolvePermission(OctopusImportResourceResultDto previewResource, IReadOnlyDictionary<string, OctopusResourceNode> currentResources)
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
            OctopusResourceKind.Channel => ResolveChannelPermission(resource),
            OctopusResourceKind.VariableSet => Permission.VariableEdit,
            OctopusResourceKind.DeploymentProcess => Permission.ProcessEdit,
            OctopusResourceKind.Release => Permission.ReleaseCreate,
            _ => null
        };
    }

    private static Permission ResolveChannelPermission(OctopusResourceNode resource)
        => resource.GetSource<OctopusChannelDto>()?.IsDefault == true ? Permission.ChannelEdit : Permission.ChannelCreate;
}
