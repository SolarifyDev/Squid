using Squid.Core.Services.DeploymentExecution.Packages.Staging;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Ssh.Packages;

/// <summary>
/// SSH-specific package staging context. Carries the active
/// <see cref="ISshConnectionScope"/> so handlers can read/write through the
/// existing SFTP/SSH session without having to re-establish it.
/// </summary>
/// <param name="Scope">The per-target SSH connection scope held open by the execution strategy.</param>
/// <param name="BaseDirectory">The resolved remote base directory (e.g. <c>/home/deploy/.squid</c>).</param>
public sealed record SshPackageStagingContext(ISshConnectionScope Scope, string BaseDirectory)
    : PackageStagingContext(CommunicationStyle.Ssh, BaseDirectory);
