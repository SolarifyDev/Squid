using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;

public class CommandExecutionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
}

public interface ICommandExecutionService : IScopedDependency
{
    Task<CommandExecutionResult> ExecuteCommandAsync(ActionCommand command, Message.Domain.Deployments.Machine targetMachine, CancellationToken cancellationToken = default);
}
