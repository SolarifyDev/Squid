namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>A later history chunk named a different Octopus server, space or installation than the one the fetch is pinned to.</summary>
public class OctopusImportHistoryServerMismatchException : InvalidOperationException
{
    public OctopusImportHistoryServerMismatchException(string pinnedField) : base($"This session's history fetch is pinned to another Octopus {pinnedField}. Start a new session to fetch from a different server.")
    {
        PinnedField = pinnedField;
    }

    public string PinnedField { get; }
}
