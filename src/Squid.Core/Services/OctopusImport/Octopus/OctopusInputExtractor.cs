using System.Security.Cryptography;
using System.Text.Json;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Octopus;

public interface IOctopusInputExtractor : IScopedDependency
{
    Task<OctopusInputExtractionResult> ExtractStandaloneJsonAsync(
        Stream jsonStream,
        string sourcePath,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default);

    Task<OctopusInputExtractionResult> ExtractFolderAsync(
        string folderPath,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default);

    Task<OctopusInputExtractionResult> ExtractJsonEntriesAsync(
        IReadOnlyList<OctopusExtractedArchiveEntry> entries,
        CancellationToken ct = default);
}

public class OctopusInputExtractor : IOctopusInputExtractor
{
    private const int CopyBufferSize = 81920;

    public async Task<OctopusInputExtractionResult> ExtractStandaloneJsonAsync(
        Stream jsonStream,
        string sourcePath,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jsonStream);

        options ??= OctopusArchiveExtractionOptions.Default;
        options.EnsureValid();

        var diagnostics = new List<OctopusInputExtractionDiagnostic>();
        var documents = new List<OctopusExtractedJsonDocument>();
        var safeSourcePath = NormalizeDisplayPath(sourcePath);
        var content = await ReadStreamWithLimitAsync(jsonStream, safeSourcePath, options.MaxEntrySizeBytes, diagnostics, ct).ConfigureAwait(false);

        if (content != null)
            AddParsedDocument(safeSourcePath, content, documents, diagnostics);

