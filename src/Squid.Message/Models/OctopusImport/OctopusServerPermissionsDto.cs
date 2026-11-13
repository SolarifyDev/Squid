namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// Whether an Octopus API key can read a whole space. A permission is <see cref="Restricted"/> when every grant of it in
/// the space is limited to some projects, environments, tenants or project groups, and <see cref="Missing"/> when no grant
/// exists. An import with such a key would silently leave data out, so it is refused unless <see cref="Sufficient"/>.
/// </summary>
public class OctopusServerPermissionsDto
{
    public string OctopusSpaceId { get; set; }

    public bool Sufficient { get; set; }

    public List<string> Restricted { get; set; } = [];

    public List<string> Missing { get; set; } = [];

    /// <summary>Octopus could not report the key's complete permission set (or refused to report it), so it cannot be proven sufficient.</summary>
    public bool Incomplete { get; set; }
}
