namespace Squid.Core.Services.Deployments.Variables.Exceptions;

/// <summary>
/// A save of a variable set was based on a version another writer (another editor, or an Octopus import that appended
/// variables) has since moved: replacing the set with the stale list would erase that writer's variables, so it is refused.
/// </summary>
public class VariableSetVersionConflictException : InvalidOperationException
{
    public VariableSetVersionConflictException(int variableSetId, int expectedVersion, int currentVersion)
        : base($"Variable set {variableSetId} was changed by someone else (version {currentVersion}, this save was based on version {expectedVersion}). Reload the variables and save again.")
    {
        VariableSetId = variableSetId;
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }

    public int VariableSetId { get; }

    public int ExpectedVersion { get; }

    public int CurrentVersion { get; }
}
