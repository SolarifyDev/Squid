using System.Text;
using Renci.SshNet;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshConnectionFactory : ISshConnectionFactory
{
    public ISshConnectionScope CreateScope(SshConnectionInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Host))
            throw new ArgumentException("SSH host is required", nameof(info));

        if (string.IsNullOrWhiteSpace(info.Username))
            throw new ArgumentException("SSH username is required", nameof(info));

        var connectionInfo = BuildConnectionInfo(info);

        return new SshConnectionScope(connectionInfo, info.ExpectedFingerprint);
    }

    private static ConnectionInfo BuildConnectionInfo(SshConnectionInfo info)
    {
        var authMethods = BuildAuthMethods(info);

        if (authMethods.Count == 0)
            throw new InvalidOperationException("No SSH authentication method available — provide either a private key or a password");

        var connectionInfo = new ConnectionInfo(info.Host, info.Port, info.Username, authMethods.ToArray())
        {
            Timeout = info.ConnectTimeout > TimeSpan.Zero ? info.ConnectTimeout : TimeSpan.FromSeconds(30)
        };

        return connectionInfo;
    }

    private static List<AuthenticationMethod> BuildAuthMethods(SshConnectionInfo info)
    {
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrEmpty(info.PrivateKey))
        {
            var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(info.PrivateKey));
            var keyFile = string.IsNullOrEmpty(info.Passphrase) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, info.Passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(info.Username, keyFile));
        }

        if (!string.IsNullOrEmpty(info.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(info.Username, info.Password));

            var kbInteractive = new KeyboardInteractiveAuthenticationMethod(info.Username);
            var password = info.Password;
            kbInteractive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            methods.Add(kbInteractive);
        }

        return methods;
    }
}
