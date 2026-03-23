using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.SelfCert;

namespace Squid.Core.Halibut;

public class HalibutModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(ctx =>
        {
            var selfCertSetting = ctx.Resolve<SelfCertSetting>();

            var certBytes = Convert.FromBase64String(selfCertSetting.Base64);
            var serverCert = X509CertificateLoader.LoadPkcs12(certBytes, selfCertSetting.Password, X509KeyStorageFlags.MachineKeySet);

            var services = new DelegateServiceFactory();

            var halibutTimeoutsAndLimits = HalibutTimeoutsAndLimits.RecommendedValues();

            var halibutRuntime = new HalibutRuntimeBuilder()
                .WithServiceFactory(services)
                .WithServerCertificate(serverCert)
                .WithHalibutTimeoutsAndLimits(halibutTimeoutsAndLimits)
                .Build();

            Log.Information("HalibutRuntime created. ServerCertThumbprint={Thumbprint}", serverCert.Thumbprint);

            StartPollingListenerIfEnabled(ctx, halibutRuntime);

            return halibutRuntime;
            
        }).As<HalibutRuntime>().SingleInstance();

        builder.RegisterType<HalibutTrustInitializer>().AsSelf().As<IStartable>().SingleInstance();
    }

    private static void StartPollingListenerIfEnabled(IComponentContext ctx, HalibutRuntime halibutRuntime)
    {
        if (!ctx.TryResolve<HalibutSetting>(out var halibutSetting))
        {
            Log.Warning("HalibutSetting not found in configuration. Polling listener will NOT start");
            return;
        }

        if (!halibutSetting.Polling.Enabled)
        {
            Log.Information("Halibut polling is disabled (Halibut:Polling:Enabled=false). Agents cannot connect via polling");
            return;
        }

        var port = halibutSetting.Polling.Port;

        halibutRuntime.Listen(port);

        Log.Information("Halibut polling listener started on port {Port}", port);
    }
}
