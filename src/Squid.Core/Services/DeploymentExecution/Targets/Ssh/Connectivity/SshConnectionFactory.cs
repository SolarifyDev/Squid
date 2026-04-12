using System.Text;
using Renci.SshNet;
using Serilog;
using Squid.Message.Enums;

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

        var timeout = info.ConnectTimeout > TimeSpan.Zero ? info.ConnectTimeout : TimeSpan.FromSeconds(30);

        var proxyType = MapProxyType(info.ProxyType);

        if (proxyType != ProxyTypes.None && !string.IsNullOrEmpty(info.ProxyHost))
        {
            return new ConnectionInfo(info.Host, info.Port, info.Username, proxyType, info.ProxyHost, info.ProxyPort, info.ProxyUsername, info.ProxyPassword, authMethods.ToArray())
            {
                Timeout = timeout
            };
        }

        return new ConnectionInfo(info.Host, info.Port, info.Username, authMethods.ToArray())
        {
            Timeout = timeout
        };
    }

    internal static ProxyTypes MapProxyType(SshProxyType proxyType)
    {
        return proxyType switch
        {
            SshProxyType.Http => ProxyTypes.Http,
            SshProxyType.Socks4 => ProxyTypes.Socks4,
            SshProxyType.Socks5 => ProxyTypes.Socks5,
            _ => ProxyTypes.None
        };
    }

    private static List<AuthenticationMethod> BuildAuthMethods(SshConnectionInfo info)
    {
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrEmpty(info.PrivateKey))
        {
            var normalizedKey = NormalizePem(info.PrivateKey);
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(normalizedKey));
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
                {
                    if (prompt.Request.TrimEnd().EndsWith(":", StringComparison.OrdinalIgnoreCase))
                        prompt.Response = password;
                }
            };
            methods.Add(kbInteractive);
        }

        return methods;
    }

    internal static string NormalizePem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("-----BEGIN")) return pem;

        var firstDash = pem.IndexOf("-----", StringComparison.Ordinal);
        var headerEnd = pem.IndexOf("-----", firstDash + 5, StringComparison.Ordinal) + 5;
        var footerStart = pem.IndexOf("-----END", headerEnd, StringComparison.Ordinal);

        if (headerEnd <= 5 || footerStart < 0) return pem;

        var header = pem[..headerEnd].Trim();
        var footer = pem[footerStart..].Trim();
        var body = pem[headerEnd..footerStart].Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");

        var sb = new StringBuilder();
        sb.Append(header).Append('\n');

        for (var i = 0; i < body.Length; i += 70)
        {
            var len = Math.Min(70, body.Length - i);
            sb.Append(body, i, len).Append('\n');
        }

        sb.Append(footer).Append('\n');

        return sb.ToString();
    }
}
