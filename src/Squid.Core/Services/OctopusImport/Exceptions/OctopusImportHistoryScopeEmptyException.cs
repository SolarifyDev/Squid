namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>A REST history fetch found no Octopus project to fetch: nothing was uploaded and nothing imported before.</summary>
public class OctopusImportHistoryScopeEmptyException : InvalidOperationException
{
    public OctopusImportHistoryScopeEmptyException() : base("No Octopus project is known for this space. Import the Octopus configuration first, then fetch its history.")
    {
    }
}
