using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Runtime;

/// <summary>
/// A scripting-language-specific helper bundle. Each implementation knows how to wrap
/// a user script with the squid-runtime helper functions (<c>set_squidvariable</c>,
/// <c>new_squidartifact</c>, <c>fail_step</c>, ...) and environment variable exports
/// required to round-trip output variables via <see cref="Script.ServiceMessages.ServiceMessageParser"/>.
/// </summary>
public interface IRuntimeBundle : IScopedDependency
{
    /// <summary>The scripting language this bundle targets.</summary>
    RuntimeBundleKind Kind { get; }

    /// <summary>
    /// Returns a wrapped script containing, in order: shebang / language header,
    /// squid environment variables, exported deployment variables, the embedded
    /// runtime helper functions, and finally the user script body.
    /// </summary>
    string Wrap(RuntimeBundleWrapContext context);
}
