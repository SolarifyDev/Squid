namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The Squid row a still-missing Octopus secret lives in, named as a user finds it: the project or library variable set that owns the
/// variable set, or the feed. A client links to it by <see cref="OwnerKind"/> and <see cref="OwnerId"/>. Never carries a value.
/// </summary>
public class OctopusImportFillDestinationDto
{
    public const string ProjectOwner = "Project";

    public const string LibraryVariableSetOwner = "LibraryVariableSet";

    public const string FeedOwner = "Feed";

    /// <summary><c>Project</c>, <c>LibraryVariableSet</c> or <c>Feed</c>.</summary>
    public string OwnerKind { get; set; }

    /// <summary>The Squid id of the project, library variable set or feed.</summary>
    public int OwnerId { get; set; }

    public string OwnerName { get; set; }

    /// <summary>The Squid variable set holding the variable; null for a feed.</summary>
    public int? VariableSetId { get; set; }
}
