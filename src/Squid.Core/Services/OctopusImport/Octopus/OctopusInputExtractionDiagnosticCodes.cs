namespace Squid.Core.Services.OctopusImport.Octopus;

public static class OctopusInputExtractionDiagnosticCodes
{
    public const string FolderNotFound = "octopus.input.folder_not_found";
    public const string NoJsonDocuments = "octopus.input.no_json_documents";
    public const string FileReadFailed = "octopus.input.file_read_failed";
    public const string MalformedJson = "octopus.input.malformed_json";
    public const string UnsupportedJsonRoot = "octopus.input.unsupported_json_root";
    public const string UnrecognizedDocument = "octopus.input.unrecognized_document";
    public const string EntryCountLimitExceeded = "octopus.input.entry_count_limit_exceeded";
    public const string EntrySizeLimitExceeded = "octopus.input.entry_size_limit_exceeded";
    public const string TotalSizeLimitExceeded = "octopus.input.total_size_limit_exceeded";
}
