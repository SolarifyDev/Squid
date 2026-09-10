using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

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
    private const int KeySizeBytes = 16;
    private const int IvSizeBytes = 16;
    private const int Pbkdf2Iterations = 1_000;
    private const string PasswordSalt = "Octopuss";
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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

            var key = DeriveKey(password);

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

    private static bool IsEncryptedValue(string value)
    {
        if (!TryGetEncryptedParts(value, out var ciphertext, out var iv))
            return false;

        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(iv);
        return true;
    }

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

    private static byte[] DeriveKey(string password)
        => Rfc2898DeriveBytes.Pbkdf2(
            password,
            Encoding.UTF8.GetBytes(PasswordSalt),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA1,
            KeySizeBytes);

    private static bool TryDecrypt(string encryptedValue, byte[] key)
    {
        if (!TryGetEncryptedParts(encryptedValue, out var ciphertext, out var iv))
            return false;

        byte[] plaintext = null;

        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

            // Octopus stores sensitive values as UTF-8 strings. Validate the bytes without
            // materializing the decrypted value as a managed string.
            return IsValidUtf8(plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        finally
        {
            if (plaintext != null)
                CryptographicOperations.ZeroMemory(plaintext);

            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        var decoder = StrictUtf8.GetDecoder();
        Span<char> chars = stackalloc char[256];
        var offset = 0;

        while (offset < bytes.Length)
        {
            decoder.Convert(
                bytes.AsSpan(offset),
                chars,
                flush: false,
                out var bytesUsed,
                out _,
                out _);

            if (bytesUsed == 0)
                return false;

            offset += bytesUsed;
        }

        decoder.Convert(
            ReadOnlySpan<byte>.Empty,
            chars,
            flush: true,
            out _,
            out _,
            out _);

        return true;
    }

    private static bool TryGetEncryptedParts(
        string encryptedValue,
        out byte[] ciphertext,
        out byte[] iv)
    {
        ciphertext = null;
        iv = null;

        if (string.IsNullOrWhiteSpace(encryptedValue))
            return false;

        var separatorIndex = encryptedValue.IndexOf('|');

        if (separatorIndex <= 0 || separatorIndex != encryptedValue.LastIndexOf('|'))
            return false;

        try
        {
            ciphertext = Convert.FromBase64String(encryptedValue[..separatorIndex]);
            iv = Convert.FromBase64String(encryptedValue[(separatorIndex + 1)..]);
        }
        catch (FormatException)
        {
            if (ciphertext != null)
                CryptographicOperations.ZeroMemory(ciphertext);
            if (iv != null)
                CryptographicOperations.ZeroMemory(iv);

            ciphertext = null;
            iv = null;
            return false;
        }

        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0 || iv.Length != IvSizeBytes)
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(iv);
            ciphertext = null;
            iv = null;
            return false;
        }

        return true;
    }
}
