using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.SystemAdmin;

/// <summary>
/// Disables the active shared bootstrap API key (Tentacle or Kubernetes Agent install
/// surface) so the next install-script generation mints a fresh one. Use when
/// suspecting a bootstrap key has leaked.
///
/// <para><b>Effect on already-registered agents</b>: none. Existing Tentacles /
/// K8s agents do NOT carry the bootstrap key after register completes -- they store
/// the server thumbprint and their own machine identity. Rotating only invalidates
/// FUTURE install-script generations using the old key.</para>
///
/// <para><b>Auth</b>: requires <see cref="Permission.AdministerSystem"/> (System
/// Administrator role only). Operators with less privilege use the per-user API key
/// rotation flow.</para>
/// </summary>
[RequiresPermission(Permission.AdministerSystem)]
public class RotateBootstrapApiKeyCommand : ICommand
{
    /// <summary>
    /// Which bootstrap surface to rotate. Match against <c>Surface</c> values in
    /// <c>BootstrapKeySurface</c>: <c>"Tentacle"</c> or <c>"KubernetesAgent"</c>.
    /// Case-insensitive.
    /// </summary>
    public string Surface { get; set; }
}

public class RotateBootstrapApiKeyResponse : SquidResponse<RotateBootstrapApiKeyResponseData>
{
}

public class RotateBootstrapApiKeyResponseData
{
    /// <summary>Description of the rotated key (canonical, for audit-log correlation).</summary>
    public string Description { get; set; }

    /// <summary>Number of previously-active keys disabled (usually 1; 0 on first-ever rotation).</summary>
    public int DisabledCount { get; set; }
}
