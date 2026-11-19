using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

public class OctopusImportSecretSeverityTests
{
    public static TheoryData<bool, string, OctopusImportCompatibilitySeverity> Matrix => new()
    {
        { true, "server", OctopusImportCompatibilitySeverity.Warning },
        { false, "server", OctopusImportCompatibilitySeverity.Blocker },
        { true, "none", OctopusImportCompatibilitySeverity.Blocker },
        { true, "unreadable", OctopusImportCompatibilitySeverity.Blocker },
        { true, "notRetained", OctopusImportCompatibilitySeverity.Blocker },
        { true, "password", OctopusImportCompatibilitySeverity.Blocker },
        { true, "null", OctopusImportCompatibilitySeverity.Blocker }
    };

    // Only a secret the server withheld IN a server session is reported instead of blocking.
    [Theory]
    [MemberData(nameof(Matrix))]
    public void For_OnlyWithheldInAServerSession_IsAWarning(bool withheld, string session, OctopusImportCompatibilitySeverity expected)
    {
        var secrets = Session(session);

        OctopusImportSecretSeverity.For(withheld, secrets).ShouldBe(expected);
        OctopusImportSecretSeverity.IsRequired(withheld, secrets).ShouldBe(expected == OctopusImportCompatibilitySeverity.Blocker);
        OctopusImportSecretSeverity.IsWithheldByServer(withheld, secrets).ShouldBe(expected == OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public void WithheldByServer_HasNoKeyAndSaysWhy()
    {
        OctopusExportSecrets.WithheldByServer.IsAvailable.ShouldBeFalse();
        OctopusExportSecrets.WithheldByServer.IsUnreadable.ShouldBeFalse();
        OctopusExportSecrets.WithheldByServer.IsNotRetained.ShouldBeFalse();
        OctopusExportSecrets.WithheldByServer.ToString().ShouldBe("OctopusExportSecrets(withheld by server)");
        OctopusExportSecrets.None.IsWithheldByServer.ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"Squid.Import.WithheldSecrets":["Password","Token"]}""", "Password,Token")]
    [InlineData("""{"Squid.Import.WithheldSecrets":["password"]}""", "password")]
    [InlineData("""{"Squid.Import.WithheldSecrets":"Password"}""", "")]
    [InlineData("""{"Squid.Import.WithheldSecrets":[1,null,"","Value"]}""", "Value")]
    [InlineData("""{"Other":["Password"]}""", "")]
    public void WithheldSecrets_ReadsTheMarkerArrayOnly(string json, string expected)
    {
        var extensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        string.Join(",", OctopusImportWithheldSecrets.For(extensionData).Order()).ShouldBe(expected);
        string.Join(",", OctopusImportWithheldSecrets.For(JsonDocument.Parse(json).RootElement).Order()).ShouldBe(expected);
    }

    [Fact]
    public void WithheldSecrets_IsCaseInsensitive_AndTheMarkerNameIsTheNormalisers()
    {
        OctopusImportWithheldSecrets.Property.ShouldBe(OctopusRestDocumentNormalizer.WithheldSecretsProperty);
        OctopusImportWithheldSecrets.IsWithheld(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Squid.Import.WithheldSecrets":["Value"]}"""), "VALUE").ShouldBeTrue();
        OctopusImportWithheldSecrets.For((Dictionary<string, JsonElement>)null).ShouldBeEmpty();
        OctopusImportWithheldSecrets.For((JsonElement?)null).ShouldBeEmpty();
    }

    private static OctopusExportSecrets Session(string kind)
        => kind switch
        {
            "server" => OctopusExportSecrets.WithheldByServer,
            "none" => OctopusExportSecrets.None,
            "unreadable" => OctopusExportSecrets.Unreadable,
            "notRetained" => OctopusExportSecrets.NotRetained,
            "password" => OctopusExportSecrets.FromPassword("pw"),
            _ => null
        };
}
