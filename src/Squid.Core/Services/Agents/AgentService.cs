using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Agent;
using Squid.Message.Enums;

namespace Squid.Core.Services.Agents;

public interface IAgentService : IScopedDependency
{
    Task<RegisterAgentResponseData> RegisterAgentAsync(RegisterAgentCommand command, CancellationToken cancellationToken = default);
}

public class AgentService : IAgentService
{
    private readonly IAgentDataProvider _agentDataProvider;
    private readonly HalibutRuntime _halibutRuntime;
    private readonly SelfCertSetting _selfCertSetting;

    public AgentService(
        IAgentDataProvider agentDataProvider,
        HalibutRuntime halibutRuntime,
        SelfCertSetting selfCertSetting)
    {
        _agentDataProvider = agentDataProvider;
        _halibutRuntime = halibutRuntime;
        _selfCertSetting = selfCertSetting;
    }

    public async Task<RegisterAgentResponseData> RegisterAgentAsync(RegisterAgentCommand command, CancellationToken cancellationToken = default)
    {
        _halibutRuntime.Trust(command.Thumbprint);

        var machine = BuildAgentMachine(command);

        await _agentDataProvider.AddAgentMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        Log.Information("Registered agent machine {MachineName} ({SubscriptionId})",
            machine.Name, machine.PollingSubscriptionId);

        return new RegisterAgentResponseData
        {
            MachineId = machine.Id,
            ServerThumbprint = GetServerThumbprint(),
            SubscriptionUri = $"poll://{command.SubscriptionId}/"
        };
    }

    private static Machine BuildAgentMachine(RegisterAgentCommand command)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            command.SubscriptionId,
            command.Thumbprint,
            command.Namespace
        });

        return new Machine
        {
            Name = command.MachineName ?? $"k8s-agent-{command.SubscriptionId[..8]}",
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
            Slug = $"k8s-agent-{Guid.NewGuid():N}",
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
