using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Attributes;
using Squid.Core.Extensions;
using Squid.Core.Settings.SelfCert;
using Squid.Infrastructure.Communications;
using Squid.Infrastructure.HalibutService;

namespace Squid.Infrastructure.Halibut;

public class HalibutModule : Module
{
    private readonly SelfCertSetting _selfCertSetting;
    
    public HalibutModule(SelfCertSetting selfCertSetting)
    {
        _selfCertSetting = selfCertSetting;
    }
    
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(c =>
        {
            var services = c.Resolve<IServiceFactory>();
        
            if (string.IsNullOrEmpty(_selfCertSetting.Base64))
                throw new InvalidOperationException("缺少HALIBUT_CERT_BASE64环境变量");
        
            var certBytes = Convert.FromBase64String(_selfCertSetting.Base64);
            
            var serverCert = new X509Certificate2(certBytes, _selfCertSetting.Password, X509KeyStorageFlags.MachineKeySet);

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