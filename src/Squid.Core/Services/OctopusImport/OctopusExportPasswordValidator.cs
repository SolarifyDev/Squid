using System.Security.Cryptography;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;

namespace Squid.Core.Services.OctopusImport;

public enum OctopusExportPasswordValidationStatus
{
    Valid,
    Invalid,
    NotVerifiable
}

public sealed record OctopusExportPasswordValidationResult(
    OctopusExportPasswordValidationStatus Status)
{
    public bool IsAccepted => Status != OctopusExportPasswordValidationStatus.Invalid;
}

public interface IOctopusExportPasswordValidator : IScopedDependency
{
    Task<OctopusExportPasswordValidationResult> ValidateAsync(
        Stream content,
        string sourcePath,
        string password,
        CancellationToken ct = default);
}

public sealed class OctopusExportPasswordValidator(IOctopusImportSourceReader sourceReader, IOctopusManifestInventoryBuilder inventoryBuilder, IOctopusResourceGraphBuilder graphBuilder) : IOctopusExportPasswordValidator
{
    public async Task<OctopusExportPasswordValidationResult> ValidateAsync(
        Stream content,
        string sourcePath,
        string password,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrWhiteSpace(password))
            return new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.Invalid);

        if (!content.CanSeek)
            throw new ArgumentException("Octopus export password validation requires a seekable stream.", nameof(content));

        var originalPosition = content.Position;

        try
        {
            content.Position = 0;
            var extraction = await ExtractInputAsync(content, sourcePath, ct).ConfigureAwait(false);
            var encryptedValues = FindImportedEncryptedValues(extraction);

            if (encryptedValues.Count == 0)
            {
                // Masked values and exports without secrets have no cryptographic evidence
                // with which to distinguish a password. Preserve the existing upload flow.
                return new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.NotVerifiable);
            }

            var key = OctopusExportCipher.DeriveKey(password);

            try
            {
                foreach (var encryptedValue in encryptedValues)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!TryDecrypt(encryptedValue, key))
                        return new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.Invalid);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return new OctopusExportPasswordValidationResult(OctopusExportPasswordValidationStatus.Valid);
        }
        finally
        {
            content.Position = originalPosition;
        }
    }

    private async Task<OctopusInputExtractionResult> ExtractInputAsync(Stream content, string sourcePath, CancellationToken ct)
    {
        try
        {
            return await sourceReader.ReadStreamAsync(content, sourcePath, ct).ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException)
        {
            // Archive shape validation remains owned by the existing extract endpoint.
            // Upload password validation is best-effort when no source document can be read.
            return new OctopusInputExtractionResult([], []);
        }
    }

    // Evidence = exactly the ciphertext the importer decrypts, read off the same graph the import plans from.
    private List<string> FindImportedEncryptedValues(OctopusInputExtractionResult extraction)
    {
        var inventory = inventoryBuilder.Build(extraction);
        var graph = graphBuilder.Build(inventory.HasManifest ? inventory : StandaloneInventory(extraction));

        return OctopusImportExportPasswordEvidence.EncryptedValues(graph).ToList();
    }

    // Without a manifest the importer plans nothing (it reports manifest_missing), but the password is still
    // checked against what it would decrypt from each document, classified the way the extractor classified it.
    private static OctopusManifestInventoryResult StandaloneInventory(OctopusInputExtractionResult extraction)
        => new(null, extraction.Documents.Select(StandaloneItem).ToList(), [], []);

    private static OctopusManifestInventoryItem StandaloneItem(OctopusExtractedJsonDocument document)
        => new(new OctopusManifestEntryDto { Id = document.Classification.SourceId, DocumentSource = document.SourcePath }, document, document.Classification, true);

    private static bool TryDecrypt(string encryptedValue, byte[] key)
    {
        // Validate the plaintext bytes without materializing the decrypted value as a managed string.
        if (!OctopusExportCipher.TryDecryptBytes(encryptedValue, key, out var plaintext)) return false;

        CryptographicOperations.ZeroMemory(plaintext);

        return true;
    }
}
