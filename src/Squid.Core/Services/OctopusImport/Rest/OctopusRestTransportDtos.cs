using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>One page of an Octopus collection resource (<c>?skip=&amp;take=</c>).</summary>
public class OctopusRestPage<T>
{
    public List<T> Items { get; set; } = [];

    public int TotalResults { get; set; }

    public int ItemsPerPage { get; set; }
}

/// <summary>The Octopus <c>/api</c> root document.</summary>
public class OctopusRestRootDto
{
    public string Application { get; set; }

    public string Version { get; set; }

    public string InstallationId { get; set; }

    public Dictionary<string, string> Links { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];

    /// <summary>Octopus added spaces in 2019.1; an older server has no <c>Spaces</c> link and unprefixed paths.</summary>
    [JsonIgnore]
    public bool Spaceless => Links == null || !Links.ContainsKey("Spaces");

    [JsonIgnore]
    public bool IsOctopus => !string.IsNullOrWhiteSpace(Application) && !string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(InstallationId);
}

public class OctopusRestSpaceDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public bool IsDefault { get; set; }
}

/// <summary>The <c>users/me</c> resource: the Octopus user an API key belongs to.</summary>
public class OctopusRestUserDto
{
    public string Id { get; set; }

    public bool IsService { get; set; }
}

/// <summary>The <c>tasks/{id}/details</c> resource: the task summary and its activity tree.</summary>
public class OctopusRestTaskDetailsDto
{
    public OctopusServerTaskDto Task { get; set; }

    public List<OctopusActivityLogDto> ActivityLogs { get; set; } = [];

    public OctopusActivityLogDto ActivityLog { get; set; }
}

/// <summary>The <c>users/{id}/permissions</c> resource: every space permission the user holds, each with the grants that give it.</summary>
public class OctopusRestUserPermissionsDto
{
    public Dictionary<string, List<OctopusRestPermissionGrantDto>> SpacePermissions { get; set; } = [];

    /// <summary>False when Octopus could not work the full set out; older servers omit it.</summary>
    public bool? IsPermissionsComplete { get; set; }
}

/// <summary>One grant of a permission: the space it applies in and what it is limited to (ids, or the <c>{dimension}-all</c> / <c>{dimension}-unrelated</c> sentinels).</summary>
public class OctopusRestPermissionGrantDto
{
    public string SpaceId { get; set; }

    public List<string> RestrictedToProjectIds { get; set; }

    public List<string> RestrictedToEnvironmentIds { get; set; }

    public List<string> RestrictedToTenantIds { get; set; }

    public List<string> RestrictedToProjectGroupIds { get; set; }
}
