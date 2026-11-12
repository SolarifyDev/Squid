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
    Updated = 7
}
