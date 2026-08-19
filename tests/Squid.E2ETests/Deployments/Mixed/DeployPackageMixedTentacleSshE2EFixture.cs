using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Autofac;
using Halibut;
using Microsoft.Extensions.Configuration;
using Renci.SshNet;
using Serilog;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.Halibut;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.E2ETests.Deployments.Mixed;

/// <summary>
/// One environment with both a Tentacle polling stub and a real OpenSSH Docker target.
/// Soft-skips SSH registration when Docker is unavailable so local non-Docker runs remain green.
/// </summary>
public sealed class DeployPackageMixedTentacleSshE2EFixture
    : E2EFixtureBase<DeployPackageMixedTentacleSshE2EFixture>
{
    public const string TentacleRole = "mixed-tentacle";
    public const string SshRole = "mixed-ssh";
    public const string RemoteWorkDir = "/tmp/squid-mixed-ssh-e2e";
    public const string SshUser = "squid";
    public const string SshPassword = "squidssh";

    public CapturingLogSink LogSink { get; } = new();
    public int EnvironmentId { get; private set; }
    public string EnvironmentName { get; private set; }
    public int TentacleMachineId { get; private set; }
    public int SshMachineId { get; private set; }
    public int SshHostPort { get; private set; }
    public string SshFingerprint { get; private set; }
    public bool DockerAvailable { get; private set; }
    public string SkipReason { get; private set; }

    private TentacleStub _pollingStub;
    private int _pollingPort;
    private string _containerName;

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        _pollingPort = GetAvailablePort();
        builder.RegisterInstance(new HalibutSetting
        {
            Polling = new PollingSettings { Enabled = true, Port = _pollingPort }
        }).AsSelf().SingleInstance();
    }

    protected override async Task OnInitializedAsync()
    {
        MultiplexCapturingSink.Instance.Register(LogSink);
        await CreateEnvironmentAsync().ConfigureAwait(false);
        StartPollingStub();
        await RegisterTentacleMachineAsync().ConfigureAwait(false);

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
            SkipReason = $"Failed to start/register SSH target for mixed e2e: {ex.Message}";
            Log.Warning(ex, "Mixed Tentacle+SSH fixture SSH setup failed; mixed tests will skip.");
            await TryRemoveContainerAsync().ConfigureAwait(false);
        }
    }

    protected override async Task OnDisposingAsync()
    {
        MultiplexCapturingSink.Instance.Unregister(LogSink);
        if (_pollingStub != null)
            await _pollingStub.DisposeAsync().ConfigureAwait(false);
        await TryRemoveContainerAsync().ConfigureAwait(false);
    }

    private async Task CreateEnvironmentAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var env = await builder.CreateEnvironmentAsync(
                $"Mixed Tentacle SSH E2E {Guid.NewGuid().ToString("N")[..6]}").ConfigureAwait(false);
            EnvironmentId = env.Id;
            EnvironmentName = env.Name;
        }).ConfigureAwait(false);
    }

    private void StartPollingStub()
    {
        var selfCertSetting = LifetimeScope.Resolve<Core.Settings.SelfCert.SelfCertSetting>();
        var certBytes = Convert.FromBase64String(selfCertSetting.Base64);
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            certBytes, selfCertSetting.Password);
        var serverThumbprint = cert.Thumbprint;

        _pollingStub = TentacleStub.CreatePolling(serverThumbprint, _pollingPort);
        var halibutRuntime = LifetimeScope.Resolve<HalibutRuntime>();
        halibutRuntime.Trust(_pollingStub.Thumbprint);
    }

    private async Task RegisterTentacleMachineAsync()
    {
        var registration = await Run<IMachineRegistrationService, RegisterMachineResponseData>(async svc =>
            await svc.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
            {
                MachineName = $"mixed-tentacle-{_pollingStub.SubscriptionId[..8]}",
                Thumbprint = _pollingStub.Thumbprint,
                SubscriptionId = _pollingStub.SubscriptionId,
                Roles = TentacleRole,
                Environments = EnvironmentName,
                AgentVersion = "1.0.0-test"
            }).ConfigureAwait(false)).ConfigureAwait(false);

        TentacleMachineId = registration.MachineId;
    }

    private async Task StartSshContainerAsync()
    {
        SshHostPort = GetAvailablePort();
        _containerName = $"squid-mixed-ssh-{Guid.NewGuid().ToString("N")[..8]}";
        var args =
            $"run -d --name {_containerName} " +
            $"-p 127.0.0.1:{SshHostPort}:2222 " +
            "-e PUID=1000 -e PGID=1000 " +
            "-e PASSWORD_ACCESS=true " +
            $"-e USER_PASSWORD={SshPassword} " +
            $"-e USER_NAME={SshUser} " +
            "lscr.io/linuxserver/openssh-server:latest";

        var run = await RunProcessAsync("docker", args).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"docker run failed: {run.Error}\n{run.Output}");

        Exception last = null;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                SshFingerprint = await ProbeFingerprintAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(SshFingerprint))
                    return;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"SSH container did not become ready on port {SshHostPort}. Last error: {last?.Message}");
    }

    private async Task RegisterSshTargetAsync()
    {
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
                Name = $"mixed-ssh-account-{Guid.NewGuid().ToString("N")[..6]}",
                AccountType = AccountType.UsernamePassword,
                Credentials = credentials,
                EnvironmentIds = [EnvironmentId]
            }, CancellationToken.None).ConfigureAwait(false);

            return created.DeploymentAccount.Id;
        }).ConfigureAwait(false);

        SshMachineId = await Run<IMachineRegistrationService, int>(async registration =>
        {
            var result = await registration.RegisterSshAsync(new RegisterSshCommand
            {
                MachineName = $"mixed-ssh-{Guid.NewGuid().ToString("N")[..6]}",
                SpaceId = 1,
                Roles = [SshRole],
                EnvironmentIds = [EnvironmentId],
                Host = "127.0.0.1",
                Port = SshHostPort,
                Fingerprint = SshFingerprint,
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
    }

    private async Task<string> ProbeFingerprintAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new SshClient("127.0.0.1", SshHostPort, SshUser, SshPassword);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(3);
        client.HostKeyReceived += (_, e) =>
        {
            var fp = e.FingerPrintSHA256;
            if (!string.IsNullOrWhiteSpace(fp))
                tcs.TrySetResult(fp.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? fp : $"SHA256:{fp}");
            e.CanTrust = true;
        };

        try
        {
            client.Connect();
            using var cmd = client.CreateCommand($"mkdir -p {RemoteWorkDir} && echo ok");
            cmd.CommandTimeout = TimeSpan.FromSeconds(5);
            cmd.Execute();
            client.Disconnect();
        }
        catch
        {
            // host key may still have been received
        }

        if (tcs.Task.IsCompleted)
            return await tcs.Task.ConfigureAwait(false);

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
            return RunProcessAsync("docker", "info").GetAwaiter().GetResult().ExitCode == 0;
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

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName, string arguments)
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
