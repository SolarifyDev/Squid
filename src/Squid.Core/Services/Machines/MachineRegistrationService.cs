using Halibut;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Enums;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines;

public interface IMachineRegistrationService : IScopedDependency
{
    Task<RegisterMachineResponseData> RegisterKubernetesAgentAsync(RegisterKubernetesAgentCommand command, CancellationToken cancellationToken = default);
    
    Task<RegisterMachineResponseData> RegisterKubernetesApiAsync(RegisterKubernetesApiCommand command, CancellationToken cancellationToken = default);
}

public partial class MachineRegistrationService : IMachineRegistrationService
{
    private readonly IMachineDataProvider _dataProvider;
    private readonly IMachinePolicyDataProvider _policyDataProvider;
    private readonly IEnvironmentDataProvider _environmentDataProvider;
    private readonly HalibutRuntime _halibutRuntime;
    private readonly SelfCertSetting _selfCertSetting;

    public MachineRegistrationService(
        IMachineDataProvider dataProvider,
        IMachinePolicyDataProvider policyDataProvider,
        IEnvironmentDataProvider environmentDataProvider,
        HalibutRuntime halibutRuntime,
        SelfCertSetting selfCertSetting)
    {
        _dataProvider = dataProvider;
        _policyDataProvider = policyDataProvider;
        _environmentDataProvider = environmentDataProvider;
        _halibutRuntime = halibutRuntime;
        _selfCertSetting = selfCertSetting;
    }

    private static Machine BuildMachineDefaults(string name, string roles, string environmentIds, int spaceId, string endpointJson)
    {
        return new Machine
        {
            Name = name,
            IsDisabled = false,
            Roles = roles ?? "[]",
            EnvironmentIds = environmentIds ?? "[]",
            Json = string.Empty,
            Thumbprint = string.Empty,
            Uri = string.Empty,
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = spaceId,
            OperatingSystem = OperatingSystemType.Linux,
            ShellName = "Bash",
            ShellVersion = string.Empty,
            LicenseHash = string.Empty,
            Slug = $"machine-{Guid.NewGuid():N}",
        };
    }

    private async Task<string> ResolveEnvironmentIdsAsync(string environmentNames, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentNames))
            return "[]";

        var names = environmentNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (names.Count == 0)
            return "[]";

        var environments = await _environmentDataProvider.GetEnvironmentsByNamesAsync(names, ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(environments.Select(e => e.Id).ToList());
    }

    private static string SerializeRolesFromCsv(string csvRoles)
    {
        if (string.IsNullOrWhiteSpace(csvRoles)) return null;

        var roles = csvRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return JsonSerializer.Serialize(roles);
    }

    private async Task AssignDefaultPolicyAsync(Machine machine, CancellationToken cancellationToken)
    {
        if (machine.MachinePolicyId != null) return;

        var defaultPolicy = await _policyDataProvider.GetDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (defaultPolicy == null) return;

        machine.MachinePolicyId = defaultPolicy.Id;
    }

    private string GetServerThumbprint()
    {
        var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
        using var cert = X509CertificateLoader.LoadPkcs12(certBytes, _selfCertSetting.Password);

        return cert.Thumbprint;
    }
}
