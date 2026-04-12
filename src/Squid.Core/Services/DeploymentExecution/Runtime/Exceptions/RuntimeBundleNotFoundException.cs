namespace Squid.Core.Services.DeploymentExecution.Runtime.Exceptions;

/// <summary>
/// Raised by <see cref="IRuntimeBundleProvider.GetBundle"/> when no bundle is registered
/// for the requested <see cref="RuntimeBundleKind"/>. Indicates a DI wiring gap, not a
/// user error.
/// </summary>
public sealed class RuntimeBundleNotFoundException : InvalidOperationException
{
    public RuntimeBundleNotFoundException(RuntimeBundleKind kind)
        : base($"No IRuntimeBundle is registered for kind '{kind}'. Ensure the corresponding bundle implementation is registered via IScopedDependency.")
    {
        Kind = kind;
    }

    public RuntimeBundleKind Kind { get; }
}
