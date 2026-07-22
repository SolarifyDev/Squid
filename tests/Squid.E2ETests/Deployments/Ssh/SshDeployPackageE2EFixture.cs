using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autofac;
using Microsoft.Extensions.Configuration;
using Renci.SshNet;
using Serilog;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Machines;
using Squid.E2ETests.Infrastructure;
using Xunit;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using DeploymentAccountEntity = Squid.Core.Persistence.Entities.Deployments.DeploymentAccount;

namespace Squid.E2ETests.Deployments.Ssh;

/// <summary>
/// Starts an OpenSSH Docker container (when Docker is available) and registers it as a Squid SSH target
/// with a custom remote working directory. Used to lock the Deploy a Package package-cache path contract.
/// </summary>
public sealed class SshDeployPackageE2EFixture : E2EFixtureBase<SshDeployPackageE2EFixture>
{
    public const string TargetRole = "ssh-e2e";
    public const string RemoteWorkDir = "/tmp/squid-ssh-e2e";
    public const string SshUser = "squid";
    public const string SshPassword = "squidssh";

    public CapturingLogSink LogSink { get; } = new();
    public int EnvironmentId { get; private set; }
    public string EnvironmentName { get; private set; }
    public int MachineId { get; private set; }
    public int HostPort { get; private set; }
    public string Fingerprint { get; private set; }
    public bool DockerAvailable { get; private set; }
    public string SkipReason { get; private set; }

