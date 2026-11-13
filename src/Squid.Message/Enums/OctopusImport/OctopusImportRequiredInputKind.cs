namespace Squid.Message.Enums.OctopusImport;

/// <summary>Append-only: SquidWeb mirrors these ordinals.</summary>
public enum OctopusImportRequiredInputKind
{
    SensitiveVariableValue = 0,
    FeedPassword = 1,
    AccountSecret = 2,
    CertificatePrivateKey = 3,
    SensitiveActionProperty = 4
}
