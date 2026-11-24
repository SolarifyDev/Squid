namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

public sealed class RunbookTaskOperationNotSupportedException(int taskId, string operation)
    : InvalidOperationException($"{operation} is not supported for Runbook task {taskId}.");
