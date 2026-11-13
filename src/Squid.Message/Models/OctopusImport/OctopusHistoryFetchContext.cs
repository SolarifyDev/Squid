namespace Squid.Message.Models.OctopusImport;

/// <summary>Everything one Octopus server fetch chunk needs. <see cref="Cursor"/> is advanced in place; the caller persists it.</summary>
public sealed class OctopusHistoryFetchContext
{
    public string ServerUrl { get; init; }

    public string ApiKey { get; init; }

    public string SessionDirectory { get; init; }

    public OctopusImportHistoryFetchDto Cursor { get; init; } = new();

    public OctopusImportPriorMappings PriorMappings { get; init; } = OctopusImportPriorMappings.Empty;
}
