namespace Squid.Tentacle.Instance;

/// <summary>
/// Metadata about a single Tentacle instance. Serialised into <c>instances.json</c>.
/// </summary>
/// <remarks>
/// An "instance" is an independent Tentacle configuration — separate certs, separate
/// Server binding, separate systemd unit. One host can run multiple instances side-by-side
/// (useful for dev/prod split or for tentacles serving different Squid Servers).
/// </remarks>
public sealed class InstanceRecord
{
    /// <summary>Default instance name used when <c>--instance</c> is omitted.</summary>
    public const string DefaultName = "Default";

    public string Name { get; set; } = DefaultName;

    /// <summary>Absolute path to this instance's config.json file.</summary>
    public string ConfigPath { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
