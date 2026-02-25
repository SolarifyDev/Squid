using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines;

public interface IMachineRegistrationService : IScopedDependency
{
    Task<RegisterMachineResponseData> RegisterMachineAsync(RegisterMachineCommand command, CancellationToken cancellationToken = default);
}

public class MachineRegistrationService : IMachineRegistrationService
{
    private readonly IMachineRegistrationDataProvider _dataProvider;
    private readonly HalibutRuntime _halibutRuntime;
    private readonly SelfCertSetting _selfCertSetting;

    public MachineRegistrationService(
        IMachineRegistrationDataProvider dataProvider,
        HalibutRuntime halibutRuntime,
        SelfCertSetting selfCertSetting)
    {
        _dataProvider = dataProvider;
        _halibutRuntime = halibutRuntime;
        _selfCertSetting = selfCertSetting;
    }

    public async Task<RegisterMachineResponseData> RegisterMachineAsync(RegisterMachineCommand command, CancellationToken cancellationToken = default)
    {
        _halibutRuntime.Trust(command.Thumbprint);

        var existing = await _dataProvider.GetMachineBySubscriptionIdAsync(
            command.SubscriptionId, cancellationToken).ConfigureAwait(false);

        Machine machine;

        if (existing != null)
        {
            existing.Thumbprint = command.Thumbprint;
            existing.Roles = command.Roles ?? existing.Roles;
            existing.EnvironmentIds = command.EnvironmentIds ?? existing.EnvironmentIds;
            existing.Endpoint = BuildEndpointJson(command);

            await _dataProvider.UpdateMachineAsync(existing, cancellationToken).ConfigureAwait(false);

            machine = existing;

            Log.Information("Updated existing machine {MachineName} ({SubscriptionId})",
                machine.Name, machine.PollingSubscriptionId);
        }
        else
        {
            machine = BuildMachine(command);

            await _dataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

            Log.Information("Registered new machine {MachineName} ({SubscriptionId})",
                machine.Name, machine.PollingSubscriptionId);
        }

        return new RegisterMachineResponseData
        {
            MachineId = machine.Id,
            ServerThumbprint = GetServerThumbprint(),
            SubscriptionUri = $"poll://{command.SubscriptionId}/"
        };
    }

    private static string BuildEndpointJson(RegisterMachineCommand command)
    {
        return JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            command.SubscriptionId,
            command.Thumbprint,
            command.Namespace
        });
    }

    private static Machine BuildMachine(RegisterMachineCommand command)
    {
        var endpointJson = BuildEndpointJson(command);

        return new Machine
        {
            Name = command.MachineName ?? $"machine-{command.SubscriptionId[..8]}",
            IsDisabled = false,
            Roles = command.Roles ?? string.Empty,
            EnvironmentIds = command.EnvironmentIds ?? string.Empty,
            Json = string.Empty,
            Thumbprint = command.Thumbprint,
            Uri = string.Empty,
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = command.SpaceId,
            OperatingSystem = OperatingSystemType.Linux,
            ShellName = "Bash",
            ShellVersion = string.Empty,
            LicenseHash = string.Empty,
            Slug = $"machine-{Guid.NewGuid():N}",
            PollingSubscriptionId = command.SubscriptionId
        };
    }

    private string GetServerThumbprint()
    {
        var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
        using var cert = new X509Certificate2(certBytes, _selfCertSetting.Password);

        return cert.Thumbprint;
    }
}
