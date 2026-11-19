using System.Security.Cryptography;
using System.Text;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Fingerprint of a team's member set. Reusing a team grants manual-intervention approval to its members, so the
/// preview records who they are and confirmation compares it: a member swapped for another keeps the count but
/// changes the fingerprint. Only the set matters (order and duplicates are ignored); user ids are hashed so the
/// preview plan never carries them.
/// </summary>
public static class OctopusImportTeamMembership
{
    public static string Fingerprint(IEnumerable<int> userIds)
    {
        var canonical = string.Join(",", userIds.Distinct().Order());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
