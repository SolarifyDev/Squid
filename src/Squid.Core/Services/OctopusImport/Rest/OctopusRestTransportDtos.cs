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

public class OctopusRestMachineDto
{
    public string Id { get; set; }

    public string Name { get; set; }
}

/// <summary>The <c>tasks/{id}/details</c> resource: the task summary and its activity tree.</summary>
public class OctopusRestTaskDetailsDto
{
    public OctopusServerTaskDto Task { get; set; }

    public List<OctopusActivityLogDto> ActivityLogs { get; set; } = [];

    public OctopusActivityLogDto ActivityLog { get; set; }
}
