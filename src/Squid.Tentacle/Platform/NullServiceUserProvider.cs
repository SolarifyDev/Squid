namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.A.3 — fallback impl for macOS / unsupported platforms /
/// test contexts. Always reports "no service user concept here" —
/// callers that depend on service-user semantics short-circuit cleanly
/// without OS branching.
///
/// <para><c>TrySetOwnership</c> returns true (no-op success) so generic
/// cross-platform call sites don't log spurious failures.</para>
/// </summary>
public sealed class NullServiceUserProvider : IServiceUserProvider
{
    public string DefaultServiceUser => string.Empty;

    public bool IsRunningElevated() => false;

    public bool ServiceUserExists(string user) => false;

    public bool TrySetOwnership(string path, string user)
    {
        _ = path;
        _ = user;
        return true;
    }
}
