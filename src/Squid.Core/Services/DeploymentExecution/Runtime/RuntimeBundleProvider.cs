using Squid.Core.Services.DeploymentExecution.Runtime.Exceptions;

namespace Squid.Core.Services.DeploymentExecution.Runtime;

/// <summary>
/// Default <see cref="IRuntimeBundleProvider"/> — indexes the registered bundles by
/// <see cref="IRuntimeBundle.Kind"/> at construction time and throws
/// <see cref="RuntimeBundleNotFoundException"/> on unknown kinds. If two bundles report
/// the same kind, the last one wins (deterministic by DI registration order).
/// </summary>
public class RuntimeBundleProvider : IRuntimeBundleProvider
{
    private readonly IReadOnlyDictionary<RuntimeBundleKind, IRuntimeBundle> _bundles;

    public RuntimeBundleProvider(IEnumerable<IRuntimeBundle> bundles)
    {
        ArgumentNullException.ThrowIfNull(bundles);

        _bundles = bundles
            .GroupBy(b => b.Kind)
            .ToDictionary(g => g.Key, g => g.Last());
    }

    public IRuntimeBundle GetBundle(RuntimeBundleKind kind)
    {
        if (!_bundles.TryGetValue(kind, out var bundle))
            throw new RuntimeBundleNotFoundException(kind);

        return bundle;
    }
}
