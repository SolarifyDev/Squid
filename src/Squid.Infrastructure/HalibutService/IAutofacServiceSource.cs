using Squid.Infrastructure.Communications;

namespace Squid.Infrastructure.HalibutService;

public interface IAutofacServiceSource
{
    IEnumerable<KnownService> KnownServices { get; }
}