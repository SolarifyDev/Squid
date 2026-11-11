using System.Security.Cryptography;
using System.Text.Json;
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

public sealed class OctopusExportPasswordValidator(
    IOctopusArchiveExtractor archiveExtractor,
    IOctopusInputExtractor inputExtractor) : IOctopusExportPasswordValidator
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

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
            var encryptedValues = extraction.Documents
                .SelectMany(document => FindEncryptedValues(document.Root))
                .ToList();

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

    private async Task<OctopusInputExtractionResult> ExtractInputAsync(
        Stream content,
        string sourcePath,
        CancellationToken ct)
    {
        if (!await LooksLikeZipAsync(content, ct).ConfigureAwait(false))
        {
            return await inputExtractor
                .ExtractStandaloneJsonAsync(content, sourcePath, ct: ct)
                .ConfigureAwait(false);
        }

        try
        {
            var archive = await archiveExtractor
                .ExtractZipAsync(content, ct: ct)
                .ConfigureAwait(false);

            return await inputExtractor
                .ExtractJsonEntriesAsync(archive.Entries, ct)
                .ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException)
        {
            // Archive shape validation remains owned by the existing extract endpoint.
            // Upload password validation is best-effort when no source document can be read.
            return new OctopusInputExtractionResult([], []);
        }
    }

    private static async Task<bool> LooksLikeZipAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[ZipMagic.Length];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct).ConfigureAwait(false);
        stream.Position = 0;

        if (read < ZipMagic.Length)
            return false;

        for (var i = 0; i < ZipMagic.Length; i++)
        {
            if (header[i] != ZipMagic[i])
                return false;
        }

        return true;
    }

    private static IEnumerable<string> FindEncryptedValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var isSensitiveObject = HasStringProperty(element, "Type", "Sensitive");

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        IsEncryptedValueProperty(property.Name, isSensitiveObject) &&
                        IsEncryptedValue(property.Value.GetString()))
                    {
                        yield return property.Value.GetString();
                    }

                    foreach (var nestedValue in FindEncryptedValues(property.Value))
                        yield return nestedValue;
                }

                yield break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nestedValue in FindEncryptedValues(item))
                        yield return nestedValue;
                }

                yield break;
        }
    }

    private static bool IsEncryptedValueProperty(string propertyName, bool isSensitiveObject)
        => isSensitiveObject && string.Equals(propertyName, "Value", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "Password", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "SensitiveValue", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "Secret", StringComparison.OrdinalIgnoreCase);

    private static bool HasStringProperty(JsonElement element, string propertyName, string expectedValue)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                string.Equals(property.Value.GetString(), expectedValue, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsEncryptedValue(string value) => OctopusExportCipher.IsEncryptedValue(value);

    private static bool TryDecrypt(string encryptedValue, byte[] key)
    {
        // Validate the plaintext bytes without materializing the decrypted value as a managed string.
        if (!OctopusExportCipher.TryDecryptBytes(encryptedValue, key, out var plaintext)) return false;

        CryptographicOperations.ZeroMemory(plaintext);

        return true;
    }
}
