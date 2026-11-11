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

    // ── Upload-time evidence ⇔ import-time sensitivity (one predicate) ─────────────────────────

    [Theory]
    [InlineData(true, "String", true)]
    [InlineData(true, "Sensitive", true)]
    [InlineData(false, "Sensitive", true)]
    [InlineData(false, " sensitive ", true)]
    [InlineData(true, null, true)]
    [InlineData(false, "String", false)]
    [InlineData(false, null, false)]
    public async Task ValidateAsync_EncryptedVariableValue_CountsAsEvidenceExactlyWhenTheImporterTreatsItAsSensitive(bool isSensitive, string type, bool expectedSensitive)
    {
        var variable = VariableJson(isSensitive, type, Encrypt(Secret, Password));
        var json = $"{{\"Id\":\"variableset-1\",\"Variables\":[{variable}]}}";

        var importerTreatsAsSensitive = OctopusImportRequiredInputBuilder.IsSensitiveVariable(JsonSerializer.Deserialize<OctopusVariableDto>(variable, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

        importerTreatsAsSensitive.ShouldBe(expectedSensitive);
        (await ValidateJsonAsync(json, Password)).ShouldBe(expectedSensitive ? OctopusExportPasswordValidationStatus.Valid : OctopusExportPasswordValidationStatus.NotVerifiable, customMessage: "a value the importer will decrypt must be proven at upload, otherwise its password is never retained");
        (await ValidateJsonAsync(json, "wrong-password")).ShouldBe(expectedSensitive ? OctopusExportPasswordValidationStatus.Invalid : OctopusExportPasswordValidationStatus.NotVerifiable);
    }

    [Fact]
    public async Task ValidateAsync_FeedPasswordIsTheOnlySecret_IsVerified()
    {
        var json = JsonSerializer.Serialize(new { Id = "Feeds-1", Name = "ACR", FeedType = "Docker", Username = "robot", Password = Encrypt("registry-token", Password) });

        (await ValidateJsonAsync(json, Password)).ShouldBe(OctopusExportPasswordValidationStatus.Valid);
        (await ValidateJsonAsync(json, "wrong-password")).ShouldBe(OctopusExportPasswordValidationStatus.Invalid);
    }

    [Fact]
    public async Task ValidateAsync_LibraryVariableSetIsTheOnlyPlaceWithSecrets_IsVerified()
    {
        var library = JsonSerializer.Serialize(new { Id = "LibraryVariableSets-1", Name = "Shared", VariableSetId = "variableset-LibraryVariableSets-1", ContentType = "Variables" });
        var variables = $"{{\"Id\":\"variableset-LibraryVariableSets-1\",\"OwnerId\":\"LibraryVariableSets-1\",\"Variables\":[{VariableJson(true, "String", Encrypt(Secret, Password))}]}}";
        var project = JsonSerializer.Serialize(new { Id = "variableset-Projects-1", OwnerId = "Projects-1", Variables = new[] { new { Name = "Plain", Value = "visible" } } });

        (await ValidateZipAsync(Password, ("library.json", library), ("library-variables.json", variables), ("project-variables.json", project))).ShouldBe(OctopusExportPasswordValidationStatus.Valid);
        (await ValidateZipAsync("wrong-password", ("library.json", library), ("library-variables.json", variables), ("project-variables.json", project))).ShouldBe(OctopusExportPasswordValidationStatus.Invalid);
    }

    [Fact]
    public async Task ValidateAsync_SensitiveStepTemplateParameterIsTheOnlySecret_IsNotVerifiable()
    {
        // Defined outcome: sensitive step-template parameter values are never decrypted by the importer (the
        // script mapper raises SensitiveStepTemplateParameter instead), so they are not password evidence and the
        // password is not retained for them.
        var template = JsonSerializer.Serialize(new { Id = "ActionTemplates-1", Name = "Notify", Parameters = new[] { new { Name = "ApiKey", DisplaySettings = new Dictionary<string, string> { ["Octopus.ControlType"] = "Sensitive" } } } });
        var process = JsonSerializer.Serialize(new { Id = "deploymentprocess-Projects-1", Steps = new[] { new { Actions = new[] { new { Name = "Notify", Properties = new Dictionary<string, string> { ["Octopus.Action.Template.Id"] = "ActionTemplates-1", ["ApiKey"] = Encrypt("api-key", Password) } } } } } });

        (await ValidateZipAsync(Password, ("template.json", template), ("process.json", process))).ShouldBe(OctopusExportPasswordValidationStatus.NotVerifiable);
        (await ValidateZipAsync("wrong-password", ("template.json", template), ("process.json", process))).ShouldBe(OctopusExportPasswordValidationStatus.NotVerifiable);
    }

    [Theory]
    [InlineData("password-a")]
    [InlineData("password-b")]
    [InlineData("password-c")]
    public async Task ValidateAsync_SecretsEncryptedWithTwoDifferentPasswords_IsRejectedWhicheverIsGiven(string givenPassword)
    {
        // Defined outcome: Octopus encrypts one export with one password; an archive whose secrets do not all
        // decrypt with the given password is rejected at upload (fail-closed) instead of importing half of them.
        var variables = $"{{\"Id\":\"variableset-1\",\"Variables\":[{VariableJson(false, "Sensitive", Encrypt(Secret, "password-a"))}]}}";
        var feed = JsonSerializer.Serialize(new { Id = "Feeds-1", Name = "ACR", Username = "robot", Password = Encrypt("registry-token", "password-b") });

        (await ValidateZipAsync(givenPassword, ("variables.json", variables), ("feed.json", feed))).ShouldBe(OctopusExportPasswordValidationStatus.Invalid);
    }

    [Fact]
    public async Task ValidateAsync_MaskedSensitiveValueNextToARealSecret_VerifiesOnlyTheRealSecret()
    {
        var json = $"{{\"Id\":\"variableset-1\",\"Variables\":[{VariableJson(true, "Sensitive", "********")},{VariableJson(true, "Sensitive", Encrypt(Secret, Password))}]}}";

        (await ValidateJsonAsync(json, Password)).ShouldBe(OctopusExportPasswordValidationStatus.Valid);
        (await ValidateJsonAsync(json, "wrong-password")).ShouldBe(OctopusExportPasswordValidationStatus.Invalid);
    }

    private static async Task<OctopusExportPasswordValidationStatus> ValidateJsonAsync(string json, string password)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return (await CreateSut().ValidateAsync(stream, "export.json", password)).Status;
    }

    private static async Task<OctopusExportPasswordValidationStatus> ValidateZipAsync(string password, params (string Name, string Json)[] entries)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, json) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(json);
            }
        }

        stream.Position = 0;

        using (stream)
            return (await CreateSut().ValidateAsync(stream, "export.zip", password)).Status;
    }

    private static string VariableJson(bool isSensitive, string type, string value)
    {
        var variable = new Dictionary<string, object> { ["Id"] = Guid.NewGuid().ToString(), ["Name"] = "Secret", ["IsSensitive"] = isSensitive, ["Value"] = value };

        if (type != null) variable["Type"] = type;

        return JsonSerializer.Serialize(variable);
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
