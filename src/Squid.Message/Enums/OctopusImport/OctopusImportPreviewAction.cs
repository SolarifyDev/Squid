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
    Update = 7,

    /// <summary>The resource was imported before and only its secret is written: a value the Octopus server withheld, now supplied by the export file into the still-empty Squid value.</summary>
    FillMissingSecret = 8
}
