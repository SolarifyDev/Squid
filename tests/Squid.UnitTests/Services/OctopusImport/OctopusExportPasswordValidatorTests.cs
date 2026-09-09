using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusExportPasswordValidatorTests
{
    private const string Password = "octopus-password";
    private const string Secret = "secret-value-that-must-not-be-returned";

    [Fact]
    public async Task ValidateAsync_WithCorrectPassword_ReturnsValidAndRestoresStreamPosition()
    {
        var encryptedValue = Encrypt(Secret, Password);
        var json = BuildJson(encryptedValue);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        stream.Position = 3;
        var sut = CreateSut();

        var result = await sut.ValidateAsync(stream, "variableset.json", Password);

        result.Status.ShouldBe(OctopusExportPasswordValidationStatus.Valid);
        result.ToString().ShouldNotContain(Secret);
        stream.Position.ShouldBe(3);
    }

    [Fact]
    public async Task ValidateAsync_WithWrongPassword_ReturnsInvalid()
    {
        var json = BuildJson(Encrypt(Secret, Password));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var sut = CreateSut();

        var result = await sut.ValidateAsync(stream, "variableset.json", "wrong-password");

        result.Status.ShouldBe(OctopusExportPasswordValidationStatus.Invalid);
    }

    [Fact]
    public async Task ValidateAsync_WithMaskedOrNoSensitiveValues_PreservesExistingUploadBehavior()
    {
        var json = BuildJson("***");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var sut = CreateSut();

        var result = await sut.ValidateAsync(stream, "variableset.json", "any-password");

        result.Status.ShouldBe(OctopusExportPasswordValidationStatus.NotVerifiable);
        result.IsAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WithZipExport_ValidatesSensitiveValueInsideArchive()
    {
        var json = BuildJson(Encrypt(Secret, Password));
        using var stream = CreateZipStream("variableset.json", json);
        var sut = CreateSut();

        var result = await sut.ValidateAsync(stream, "export.zip", Password);

        result.Status.ShouldBe(OctopusExportPasswordValidationStatus.Valid);
    }

    private static OctopusExportPasswordValidator CreateSut()
        => new(new OctopusArchiveExtractor(), new OctopusInputExtractor());

    private static string BuildJson(string value)
        => JsonSerializer.Serialize(new
        {
            Id = "variableset-1",
            Variables = new[]
            {
                new
                {
                    Type = "Sensitive",
                    Value = value
                }
            }
        });

    private static MemoryStream CreateZipStream(string entryName, string content)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open(), Encoding.UTF8))
        {
            writer.Write(content);
        }

        stream.Position = 0;
        return stream;
    }

    private static string Encrypt(string plaintext, string password)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            Encoding.UTF8.GetBytes("Octopuss"),
            1_000,
            HashAlgorithmName.SHA1,
            16);
        var iv = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;

            using var encryptor = aes.CreateEncryptor();
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

            return $"{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(iv)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
