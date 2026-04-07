namespace Squid.Core.Services.DeploymentExecution.Ssh;

public record SshCommandResult(int ExitCode, string Output, string Error);
