using Halibut;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines;

public interface IMachineRegistrationService : IScopedDependency
{
    Task<RegisterMachineResponseData> RegisterKubernetesAgentAsync(RegisterKubernetesAgentCommand command, CancellationToken cancellationToken = default);
    Task<RegisterMachineResponseData> RegisterKubernetesApiAsync(RegisterKubernetesApiCommand command, CancellationToken cancellationToken = default);
}

public partial class MachineRegistrationService : IMachineRegistrationService
{
    private readonly IMachineDataProvider _dataProvider;
    private readonly HalibutRuntime _halibutRuntime;
    private readonly SelfCertSetting _selfCertSetting;

    public MachineRegistrationService(
        IMachineDataProvider dataProvider,
        HalibutRuntime halibutRuntime,
        SelfCertSetting selfCertSetting)
    {
        _dataProvider = dataProvider;
        _halibutRuntime = halibutRuntime;
        _selfCertSetting = selfCertSetting;
    }

    private static Machine BuildMachineDefaults(string name, string roles, string environmentIds, int spaceId, string endpointJson)
    {
        return new Machine
        {
            Name = name,
            IsDisabled = false,
            Roles = roles ?? string.Empty,
            EnvironmentIds = environmentIds ?? string.Empty,
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

    private string GetServerThumbprint()
    {
        var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
        using var cert = X509CertificateLoader.LoadPkcs12(certBytes, _selfCertSetting.Password);

        return cert.Thumbprint;
    }
}
