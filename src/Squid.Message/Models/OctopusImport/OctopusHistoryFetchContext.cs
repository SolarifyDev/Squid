namespace Squid.Message.Models.OctopusImport;

/// <summary>Everything one Octopus server fetch chunk needs. <see cref="Cursor"/> is advanced in place; the caller persists it.</summary>
public sealed class OctopusHistoryFetchContext
{
    public string ServerUrl { get; init; }

    public string ApiKey { get; init; }

    /// <summary>The import session whose stored files the fetch stages into.</summary>
    public Guid SessionId { get; init; }

    /// <summary>This server's directory of the session: where the finished bundle is written before it is stored with the session.</summary>
    public string SessionDirectory { get; init; }

    public OctopusImportHistoryFetchDto Cursor { get; init; } = new();

    public OctopusImportPriorMappings PriorMappings { get; init; } = OctopusImportPriorMappings.Empty;

    /// <summary>The projects the session's selection names; null reads every project of the space.</summary>
    public IReadOnlyCollection<string> ChosenProjectIds { get; init; }
}
