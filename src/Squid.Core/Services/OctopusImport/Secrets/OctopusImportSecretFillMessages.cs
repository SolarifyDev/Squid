using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>The words preview, validation and confirmation use for a still-missing secret. Names only — never a value.</summary>
public static class OctopusImportSecretFillMessages
{
    public static string What(OctopusResourceKind kind) => kind == OctopusResourceKind.Feed ? "feed password" : "sensitive value";

    /// <summary>The Squid project, library variable set or feed by name and id; the bare id only when its row is unknown.</summary>
    public static string Where(OctopusImportSecretFillCandidate candidate)
    {
        var destination = candidate.Destination;

        if (destination == null) return candidate.Kind == OctopusResourceKind.Feed ? $"Squid feed {candidate.DestinationId}" : $"Squid variable set {candidate.DestinationId}";

        return destination.OwnerKind switch
        {
            OctopusImportFillDestinationDto.ProjectOwner => $"Squid project \"{destination.OwnerName}\" (project {destination.OwnerId}, variable set {destination.VariableSetId})",
            OctopusImportFillDestinationDto.LibraryVariableSetOwner => $"Squid library variable set \"{destination.OwnerName}\" (library variable set {destination.OwnerId}, variable set {destination.VariableSetId})",
            _ => $"Squid feed \"{destination.OwnerName}\" (feed {destination.OwnerId})"
        };
    }

    public static string Fill(OctopusImportSecretFillCandidate candidate, bool pairingRequired)
        => $"This {What(candidate.Kind)} was imported empty earlier because the Octopus API does not return secrets. This import fills it from the export file into {Where(candidate)}: empty → value from the export file. Only a still-empty value is written; one set in Squid in the meantime is kept. Releases created before the fill keep the empty value in their snapshot."
           + (pairingRequired ? " It is filled only after you confirm that this export file comes from the Octopus installation this space was imported from." : string.Empty);

    /// <summary>The diagnostic code for a missing secret that is not filled because of Squid's state.</summary>
    public static string KeptCode(SecretSlotState destination)
        => destination switch
        {
            SecretSlotState.AlreadySet => OctopusImportPreviewDiagnosticCodes.SecretSetInSquid,
            SecretSlotState.NotFound => OctopusImportPreviewDiagnosticCodes.SecretFillTargetGone,
            SecretSlotState.Empty => OctopusImportPreviewDiagnosticCodes.SecretStillMissing,
            _ => OctopusImportPreviewDiagnosticCodes.SecretFillAmbiguous
        };

    public static string Kept(OctopusImportSecretFillCandidate candidate, SecretSlotState destination)
        => destination switch
        {
            SecretSlotState.AlreadySet => $"This {What(candidate.Kind)} was missing after an earlier import and has since been set in {Where(candidate)}; it is kept as it is and never filled from Octopus.",
            SecretSlotState.NotFound => $"This {What(candidate.Kind)} was missing after an earlier import, but {Where(candidate)} no longer has it with this name, type and scope (renamed, re-scoped or deleted); nothing is filled.",
            SecretSlotState.Empty => $"{Where(candidate)} was being saved in Squid while this {What(candidate.Kind)} was filled, so nothing was written and it stays missing. Run the import again, or set it in Squid.",
            _ => $"{Where(candidate)} has more than one sensitive variable with this name, type and scope, so which one was missing cannot be told and nothing is filled. Set it in Squid."
        };

    public static string SourceAmbiguous(OctopusImportSecretFillCandidate candidate)
        => $"The export file has more than one sensitive variable with this name, type and scope, so which one fills the missing value in {Where(candidate)} cannot be told and nothing is filled. Set it in Squid.";

    public static string StillMissing(OctopusImportSecretFillCandidate candidate, bool serverSource)
        => serverSource
            ? $"This {What(candidate.Kind)} is still missing in {Where(candidate)}. Set it in Squid, or import the Octopus export file to fill it."
            : $"This {What(candidate.Kind)} is still missing in {Where(candidate)} and this export file cannot supply it (no export password, or the value does not decrypt with it). Set it in Squid.";

    public static string EmptyInOctopus(OctopusImportSecretFillCandidate candidate, bool pairingRequired)
        => $"The export file holds no value for this {What(candidate.Kind)} either (it is empty in Octopus): nothing is written to {Where(candidate)} and it is no longer reported as missing."
           + (pairingRequired ? " This is recorded only after you confirm that this export file comes from the Octopus installation this space was imported from." : string.Empty);

    public static string Unproven(OctopusImportSecretFillCandidate candidate, OctopusImportFillEvidence evidence)
        => evidence?.Basis == OctopusImportFillEvidences.ExportFingerprintMismatchBasis
            ? $"This {What(candidate.Kind)} is missing in {Where(candidate)}, but this export file's fingerprint is not one recorded for this space, and none of its process step, action or lifecycle phase ids were imported into it, so nothing proves it comes from the same Octopus server. Missing values are never filled from another installation's file; import an export file of the same Octopus server."
            : $"This {What(candidate.Kind)} is missing in {Where(candidate)}, but nothing proves this export file comes from the Octopus installation this space was imported from: no matching installation id, export fingerprint, or process step, action or lifecycle phase ids. Missing values are never filled from another installation's file; import the export file of the same Octopus server.";

    public static string PairingRequired(int matchingIds, string installationId, OctopusImportInstallationOrigin origin, int fills)
        => $"This export file carries no proof of its Octopus installation, but {matchingIds} of its process step, action and lifecycle phase ids were imported into this space from Octopus installation {installationId ?? "(not recorded)"}{OctopusImportOriginMessages.OriginText(origin)}. Confirm that the file comes from that installation to fill or settle {fills} missing value(s); without the confirmation the import is refused.";

    public const string PairingUnconfirmed = "This import fills missing values from an export file whose Octopus installation is not proven. Confirm the pairing shown in the preview (the file comes from the Octopus installation this space was imported from), or import without this file.";

    public const string NotBacked = "The plan fills a missing value, but no missing value for it is recorded, the export file cannot supply it, or the file is not proven to come from this installation. Re-run the preview.";
}
