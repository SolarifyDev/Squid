namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportRedactionDiagnosticCodes
{
    public const string SensitiveVariableValueOmitted = "OctopusImport.Redaction.SensitiveVariableValueOmitted";
    public const string FeedCredentialsOmitted = "OctopusImport.Redaction.FeedCredentialsOmitted";
    public const string AccountCredentialsOmitted = "OctopusImport.Redaction.AccountCredentialsOmitted";
    public const string CertificatePrivateMaterialOmitted = "OctopusImport.Redaction.CertificatePrivateMaterialOmitted";
    public const string EndpointSecretOmitted = "OctopusImport.Redaction.EndpointSecretOmitted";
    public const string SensitiveActionPropertyValueOmitted = "OctopusImport.Redaction.SensitiveActionPropertyValueOmitted";
    public const string SuspiciousPropertyValueRedacted = "OctopusImport.Redaction.SuspiciousPropertyValueRedacted";
}
