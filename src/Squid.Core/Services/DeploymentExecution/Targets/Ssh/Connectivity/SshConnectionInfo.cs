using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public record SshConnectionInfo(
    string Host,
    int Port,
    string Username,
    string PrivateKey,
    string Passphrase,
    string Password,
    string ExpectedFingerprint,
    TimeSpan ConnectTimeout,
    SshProxyType ProxyType = SshProxyType.None,
    string ProxyHost = null,
    int ProxyPort = 0,
    string ProxyUsername = null,
    string ProxyPassword = null);
