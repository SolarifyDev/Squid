using System.Runtime.InteropServices;

namespace Squid.Tentacle.ServiceHost;

/// <summary>
/// Picks the correct <see cref="IServiceHost"/> implementation for the current OS.
/// </summary>
///
/// <remarks>
/// <para>Uses a registry pattern so adding a new service host (e.g. FreeBSD rc.d,
/// Solaris SMF) is a one-line change here — no edits to per-platform branching
/// elsewhere, no new <c>if</c>/<c>switch</c> arm to forget. Each
/// <see cref="IServiceHost"/> is sole authority on whether it applies to the
/// current platform via its <see cref="IServiceHost.IsSupported"/> property,
/// so the factory stays ignorant of OS details.</para>
/// <para>Ordering defines priority: the first candidate whose
/// <see cref="IServiceHost.IsSupported"/> is <c>true</c> wins. Systemd comes
/// first because it's the most deployed target; Windows and launchd follow.</para>
/// </remarks>
public static class ServiceHostFactory
{
    /// <summary>
    /// Candidate hosts in priority order. Registering a new platform = add one entry.
    /// Internal so tests can inspect / extend if needed.
    /// </summary>
    internal static readonly IReadOnlyList<Func<IServiceHost>> Candidates =
    [
        () => new SystemdServiceHost(),
        () => new WindowsServiceHost(),
        () => new LaunchdServiceHost()
    ];

    /// <summary>
    /// Returns the first registered <see cref="IServiceHost"/> whose
    /// <see cref="IServiceHost.IsSupported"/> is <c>true</c> on the current platform.
    /// Throws <see cref="PlatformNotSupportedException"/> when nothing matches.
    /// </summary>
    public static IServiceHost Resolve()
    {
        foreach (var factory in Candidates)
        {
            var host = factory();

            if (host.IsSupported)
                return host;
        }

        throw new PlatformNotSupportedException(
            $"No IServiceHost implementation matches this platform ({RuntimeInformation.OSDescription}). " +
            "Register one in ServiceHostFactory.Candidates.");
    }
}
