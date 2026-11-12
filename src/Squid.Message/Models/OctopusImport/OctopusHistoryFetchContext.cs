namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// Everything one REST history chunk needs. <see cref="Cursor"/> is advanced in place; the caller persists it.
/// <see cref="ExportProjectIds"/> are the Octopus project ids of an export uploaded into the same session.
/// </summary>
public sealed class OctopusHistoryFetchContext
{
    public string ServerUrl { get; init; }

    public string ApiKey { get; init; }

    public string SessionDirectory { get; init; }

    public OctopusImportHistoryFetchDto Cursor { get; init; } = new();

    public OctopusImportPriorMappings PriorMappings { get; init; } = OctopusImportPriorMappings.Empty;

    public IReadOnlyList<string> ExportProjectIds { get; init; } = [];
}
