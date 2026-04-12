namespace Squid.Core.Services.DeploymentExecution.Ssh;

public interface ISshConnectionFactory : IScopedDependency
{
    ISshConnectionScope CreateScope(SshConnectionInfo info);
}
