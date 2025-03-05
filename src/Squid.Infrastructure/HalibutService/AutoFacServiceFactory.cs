using Halibut.ServiceModel;
using Squid.Core.Extensions;
using Squid.Infrastructure.Communications;

namespace Squid.Infrastructure.HalibutService;

public class AutofacServiceFactory : IServiceFactory, IDisposable
{
    const string TentacleServiceShuttingDownMessage = "The Tentacle service is shutting down and cannot process this request.";
    readonly ILifetimeScope scope;
    readonly Dictionary<string, KnownService> knownServices;

    public AutofacServiceFactory(ILifetimeScope scope, IEnumerable<IAutofacServiceSource> sources)
    {
        this.scope = scope;

        knownServices = sources
            .SelectMany(x => x.KnownServices.EmptyIfNull())
            .ToDictionary(ks => ks.ServiceContractType.Name);
    }

    public IServiceLease CreateService(string serviceName)
    {
        try
        {
            if (knownServices.TryGetValue(serviceName, out var knownService))
            {
                return new Lease(scope.Resolve(knownService.ServiceImplementationType));
            }

            throw new Exception(serviceName);
        }
        catch (ObjectDisposedException)
        {
            throw new Exception(TentacleServiceShuttingDownMessage);
        }
    }

    public IReadOnlyList<Type> RegisteredServiceTypes => knownServices.Values.Select(ks => ks.ServiceContractType).ToList();

    class Lease : IServiceLease
    {
        public Lease(object service)
        {
            Service = service;
        }

        public object Service { get; }

        public void Dispose()
        {
            if (Service is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public void Dispose()
    {
        scope.Dispose();
    }
}