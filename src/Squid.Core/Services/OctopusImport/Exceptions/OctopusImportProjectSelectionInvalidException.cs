using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>A project selection that names no project, too many, or an id Octopus could never have listed.</summary>
public class OctopusImportProjectSelectionInvalidException : InvalidOperationException
{
    public OctopusImportProjectSelectionInvalidException(OctopusImportProjectSelectionProblem problem, string message) : base(message)
    {
        Problem = problem;
    }

    public OctopusImportProjectSelectionProblem Problem { get; }
}
