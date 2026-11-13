namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

/// <summary>
/// Thrown when an operator action (cancel, force-cancel, resume, interruption submit / take-responsibility) targets a
/// server task that was imported from Octopus. Imported history is a read-only record of what happened on another
/// server: it is never executed, holds no concurrency slot and must never be driven by Squid's task controls, even
/// when its original Octopus state was non-terminal (Executing, Pending, Cancelling). Mapped to HTTP 409.
/// </summary>
public sealed class ImportedServerTaskReadOnlyException : InvalidOperationException
{
    public int TaskId { get; }

    public ImportedServerTaskReadOnlyException(int taskId)
        : base($"ServerTask {taskId} was imported from Octopus and is read-only; it cannot be cancelled, resumed or have its interruptions answered")
    {
        TaskId = taskId;
    }
}
