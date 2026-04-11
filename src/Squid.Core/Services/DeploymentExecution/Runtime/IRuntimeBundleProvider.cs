using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Runtime;

/// <summary>
/// Resolves the appropriate <see cref="IRuntimeBundle"/> for a given scripting
/// language. Consumed by transport renderers (today <c>SshIntentRenderer</c>) when
/// <see cref="Intents.RunScriptIntent.InjectRuntimeBundle"/> is <c>true</c>.
/// </summary>
public interface IRuntimeBundleProvider : IScopedDependency
{
    /// <summary>
    /// Returns the bundle for <paramref name="kind"/>.
    /// </summary>
    /// <exception cref="Exceptions.RuntimeBundleNotFoundException">
    /// Thrown when no <see cref="IRuntimeBundle"/> is registered for the requested kind.
    /// </exception>
    IRuntimeBundle GetBundle(RuntimeBundleKind kind);
}
