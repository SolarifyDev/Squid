using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// A resource the user decides to import or not. The server sets every field but <see cref="Selected"/>; the client sets
/// <see cref="Selected"/> on a root row (<see cref="FollowsSourceId"/> null) and posts the plan back to validate. A missing
/// <see cref="Selected"/>, a missing choice or a missing plan all mean the server default.
/// </summary>
public class OctopusImportChoiceDto
{
    public OctopusImportChoiceReason Reason { get; set; }

    /// <summary>The effective selection in a plan the server returns (always set there); the user's selection in a posted plan.</summary>
    public bool? Selected { get; set; }

    public bool DefaultSelected { get; set; }

    /// <summary>Set on a row that follows a choosable owner (an action of a choosable step): it has no choice of its own.</summary>
    public string FollowsSourceId { get; set; }

    /// <summary>What the resource belongs to, for display ("Lifecycle 'Default'", "Step 'Deploy web'").</summary>
    public string OwnerName { get; set; }
}
