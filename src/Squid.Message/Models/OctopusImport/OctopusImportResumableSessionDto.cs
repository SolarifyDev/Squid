using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// An import session of the current user that can be continued after the page was reloaded. Display data only: it never carries the API
/// key, the key's fingerprint, the Octopus user, a temporary path, the export password or the plan.
/// </summary>
public class OctopusImportResumableSessionDto
{
    public Guid SessionId { get; set; }

    public OctopusImportSessionState State { get; set; }

    public OctopusImportResumeStep ResumeStep { get; set; }

    public OctopusImportSourceKind SourceKind { get; set; }

    /// <summary>The export file's name; null for a read from Octopus.</summary>
    public string FileName { get; set; }

    /// <summary>The Octopus server the read uses (scheme, host and path, nothing else); null for an export file or a session read before it was recorded.</summary>
    public string ServerUrl { get; set; }

    public string ServerHost { get; set; }

    public string OctopusSpaceId { get; set; }

    /// <summary>How many projects were chosen; null imports the whole space.</summary>
    public int? SelectedProjectCount { get; set; }

    /// <summary>Up to five of the chosen projects' names, recorded while reading; display only.</summary>
    public List<string> SelectedProjectNames { get; set; } = [];

    /// <summary>The read's progress; null for an export file.</summary>
    public OctopusHistoryFetchProgressDto ReadProgress { get; set; }

    /// <summary>A chunk of the read still holds the session (another tab, or a read interrupted moments ago).</summary>
    public bool ReadActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset LastStateChangedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset MaxExpiresAt { get; set; }
}
