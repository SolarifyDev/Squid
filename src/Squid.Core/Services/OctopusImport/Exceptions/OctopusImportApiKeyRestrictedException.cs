using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>An Octopus API key cannot read the whole space: an import with it would silently leave data out, so it is refused.</summary>
public class OctopusImportApiKeyRestrictedException : InvalidOperationException
{
    public OctopusImportApiKeyRestrictedException(OctopusServerPermissionsDto permissions) : base(MessageFor(permissions))
    {
        Permissions = permissions;
    }

    public OctopusServerPermissionsDto Permissions { get; }

    public static string MessageFor(OctopusServerPermissionsDto permissions)
    {
        var space = permissions.OctopusSpaceId ?? "this server";
        var parts = new List<string> { $"The Octopus API key cannot read everything in {space}, so an import with it would silently leave data out." };

        if (permissions.Restricted.Count > 0) parts.Add($"Limited to some projects, environments, tenants or project groups: {string.Join(", ", permissions.Restricted)}.");
        if (permissions.Missing.Count > 0) parts.Add($"Not granted: {string.Join(", ", permissions.Missing)}.");
        if (permissions.Incomplete) parts.Add("Octopus could not report the key's complete permissions.");

        parts.Add($"Use the API key of a dedicated read-only service account whose team grants these permissions in {space} without any project, environment, tenant or project-group restriction.");

        return string.Join(" ", parts);
    }
}
