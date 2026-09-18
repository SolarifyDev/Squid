using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Exceptions;

public class OctopusImportSessionStateTransitionException : Exception
{
    public OctopusImportSessionStateTransitionException(OctopusImportSessionState from, OctopusImportSessionState to)
        : base($"Cannot transition Octopus import session from '{from}' to '{to}'.")
    {
        From = from;
        To = to;
    }

    public OctopusImportSessionState From { get; }

    public OctopusImportSessionState To { get; }
}
