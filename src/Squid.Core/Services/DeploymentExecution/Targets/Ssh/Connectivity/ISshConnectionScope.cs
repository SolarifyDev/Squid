using Renci.SshNet;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public interface ISshConnectionScope : IDisposable
{
    SshClient GetSshClient();
    SftpClient GetSftpClient();
}
