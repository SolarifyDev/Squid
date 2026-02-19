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
            var selfCertBase64 = selfCertSetting.Base64;

            if (string.IsNullOrEmpty(selfCertBase64))
                throw new InvalidOperationException("缺少HALIBUT_CERT_BASE64环境变量");

            var certBytes = Convert.FromBase64String(selfCertBase64);
            var serverCert = new X509Certificate2(certBytes, selfCertSetting.Password, X509KeyStorageFlags.MachineKeySet);

            var services = new DelegateServiceFactory();

            var halibutTimeoutsAndLimits = HalibutTimeoutsAndLimits.RecommendedValues();

            var halibutRuntime = new HalibutRuntimeBuilder()
                .WithServiceFactory(services)
                .WithServerCertificate(serverCert)
                .WithHalibutTimeoutsAndLimits(halibutTimeoutsAndLimits)
                .Build();

            StartPollingListenerIfEnabled(ctx, halibutRuntime);

            return halibutRuntime;
        }).As<HalibutRuntime>().SingleInstance();
    }

    private static void StartPollingListenerIfEnabled(IComponentContext ctx, HalibutRuntime halibutRuntime)
    {
        if (!ctx.TryResolve<PollingListenerSetting>(out var pollingSetting))
            return;

        if (!pollingSetting.Enabled) return;

        var port = pollingSetting.Port;

        halibutRuntime.Listen(port);

        Log.Information("Halibut polling listener started on port {Port}", port);
    }
}
