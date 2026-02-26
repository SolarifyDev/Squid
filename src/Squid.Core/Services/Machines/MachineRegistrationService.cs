using Halibut;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines;

public interface IMachineRegistrationService : IScopedDependency
{
    Task<RegisterMachineResponseData> RegisterKubernetesAgentAsync(RegisterKubernetesAgentCommand command, CancellationToken cancellationToken = default);
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

    private string GetServerThumbprint()
    {
        var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
        using var cert = X509CertificateLoader.LoadPkcs12(certBytes, _selfCertSetting.Password);

        return cert.Thumbprint;
    }
}
