using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of truth for "which ciphertext in an export does the importer decrypt with the export
/// password?" — the same graph nodes, the same DTO binding and the same sensitivity predicate the import
/// consumers use (<see cref="OctopusImportSensitiveValues"/> for sensitive variable values,
/// <see cref="OctopusImportFeedCredentials"/> for feed passwords). The upload-time password validator proves
/// the password against exactly this set, so a password is never rejected or retained because of ciphertext
/// the importer never reads (account / certificate secrets, step-template parameters, release snapshots), and a
/// value the importer decrypts is always one the upload proved.
/// </summary>
public static class OctopusImportExportPasswordEvidence
{
    public static IEnumerable<string> EncryptedValues(OctopusResourceGraph graph)
        => (graph?.Resources ?? []).Select(DecryptedValueOf).Where(OctopusExportCipher.IsEncryptedValue);

    private static string DecryptedValueOf(OctopusResourceNode resource) => resource.Kind switch
    {
        OctopusResourceKind.Variable => SensitiveValueOf(resource.GetSource<OctopusVariableDto>()),
        OctopusResourceKind.Feed => resource.GetSource<OctopusFeedDto>()?.Password,
        _ => null
    };

    private static string SensitiveValueOf(OctopusVariableDto variable) => OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable) ? variable.Value : null;
}
