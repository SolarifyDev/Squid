using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportRedactionTests
{
    [Fact]
    public void RedactDto_RemovesSensitiveVariableValuesButPreservesStructure()
    {
        var variableSet = new OctopusVariableSetDto
        {
            Id = "variableset-Projects-1",
            OwnerId = "Projects-1",
            Variables =
            [
                new OctopusVariableDto
                {
                    Id = "Variables-Secret",
                    Name = "ApiKey",
                    Type = "Sensitive",
                    IsSensitive = true,
                    Value = "super-secret-variable-value",
                    Scope = { ["Environment"] = ["Environments-1"] }
                },
                new OctopusVariableDto
                {
                    Id = "Variables-Plain",
                    Name = "Namespace",
                    Type = "String",
                    Value = "next-chat"
                }
            ]
        };

        var redacted = OctopusImportRedaction.RedactDto(variableSet);

        redacted.Id.ShouldBe(variableSet.Id);
        redacted.Variables[0].Name.ShouldBe("ApiKey");
        redacted.Variables[0].Value.ShouldBe(OctopusImportRedaction.RedactedValue);
        redacted.Variables[0].Scope["Environment"].Single().ShouldBe("Environments-1");
        redacted.Variables[1].Value.ShouldBe("next-chat");
        Serialized(redacted).ShouldNotContain("super-secret-variable-value");
    }

    [Fact]
    public void RedactDto_RemovesFeedCredentials()
    {
        var feed = new OctopusFeedDto
        {
            Id = "Feeds-1",
            Name = "Docker",
            FeedUri = "https://registry.example",
            Username = "feed-user",
            Password = "feed-password-secret"
        };

        var redacted = OctopusImportRedaction.RedactDto(feed);

        redacted.FeedUri.ShouldBe("https://registry.example");
        redacted.Username.ShouldBe(OctopusImportRedaction.RedactedValue);
        redacted.Password.ShouldBe(OctopusImportRedaction.RedactedValue);
        Serialized(redacted).ShouldNotContain("feed-user");
        Serialized(redacted).ShouldNotContain("feed-password-secret");
    }

    [Fact]
    public void RedactDto_RemovesAccountCredentialsCertificateMaterialAndEndpointSecrets()
    {
        var account = new OctopusAccountDto
        {
            Id = "Accounts-1",
            Name = "AWS",
            Credentials = Json("""
            {
              "AccessKey": "AKIA-SOURCE",
              "SecretKey": "aws-source-secret"
            }
            """)
        };
        var certificate = new OctopusCertificateDto
        {
            Id = "Certificates-1",
            Name = "TLS",
            HasPrivateKey = true,
            CertificateData = Json("""
            {
              "HasPrivateKey": true,
              "PrivateKey": "private-key-material",
              "Password": "pfx-password"
            }
            """)
        };
        var machine = new OctopusMachineDto
        {
            Id = "Machines-1",
            Name = "worker",
            Endpoint = Json("""
            {
              "Uri": "https://worker.example",
              "Thumbprint": "non-secret-thumbprint",
              "BearerToken": "endpoint-token-value"
            }
            """)
        };

        var accountJson = Serialized(OctopusImportRedaction.RedactDto(account));
        var certificateJson = Serialized(OctopusImportRedaction.RedactDto(certificate));
        var machineJson = Serialized(OctopusImportRedaction.RedactDto(machine));

        accountJson.ShouldNotContain("AKIA-SOURCE");
        accountJson.ShouldNotContain("aws-source-secret");
        certificateJson.ShouldContain("Certificates-1");
        certificateJson.ShouldNotContain("private-key-material");
        certificateJson.ShouldNotContain("pfx-password");
        machineJson.ShouldContain("https://worker.example");
        machineJson.ShouldNotContain("endpoint-token-value");
    }

    [Fact]
    public void RedactProperties_RemovesSuspiciousActionPropertyValues()
    {
        var redacted = OctopusImportRedaction.RedactProperties(new Dictionary<string, string>
        {
            ["Octopus.Action.Custom.Password"] = "literal-password",
            ["Octopus.Action.Custom.Url"] = "https://service.example",
            ["Octopus.Action.Custom.Header"] = "Authorization=Bearer source-token"
        });

        redacted["Octopus.Action.Custom.Password"].ShouldBe(OctopusImportRedaction.RedactedValue);
        redacted["Octopus.Action.Custom.Url"].ShouldBe("https://service.example");
        redacted["Octopus.Action.Custom.Header"].ShouldBe(OctopusImportRedaction.RedactedValue);
    }

    [Fact]
    public void RedactDiagnostic_RemovesSensitiveDiagnosticAndLogText()
    {
        var diagnostic = new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Warning,
            Code = OctopusImportRedactionDiagnosticCodes.SuspiciousPropertyValueRedacted,
            Message = "Import failed with Authorization=Bearer source-token and password=plain-secret.",
            ResourceType = "DeploymentAction",
            SourceId = "Actions-1",
            ResourceName = "Rotate password secret"
        };

        var redacted = OctopusImportRedaction.RedactDiagnostic(diagnostic);
        var redactedJson = Serialized(redacted);

        redactedJson.ShouldNotContain("source-token");
        redactedJson.ShouldNotContain("plain-secret");
        redacted.ResourceName.ShouldBe(OctopusImportRedaction.RedactedValue);
        redacted.SourceId.ShouldBe("Actions-1");
    }

    [Fact]
    public void RedactJson_RemovesNestedSensitiveJsonValues()
    {
        var redacted = OctopusImportRedaction.RedactJson("""
        {
          "Variables": [
            { "Name": "ApiKey", "Type": "Sensitive", "IsSensitive": true, "Value": "json-variable-secret" },
            { "Name": "Namespace", "Type": "String", "Value": "next-chat" }
          ],
          "Credentials": { "ClientSecret": "json-client-secret" },
          "Endpoint": { "Uri": "https://worker.example", "BearerToken": "json-endpoint-token" },
          "Properties": { "Octopus.Action.Custom.Secret": "json-property-secret" }
        }
        """);

        redacted.ShouldNotContain("json-variable-secret");
        redacted.ShouldNotContain("json-client-secret");
        redacted.ShouldNotContain("json-endpoint-token");
        redacted.ShouldNotContain("json-property-secret");
        redacted.ShouldContain("next-chat");
        redacted.ShouldContain("https://worker.example");
    }

    [Fact]
    public void RedactDto_PreservesRequiredInputMarkersWithoutSourceValues()
    {
        var requiredInput = OctopusImportRequiredInputBuilder.ForSensitiveVariable(
            "Variables-Secret",
            new OctopusVariableDto
            {
                Id = "Variables-Secret",
                Name = "ApiKey",
                Type = "Sensitive",
                IsSensitive = true,
                Value = "marker-source-secret",
                Scope = { ["Environment"] = ["Environments-1"] }
            });
        var preview = new OctopusImportPreviewPlanDto
        {
            RequiredInputs = [requiredInput]
        };

        var redacted = OctopusImportRedaction.RedactDto(preview);

        var redactedInput = redacted.RequiredInputs.Single();
        redactedInput.InputKey.ShouldStartWith("required-secret-input:SensitiveVariableValue:");
        redactedInput.InputKey.ShouldEndWith(":Value");
        redactedInput.SourceId.ShouldBe("Variables-Secret");
        redactedInput.Name.ShouldBe("ApiKey");
        redactedInput.HasSourceValue.ShouldBeTrue();
        Serialized(redacted).ShouldNotContain(OctopusImportRedaction.RedactedValue);
        Serialized(redacted).ShouldNotContain("marker-source-secret");
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Serialized<T>(T value)
        => JsonSerializer.Serialize(value);
}