    private string _containerName;

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        // no overrides required
    }

    protected override async Task OnInitializedAsync()
    {
        MultiplexCapturingSink.Instance.Register(LogSink);

        await CreateEnvironmentAsync().ConfigureAwait(false);

        if (!IsDockerAvailable())
        {
            DockerAvailable = false;
            SkipReason = "Docker is not available in this environment.";
            return;
        }

        try
        {
            await StartSshContainerAsync().ConfigureAwait(false);
            await RegisterSshTargetAsync().ConfigureAwait(false);
            DockerAvailable = true;
        }
        catch (Exception ex)
        {
            DockerAvailable = false;
            SkipReason = $"Failed to start/register SSH e2e container: {ex.Message}";
            Log.Warning(ex, "SSH e2e fixture setup failed; tests will skip.");
            await TryRemoveContainerAsync().ConfigureAwait(false);
        }
    }

    protected override async Task OnDisposingAsync()
    {
        MultiplexCapturingSink.Instance.Unregister(LogSink);
        await TryRemoveContainerAsync().ConfigureAwait(false);
    }

    private async Task CreateEnvironmentAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var env = await builder.CreateEnvironmentAsync($"SSH Deploy Package E2E {Guid.NewGuid().ToString("N")[..6]}")
                .ConfigureAwait(false);
            EnvironmentId = env.Id;
            EnvironmentName = env.Name;
        }).ConfigureAwait(false);
    }

    private async Task StartSshContainerAsync()
    {
        HostPort = GetAvailablePort();
        _containerName = $"squid-ssh-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        // linuxserver openssh listens on 2222 inside the container.
        var args =
            $"run -d --name {_containerName} " +
            $"-p 127.0.0.1:{HostPort}:2222 " +
            "-e PUID=1000 -e PGID=1000 " +
            "-e PASSWORD_ACCESS=true " +
            $"-e USER_PASSWORD={SshPassword} " +
            $"-e USER_NAME={SshUser} " +
            "lscr.io/linuxserver/openssh-server:latest";

        var run = await RunProcessAsync("docker", args).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"docker run failed: {run.Error}\n{run.Output}");

        // Wait for sshd readiness.
        Exception last = null;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                Fingerprint = await ProbeFingerprintAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(Fingerprint))
                    return;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"SSH container did not become ready on port {HostPort}. Last error: {last?.Message}");
    }

    private async Task RegisterSshTargetAsync()
    {
        // Create username/password account
        var accountId = await Run<IDeploymentAccountService, int>(async accountService =>
        {
            var credentials = JsonSerializer.SerializeToElement(new
            {
                username = SshUser,
                password = SshPassword
            });

            var created = await accountService.CreateAsync(new CreateDeploymentAccountCommand
            {
                SpaceId = 1,
                Name = $"ssh-e2e-{Guid.NewGuid().ToString("N")[..6]}",
                AccountType = AccountType.UsernamePassword,
                Credentials = credentials,
                EnvironmentIds = [EnvironmentId]
            }, CancellationToken.None).ConfigureAwait(false);

            return created.DeploymentAccount.Id;
        }).ConfigureAwait(false);

        MachineId = await Run<IMachineRegistrationService, int>(async registration =>
        {
            var result = await registration.RegisterSshAsync(new RegisterSshCommand
            {
                MachineName = $"ssh-e2e-{Guid.NewGuid().ToString("N")[..6]}",
                SpaceId = 1,
                Roles = [TargetRole],
                EnvironmentIds = [EnvironmentId],
                Host = "127.0.0.1",
                Port = HostPort,
                Fingerprint = Fingerprint,
                RemoteWorkingDirectory = RemoteWorkDir,
                ResourceReferences =
                [
                    new EndpointResourceReference
                    {
                        Type = EndpointResourceType.AuthenticationAccount,
                        ResourceId = accountId
                    }
                ]
            }).ConfigureAwait(false);

            return result.MachineId;
        }).ConfigureAwait(false);

        Log.Information("SSH e2e target registered. MachineId={MachineId}, Port={Port}, Fingerprint={Fingerprint}",
            MachineId, HostPort, Fingerprint);
    }

    private async Task<string> ProbeFingerprintAsync()
    {
        // Connect once to capture host key SHA256 via HostKeyReceived.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new SshClient("127.0.0.1", HostPort, SshUser, SshPassword);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(3);
        client.HostKeyReceived += (_, e) =>
        {
            // e.FingerPrintSHA256 already looks like "SHA256:...."
            var fp = e.FingerPrintSHA256;
            if (!string.IsNullOrWhiteSpace(fp))
                tcs.TrySetResult(fp.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? fp : $"SHA256:{fp}");
            // Accept for this probe connection only; production deploy validates expected fingerprint.
            e.CanTrust = true;
        };

        try
        {
            client.Connect();
            // ensure auth works
            using var cmd = client.CreateCommand("mkdir -p /tmp/squid-ssh-e2e && echo ok");
            cmd.CommandTimeout = TimeSpan.FromSeconds(5);
            cmd.Execute();
            client.Disconnect();
        }
        catch
        {
            // still may have received host key
        }

        if (tcs.Task.IsCompleted)
            return await tcs.Task.ConfigureAwait(false);

        // Fallback: ssh-keyscan if available.
        var scan = await RunProcessAsync("ssh-keyscan", $"-p {HostPort} -T 3 127.0.0.1").ConfigureAwait(false);
        if (scan.ExitCode == 0 && !string.IsNullOrWhiteSpace(scan.Output))
        {
            var temp = Path.GetTempFileName();
            await File.WriteAllTextAsync(temp, scan.Output).ConfigureAwait(false);
            try
            {
                var lf = await RunProcessAsync("ssh-keygen", $"-lf {temp} -E sha256").ConfigureAwait(false);
                var line = lf.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(l => l.Contains("ED25519", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("ECDSA", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("RSA", StringComparison.OrdinalIgnoreCase));
                if (line != null)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var fp = parts.FirstOrDefault(p => p.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(fp))
                        return fp;
                }
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException("Unable to capture SSH host fingerprint.");
    }

    private async Task TryRemoveContainerAsync()
    {
        if (string.IsNullOrWhiteSpace(_containerName))
            return;

        try
        {
            await RunProcessAsync("docker", $"rm -f {_containerName}").ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            var result = RunProcessAsync("docker", "info").GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
