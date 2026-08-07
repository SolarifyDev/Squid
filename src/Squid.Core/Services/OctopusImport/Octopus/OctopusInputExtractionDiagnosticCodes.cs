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
    public const string ManifestMissing = "octopus.manifest.missing";
    public const string MultipleManifests = "octopus.manifest.multiple";
    public const string ManifestMalformed = "octopus.manifest.malformed";
    public const string ManifestEntryMissingSource = "octopus.manifest.entry_missing_source";
    public const string ManifestDuplicateSource = "octopus.manifest.duplicate_source";
    public const string ManifestDocumentMissing = "octopus.manifest.document_missing";
    public const string ManifestHashMismatch = "octopus.manifest.hash_mismatch";
    public const string ManifestSourceIdMismatch = "octopus.manifest.source_id_mismatch";
    public const string ManifestDocumentTypeMismatch = "octopus.manifest.document_type_mismatch";
    public const string DocumentNotInManifest = "octopus.manifest.document_not_listed";
    public const string GraphDocumentMalformed = "octopus.graph.document_malformed";
    public const string GraphResourceMissingSourceId = "octopus.graph.resource_missing_source_id";
    public const string GraphDuplicateSourceId = "octopus.graph.duplicate_source_id";
    public const string DependencyCycle = "octopus.dependency.cycle";
}
