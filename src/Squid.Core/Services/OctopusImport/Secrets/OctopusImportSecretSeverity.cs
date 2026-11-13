using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// THE severity of a secret the import cannot carry, shared by every emitter (preview planner, preview/confirmation validator,
/// variable mapper) so preview, validation and confirmation never disagree. A secret the Octopus server withheld in a server
/// session is a Warning: it is written empty, listed as missing and can be filled later. Anything else that cannot be
/// resolved — an export value that does not decrypt, or a marker planted in an export file — stays a Blocker.
/// </summary>
public static class OctopusImportSecretSeverity
{
    public static OctopusImportCompatibilitySeverity For(bool withheld, OctopusExportSecrets secrets)
        => IsWithheldByServer(withheld, secrets) ? OctopusImportCompatibilitySeverity.Warning : OctopusImportCompatibilitySeverity.Blocker;

    public static bool IsRequired(bool withheld, OctopusExportSecrets secrets) => For(withheld, secrets) == OctopusImportCompatibilitySeverity.Blocker;

    public static bool IsWithheldByServer(bool withheld, OctopusExportSecrets secrets) => withheld && secrets?.IsWithheldByServer == true;
}
