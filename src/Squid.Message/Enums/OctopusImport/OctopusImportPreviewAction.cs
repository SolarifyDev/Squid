namespace Squid.Message.Enums.OctopusImport;

public enum OctopusImportPreviewAction
{
    Create,
    ReuseExisting,
    RenameRequired,
    Skip,
    Unsupported,
    Blocked,

    // Append-only: the SquidWeb mirror is ordinal.
    AlreadyImported = 6,
    Update = 7
}
