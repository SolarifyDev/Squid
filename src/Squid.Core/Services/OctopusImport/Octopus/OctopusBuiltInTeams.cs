using Squid.Core.Services.DataSeeding;

namespace Squid.Core.Services.OctopusImport.Octopus;

/// <summary>
/// Single source of truth for Octopus's built-in teams. Octopus seeds them with fixed document ids
/// (<c>Octopus.Core.Model.Users.Team</c>), so they are recognised by id, never by display name. Teams with
/// a Squid equivalent map onto the Squid built-in team (conflict discovery, preview and confirmation all
/// go through here, so they cannot disagree); the others import as regular teams with a warning.
/// </summary>
public static class OctopusBuiltInTeams
{
    public const string EveryoneTeamId = "teams-everyone";
    public const string AdministratorsTeamId = "teams-administrators";
    public const string ManagersTeamId = "teams-managers";
    public const string SpaceManagersTeamIdPrefix = "teams-spacemanagers";

    private static readonly Dictionary<string, string> SquidEquivalents = new(StringComparer.OrdinalIgnoreCase)
    {
        [EveryoneTeamId] = BuiltInTeamNames.Everyone,
        [AdministratorsTeamId] = BuiltInTeamNames.Administrators
    };

    public static bool IsBuiltIn(string sourceId)
        => !string.IsNullOrWhiteSpace(sourceId)
           && (SquidEquivalents.ContainsKey(sourceId)
               || string.Equals(sourceId, ManagersTeamId, StringComparison.OrdinalIgnoreCase)
               || sourceId.StartsWith(SpaceManagersTeamIdPrefix, StringComparison.OrdinalIgnoreCase));

    public static bool TryGetSquidEquivalent(string sourceId, out string squidTeamName)
    {
        squidTeamName = null;

        return !string.IsNullOrWhiteSpace(sourceId) && SquidEquivalents.TryGetValue(sourceId, out squidTeamName);
    }

    public static bool IsBuiltInWithoutEquivalent(string sourceId) => IsBuiltIn(sourceId) && !TryGetSquidEquivalent(sourceId, out _);
}
