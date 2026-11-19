namespace Squid.Core.Services.OctopusImport.Mapping;

/// <summary>
/// Single source of truth for reading Octopus multi-value reference properties such as
/// <c>Octopus.Action.TargetRoles</c> and <c>Octopus.Action.Manual.ResponsibleTeamIds</c>.
/// The graph builder (which drives preview blockers) and every confirmation mapper must split
/// the same value identically, otherwise preview and confirmation disagree about what is referenced.
/// </summary>
public static class OctopusReferenceList
{
    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    public static IReadOnlyList<string> Split(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        return value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
