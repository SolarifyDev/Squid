using Squid.Core.Extensions;

namespace Squid.Infrastructure.HalibutService;

public class KnownService
{
    public Type ServiceImplementationType { get; }

    public Type ServiceContractType { get; }

    public KnownService(Type serviceImplementationType, Type serviceContractType)
    {
        if (serviceImplementationType.IsInterface || serviceImplementationType.IsAbstract || serviceImplementationType.GetInterfaces().IsNullOrEmpty())
        {
            throw new ArgumentException("The service implementation type must be a non-abstract class that implements at least one interface.", nameof(serviceImplementationType));
        }

        if (!serviceContractType.IsInterface)
        {
            throw new ArgumentException("The service contract type must be an interface.", nameof(serviceContractType));
        }

        ServiceImplementationType = serviceImplementationType;
        ServiceContractType = serviceContractType;
    }
}