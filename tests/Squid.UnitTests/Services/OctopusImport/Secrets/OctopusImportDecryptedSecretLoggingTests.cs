using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// With export secrets, decrypted plaintext now travels through mediator commands. The mediator
/// logging middleware serialises every command and redacts it with OctopusImportRedaction.RedactJson;
/// these tests pin that the plaintext never survives that redaction (the same options the logger uses).
/// </summary>
public class OctopusImportDecryptedSecretLoggingTests
{
    private static readonly JsonSerializerOptions LoggerJsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReferenceHandler = ReferenceHandler.IgnoreCycles, MaxDepth = 16 };

    [Fact]
    public void CreateVariableSetCommand_WithDecryptedSensitiveValue_IsRedactedForLogging()
    {
        var command = new CreateVariableSetCommand
        {
            Name = "Smarties",
            OwnerType = VariableSetOwnerType.LibraryVariableSet,
            SpaceId = 7,
            Variables = [new VariableModel { Name = "Email:Imap:Password", Value = "decrypted-imap-pw", IsSensitive = true, Type = VariableType.Password }, new VariableModel { Name = "Plain", Value = "visible" }]
        };

        var logged = OctopusImportRedaction.RedactJson(JsonSerializer.Serialize(command, LoggerJsonOptions));

        logged.ShouldNotContain("decrypted-imap-pw");
        logged.ShouldContain("visible");
    }

    [Fact]
    public void CreateExternalFeedCommand_WithDecryptedPassword_IsRedactedForLogging()
    {
        var command = new CreateExternalFeedCommand { Name = "ACR", FeedType = "Docker Registry", FeedUri = "https://acr", Username = "robot", Password = "decrypted-registry-token", SpaceId = 7 };

        var logged = OctopusImportRedaction.RedactJson(JsonSerializer.Serialize(command, LoggerJsonOptions));

        logged.ShouldNotContain("decrypted-registry-token");
    }
}
