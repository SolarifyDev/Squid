namespace Squid.Message.Enums.OctopusImport;

public enum OctopusImportResourceOutcomeState
{
    Pending,
    Created,
    Reused,
    Skipped,
    Unsupported,
    Blocked,
    Failed,

    // Append-only: the SquidWeb mirror is ordinal.
    Updated = 7,

    /// <summary>A secret missing in Squid was filled from the export file; nothing else of the resource was written.</summary>
    SecretFilled = 8
}
