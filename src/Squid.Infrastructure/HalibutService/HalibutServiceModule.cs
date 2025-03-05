using Squid.Core.Attributes;
using Squid.Infrastructure.Communications;

namespace Squid.Infrastructure.HalibutService;

public class HalibutServiceModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        var allTypes = ThisAssembly.GetTypes();
        RegisterHalibutServices(builder, allTypes);
    }
    
    static void RegisterHalibutServices(ContainerBuilder builder, IEnumerable<Type> allTypes)
    {
        var knownServices = allTypes
            .SelectMany(t => t.GetCustomAttributes<ServiceAttribute>().Select(attr => (ServiceImplementationType: t, ServiceAttribute: attr)))
            .Select(x => new KnownService(x.ServiceImplementationType, x.ServiceAttribute!.ContractType))
            .ToArray();

        var knownServiceSources = new KnownServiceSource(knownServices);
        builder.RegisterInstance(knownServiceSources).AsImplementedInterfaces();

        //register all halibut services with the root container
        foreach (var knownServiceSource in knownServices)
        {
            builder
                .RegisterType(knownServiceSource.ServiceImplementationType)
                .AsSelf()
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}