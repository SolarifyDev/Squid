namespace Squid.Core.Services.DeploymentExecution.Ssh;

public record SshConnectionInfo(
    string Host,
    int Port,
    string Username,
    string PrivateKey,
    string Passphrase,
    string Password,
    string ExpectedFingerprint,
    TimeSpan ConnectTimeout);
