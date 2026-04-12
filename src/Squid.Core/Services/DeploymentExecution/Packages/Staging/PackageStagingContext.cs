using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Packages.Staging;

/// <summary>
/// Abstract base for transport-scoped package staging context. Derived records
/// (e.g. <c>SshPackageStagingContext</c>) carry any transport-specific handles
/// the concrete handlers need — for example an active SSH connection scope.
/// Handlers match on the concrete subtype via <see cref="IPackageStagingHandler.CanHandle"/>.
/// </summary>
/// <param name="CommunicationStyle">The target transport style (SSH, KubernetesApi, ...).</param>
/// <param name="BaseDirectory">The resolved remote base directory where packages are staged.</param>
public abstract record PackageStagingContext(
    CommunicationStyle CommunicationStyle,
    string BaseDirectory);
