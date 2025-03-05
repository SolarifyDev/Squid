using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Attributes;
using Squid.Core.Extensions;
using Squid.Infrastructure.Communications;
using Squid.Infrastructure.HalibutService;

namespace Squid.Infrastructure.Halibut;

public class HalibutModule : Module
{
    private readonly ILogger _logger;
    
    public HalibutModule(ILogger logger)
    {
        _logger = logger;
    }

    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<AutofacServiceFactory>().AsImplementedInterfaces().SingleInstance();
        
        builder.Register(c =>
        {
            var services = c.Resolve<IServiceFactory>();
            var serverCert = new X509Certificate2();

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