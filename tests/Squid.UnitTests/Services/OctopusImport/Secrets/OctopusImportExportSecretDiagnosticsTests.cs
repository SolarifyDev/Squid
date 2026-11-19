using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

public class OctopusImportExportSecretDiagnosticsTests
{
    public static TheoryData<string, string> SecretStates => new()
    {
        { "none", null },
        { "password", null },
        { "unreadable", OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailableMessage },
        { "notretained", OctopusImportPreviewDiagnosticCodes.ExportPasswordNotRetainedMessage }
    };

    [Theory]
    [MemberData(nameof(SecretStates))]
    public void Describe_ExplainsWhyTheExportPasswordIsUnavailable_OnlyWhenOneWasProvenAtUpload(string state, string expectedMessage)
    {
        var diagnostics = OctopusImportExportSecretDiagnostics.Describe(SecretsFor(state)).ToList();

        if (expectedMessage == null)
        {
            diagnostics.ShouldBeEmpty();
            return;
        }

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable);
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        diagnostic.Message.ShouldBe(expectedMessage);
    }

    [Fact]
    public void NotRetainedMessage_NeverTellsTheOperatorToRestoreAKeyThatNeverExisted()
    {
        // No envelope was written when the password was not retained, so "restore the original master key"
        // cannot help; the only remedy is re-uploading once a key is configured (or importing without it).
        OctopusImportPreviewDiagnosticCodes.ExportPasswordNotRetainedMessage.ShouldNotContain("Restore the original");
        OctopusImportPreviewDiagnosticCodes.ExportPasswordNotRetainedMessage.ShouldContain("upload the export again");
    }

    private static OctopusExportSecrets SecretsFor(string state) => state switch
    {
        "password" => OctopusExportSecrets.FromPassword("pw"),
        "unreadable" => OctopusExportSecrets.Unreadable,
        "notretained" => OctopusExportSecrets.NotRetained,
        _ => OctopusExportSecrets.None
    };
}
