namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// What the user selected in a posted plan: one flag per choosable root row (a row the server marked with a choice and no
/// <see cref="OctopusImportChoiceDto.FollowsSourceId"/>). Only the planner asks it, and only for a row it decided itself is choosable,
/// so a flag on any other row is never read.
/// </summary>
public sealed class OctopusImportChoiceSelections
{
    private readonly IReadOnlyDictionary<string, bool> _selected;

    private OctopusImportChoiceSelections(IReadOnlyDictionary<string, bool> selected)
    {
        _selected = selected;
    }

    public static OctopusImportChoiceSelections Empty { get; } = new(new Dictionary<string, bool>());

    public static OctopusImportChoiceSelections From(OctopusImportPreviewPlanDto posted)
    {
        var roots = (posted?.Resources ?? []).Where(IsSelectedRoot).GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase);

        return new OctopusImportChoiceSelections(roots.ToDictionary(g => g.Key, g => g.First().Choice.Selected!.Value, StringComparer.OrdinalIgnoreCase));
    }

    public bool SelectedOr(string sourceId, bool fallback)
        => sourceId != null && _selected.TryGetValue(sourceId, out var selected) ? selected : fallback;

    private static bool IsSelectedRoot(OctopusImportResourceResultDto resource)
        => !string.IsNullOrWhiteSpace(resource?.SourceId) && resource.Choice is { FollowsSourceId: null, Selected: not null };
}
