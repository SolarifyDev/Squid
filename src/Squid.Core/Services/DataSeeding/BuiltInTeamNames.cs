namespace Squid.Core.Services.DataSeeding;

/// <summary>
/// Names of the system-wide (SpaceId 0) built-in teams seeded by <see cref="BuiltInTeamSeeder"/>. Shared so
/// other features (e.g. the Octopus import's built-in team mapping) resolve the same rows by the same name.
/// </summary>
public static class BuiltInTeamNames
{
    public const string Administrators = "Squid Administrators";
    public const string Everyone = "Everyone";
}
