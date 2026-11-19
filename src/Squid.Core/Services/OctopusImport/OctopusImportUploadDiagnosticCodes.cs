namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportUploadDiagnosticCodes
{
    public const string ExportPasswordNotRecorded = "octopus.upload.export_password_not_recorded";

    public const string ExportPasswordNotRecordedMessage = "The upload could not be completed because recording the export password for this session failed. Upload the export again.";
}
