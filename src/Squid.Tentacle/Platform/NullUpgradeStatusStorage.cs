namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.2 — fallback impl for unsupported platforms (macOS,
/// FreeBSD, etc.) and unit-test contexts. Always returns empty strings.
///
/// <para>Server treats empty upgrade metadata as "no status available"
/// and falls back to inferring outcome from the reported agent version
/// — graceful degradation, not an error.</para>
/// </summary>
public sealed class NullUpgradeStatusStorage : IUpgradeStatusStorage
{
    public string ReadStatus() => string.Empty;
    public string ReadEvents() => string.Empty;
    public string ReadLog() => string.Empty;
}
