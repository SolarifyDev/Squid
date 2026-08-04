namespace Squid.Core.Services.OctopusImport.Octopus;

public static class OctopusArchiveExtractionErrorCodes
{
    public const string InvalidArchive = "octopus.archive.invalid";
    public const string EntryCountLimitExceeded = "octopus.archive.entry_count_limit_exceeded";
    public const string EntryPathUnsafe = "octopus.archive.entry_path_unsafe";
    public const string DuplicateEntryPath = "octopus.archive.duplicate_entry_path";
    public const string NestedArchiveRejected = "octopus.archive.nested_archive_rejected";
    public const string EntrySizeLimitExceeded = "octopus.archive.entry_size_limit_exceeded";
    public const string TotalSizeLimitExceeded = "octopus.archive.total_size_limit_exceeded";
}
