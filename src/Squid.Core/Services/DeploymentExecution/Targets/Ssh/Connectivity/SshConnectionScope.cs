using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshConnectionScope : ISshConnectionScope
{
    private readonly Renci.SshNet.ConnectionInfo _connectionInfo;
    private readonly string _expectedFingerprint;

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(5);

    private SshClient _sshClient;
    private SftpClient _sftpClient;
    private readonly object _lock = new();

    internal SshConnectionScope(Renci.SshNet.ConnectionInfo connectionInfo, string expectedFingerprint)
    {
        _connectionInfo = connectionInfo;
        _expectedFingerprint = expectedFingerprint;
    }

    public SshClient GetSshClient()
    {
        if (_sshClient != null) return _sshClient;

        lock (_lock)
        {
            if (_sshClient != null) return _sshClient;

            var client = new SshClient(_connectionInfo);
            client.KeepAliveInterval = KeepAliveInterval;
            client.ErrorOccurred += OnClientError;
            AttachFingerprintValidation(client);
            client.Connect();

            Log.Information("[SSH] SSH client connected to {Host}:{Port}", _connectionInfo.Host, _connectionInfo.Port);

            _sshClient = client;
            return _sshClient;
        }
    }

    public SftpClient GetSftpClient()
    {
        if (_sftpClient != null) return _sftpClient;

        lock (_lock)
        {
            if (_sftpClient != null) return _sftpClient;

            var client = new SftpClient(_connectionInfo);
            client.KeepAliveInterval = KeepAliveInterval;
            client.ErrorOccurred += OnClientError;
            AttachFingerprintValidation(client);
            client.Connect();

            Log.Information("[SSH] SFTP client connected to {Host}:{Port}", _connectionInfo.Host, _connectionInfo.Port);

            _sftpClient = client;
            return _sftpClient;
        }
    }

    private void AttachFingerprintValidation(BaseClient client)
    {
        if (string.IsNullOrEmpty(_expectedFingerprint)) return;

        client.HostKeyReceived += (_, e) =>
        {
            var actual = NormalizeFingerprint(e.FingerPrintSHA256);
            var expected = NormalizeFingerprint(_expectedFingerprint);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("[SSH] Host fingerprint mismatch for {Host}: expected={Expected}, actual={Actual}", _connectionInfo.Host, _expectedFingerprint, e.FingerPrintSHA256);
                e.CanTrust = false;
            }
            else
            {
                e.CanTrust = true;
            }
        };
    }

    internal static string NormalizeFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return string.Empty;

        var normalized = fingerprint.Trim();

        if (normalized.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring(7);

        if (normalized.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring(4);

        return normalized.Replace(":", string.Empty).Replace("-", string.Empty);
    }

    private void OnClientError(object sender, ExceptionEventArgs e)
    {
        Log.Error(e.Exception, "[SSH] Client error on {Host}:{Port}", _connectionInfo.Host, _connectionInfo.Port);
    }

    public void Dispose()
    {
        if (_sftpClient != null)
        {
            if (_sftpClient.IsConnected) _sftpClient.Disconnect();
            _sftpClient.Dispose();
        }

        if (_sshClient != null)
        {
            if (_sshClient.IsConnected) _sshClient.Disconnect();
            _sshClient.Dispose();
        }
    }
}
