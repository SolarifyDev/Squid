using Squid.Infrastructure.HalibutService;

namespace Squid.Infrastructure.Communications
{
    public class KnownServiceSource : IAutofacServiceSource
    {
        public KnownServiceSource(params KnownService[] serviceTypes)
        {
            KnownServices = serviceTypes;
        }

        public IEnumerable<KnownService> KnownServices { get; }
    }
}