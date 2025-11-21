using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Settings.SelfCert;

namespace Squid.Core.Halibut;

public class HalibutModule : Module
{
    private readonly SelfCertSetting _selfCertSetting;
    
    public HalibutModule(SelfCertSetting selfCertSetting)
    {
        _selfCertSetting = selfCertSetting;
    }
    
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(_ =>
        {
            var selfCertBase64 = _selfCertSetting.Base64;

            if (string.IsNullOrEmpty(selfCertBase64))
                throw new InvalidOperationException("缺少HALIBUT_CERT_BASE64环境变量");

            var certBytes = Convert.FromBase64String(selfCertBase64);
            var serverCert = new X509Certificate2(certBytes, _selfCertSetting.Password, X509KeyStorageFlags.MachineKeySet);

            var services = new DelegateServiceFactory();

            var halibutTimeoutsAndLimits = HalibutTimeoutsAndLimits.RecommendedValues();

            var halibutRuntime = new HalibutRuntimeBuilder()
                .WithServiceFactory(services)
                .WithServerCertificate(serverCert)
                .WithHalibutTimeoutsAndLimits(halibutTimeoutsAndLimits)
                .Build();

            return halibutRuntime;
        }).As<HalibutRuntime>().SingleInstance();
    }
}