        return new OctopusInputExtractionResult(documents, diagnostics);
    }

    public async Task<OctopusInputExtractionResult> ExtractFolderAsync(
        string folderPath,
        OctopusArchiveExtractionOptions options = null,
        CancellationToken ct = default)
    {
        options ??= OctopusArchiveExtractionOptions.Default;
        options.EnsureValid();

        var diagnostics = new List<OctopusInputExtractionDiagnostic>();
        var documents = new List<OctopusExtractedJsonDocument>();

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.FolderNotFound,
                "Octopus import folder input was not found.",
                NormalizeDisplayPath(folderPath)));

            return new OctopusInputExtractionResult(documents, diagnostics);
        }

        var root = Path.GetFullPath(folderPath);
        var jsonFiles = EnumerateJsonFiles(root, diagnostics);

        if (jsonFiles.Count == 0)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.NoJsonDocuments,
                "Octopus import folder input does not contain JSON documents.",
                NormalizeDisplayPath(folderPath)));

            return new OctopusInputExtractionResult(documents, diagnostics);
        }

        if (jsonFiles.Count > options.MaxEntryCount)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.EntryCountLimitExceeded,
                $"Octopus import folder contains {jsonFiles.Count} JSON documents, exceeding the configured limit of {options.MaxEntryCount}.",
                NormalizeDisplayPath(folderPath)));

            return new OctopusInputExtractionResult(documents, diagnostics);
        }

        long totalSizeBytes = 0;

        foreach (var filePath in jsonFiles)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = NormalizeFolderRelativePath(root, filePath);
            var content = await ReadFileWithLimitsAsync(filePath, relativePath, options.MaxEntrySizeBytes, diagnostics, ct).ConfigureAwait(false);

            if (content == null)
                continue;

            totalSizeBytes = checked(totalSizeBytes + content.LongLength);

            if (totalSizeBytes > options.MaxTotalUncompressedSizeBytes)
            {
                diagnostics.Add(Blocker(
                    OctopusInputExtractionDiagnosticCodes.TotalSizeLimitExceeded,
                    $"Octopus import folder JSON content exceeds the configured total size limit of {options.MaxTotalUncompressedSizeBytes} bytes.",
                    relativePath));

                break;
            }

            AddParsedDocument(relativePath, content, documents, diagnostics);
        }

        return new OctopusInputExtractionResult(documents, diagnostics);
    }

    public Task<OctopusInputExtractionResult> ExtractJsonEntriesAsync(
        IReadOnlyList<OctopusExtractedArchiveEntry> entries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var diagnostics = new List<OctopusInputExtractionDiagnostic>();
        var documents = new List<OctopusExtractedJsonDocument>();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!HasJsonExtension(entry.RelativePath))
                continue;

            AddParsedDocument(entry.RelativePath, entry.Content, documents, diagnostics);
        }

        return Task.FromResult(new OctopusInputExtractionResult(documents, diagnostics));
    }

    private static List<string> EnumerateJsonFiles(string root, List<OctopusInputExtractionDiagnostic> diagnostics)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.FileReadFailed,
                $"Octopus import folder JSON documents could not be enumerated ({ex.GetType().Name}).",
                NormalizeDisplayPath(root)));

            return [];
        }
    }

    private static async Task<byte[]> ReadFileWithLimitsAsync(
        string filePath,
        string sourcePath,
        long maxEntrySizeBytes,
        List<OctopusInputExtractionDiagnostic> diagnostics,
        CancellationToken ct)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);

            if (fileInfo.Length > maxEntrySizeBytes)
            {
                diagnostics.Add(Blocker(
                    OctopusInputExtractionDiagnosticCodes.EntrySizeLimitExceeded,
                    $"Octopus import JSON document '{sourcePath}' exceeds the configured per-entry size limit of {maxEntrySizeBytes} bytes.",
                    sourcePath));

                return null;
            }

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
            return await ReadStreamWithLimitAsync(stream, sourcePath, maxEntrySizeBytes, diagnostics, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.FileReadFailed,
                $"Octopus import JSON document '{sourcePath}' could not be read ({ex.GetType().Name}).",
                sourcePath));

            return null;
        }
    }

    private static async Task<byte[]> ReadStreamWithLimitAsync(
        Stream stream,
        string sourcePath,
        long maxEntrySizeBytes,
        List<OctopusInputExtractionDiagnostic> diagnostics,
        CancellationToken ct)
    {
        if (stream.CanSeek && stream.Length > maxEntrySizeBytes)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.EntrySizeLimitExceeded,
                $"Octopus import JSON document '{sourcePath}' exceeds the configured per-entry size limit of {maxEntrySizeBytes} bytes.",
                sourcePath));

            return null;
        }

        await using var memoryStream = new MemoryStream();
        var buffer = new byte[CopyBufferSize];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);

            if (read == 0)
                break;

            if (memoryStream.Length + read > maxEntrySizeBytes)
            {
                diagnostics.Add(Blocker(
                    OctopusInputExtractionDiagnosticCodes.EntrySizeLimitExceeded,
                    $"Octopus import JSON document '{sourcePath}' exceeds the configured per-entry size limit of {maxEntrySizeBytes} bytes.",
                    sourcePath));

                return null;
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return memoryStream.ToArray();
    }

    private static void AddParsedDocument(
        string sourcePath,
        byte[] content,
        List<OctopusExtractedJsonDocument> documents,
        List<OctopusInputExtractionDiagnostic> diagnostics)
    {
        try
        {
            using var jsonDocument = JsonDocument.Parse(content);
            var root = jsonDocument.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Blocker(
                    OctopusInputExtractionDiagnosticCodes.UnsupportedJsonRoot,
                    $"Octopus import JSON document '{sourcePath}' must contain a JSON object at the root.",
                    sourcePath));

                return;
            }

            var classification = ClassifyJson(sourcePath, root);

            if (!classification.IsKnown)
            {
                diagnostics.Add(Warning(
                    OctopusInputExtractionDiagnosticCodes.UnrecognizedDocument,
                    $"Octopus import JSON document '{sourcePath}' is not a recognized Octopus export document.",
                    sourcePath,
                    classification.SourceId,
                    classification.Kind));
            }

            documents.Add(new OctopusExtractedJsonDocument(sourcePath, classification, root.Clone(), content.LongLength, ComputeSha1(content)));
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.MalformedJson,
                BuildMalformedJsonMessage(sourcePath, ex),
                sourcePath));
        }
    }

    private static OctopusDocumentClassification ClassifyJson(string sourcePath, JsonElement root)
    {
        if (TryGetProperty(root, "Entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            return new OctopusDocumentClassification(
                OctopusDocumentKind.Manifest,
                sourcePath,
                null,
                null,
                false,
                false);
        }

        var id = TryGetStringProperty(root, "Id");
        var documentType = TryGetStringProperty(root, "DocumentType");

        return OctopusDocumentClassifier.ClassifyJsonDocument(sourcePath, id, documentType);
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string TryGetStringProperty(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string BuildMalformedJsonMessage(string sourcePath, JsonException ex)
    {
        if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
            return $"Octopus import JSON document '{sourcePath}' is malformed at line {ex.LineNumber.Value}, byte {ex.BytePositionInLine.Value}.";

        return $"Octopus import JSON document '{sourcePath}' is malformed.";
    }

    private static string NormalizeFolderRelativePath(string root, string filePath)
    {
        var relativePath = Path.GetRelativePath(root, filePath);
        return NormalizeDisplayPath(relativePath);
    }

    private static string NormalizeDisplayPath(string path)
        => string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/');

    private static bool HasJsonExtension(string sourcePath)
        => string.Equals(Path.GetExtension(sourcePath), ".json", StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha1(byte[] content)
        => Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();

    private static OctopusInputExtractionDiagnostic Warning(
        string code,
        string message,
        string sourcePath,
        string sourceId = null,
        OctopusDocumentKind? documentKind = null)
        => new(OctopusImportCompatibilitySeverity.Warning, code, message, sourcePath, sourceId, documentKind);

    private static OctopusInputExtractionDiagnostic Blocker(string code, string message, string sourcePath)
        => new(OctopusImportCompatibilitySeverity.Blocker, code, message, sourcePath);
}
