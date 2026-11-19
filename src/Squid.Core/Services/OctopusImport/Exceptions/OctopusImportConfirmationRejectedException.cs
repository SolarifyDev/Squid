namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// The session cannot be confirmed as it stands (not Validated, upload gone, plan unreadable, export password
/// unreadable, archive no longer extractable). Nothing was imported and the session state is unchanged.
/// </summary>
public class OctopusImportConfirmationRejectedException : InvalidOperationException
{
    public OctopusImportConfirmationRejectedException(string message, string code = OctopusImportConfirmationDiagnosticCodes.ConfirmationRequiresValidatedSession)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
