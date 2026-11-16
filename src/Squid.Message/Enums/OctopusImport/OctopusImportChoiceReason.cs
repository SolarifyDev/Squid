namespace Squid.Message.Enums.OctopusImport;

/// <summary>Why the user decides whether a resource is imported.</summary>
public enum OctopusImportChoiceReason
{
    // Append-only: the SquidWeb mirror is ordinal.

    /// <summary>An earlier import created it and it was deleted (or renamed) in Squid since.</summary>
    DeletedInSquid = 0,

    /// <summary>It was added in Octopus after what it belongs to was imported.</summary>
    AddedInOctopus = 1
}
