using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The one place that turns an import session's files into extracted documents: a zip or a standalone JSON
/// export, plus the REST-fetched <c>octopus-history.zip</c> bundle when one sits beside it.
/// </summary>
public interface IOctopusImportSourceReader : IScopedDependency
{
    /// <summary>Reads the session's main file and merges the history bundle in the same directory, if any.</summary>
    Task<OctopusInputExtractionResult> ReadAsync(string temporaryUploadPath, CancellationToken ct = default);

    /// <summary>Reads one zip or standalone JSON stream, with the export limits.</summary>
    Task<OctopusInputExtractionResult> ReadStreamAsync(Stream content, string sourcePath, CancellationToken ct = default);
}
