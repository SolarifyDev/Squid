using Squid.Tentacle.Configuration;
using Squid.Tentacle.Instance;

namespace Squid.Tentacle.Commands;

/// <summary>
/// Fills in <c>CertsPath</c> / <c>WorkspacePath</c> on <see cref="TentacleSettings"/>
/// when they were left blank by the config layers (appsettings.json / env / CLI).
/// Extracted as a static helper rather than inlined in <see cref="RunCommand"/>
/// so it's unit-testable without standing up a real <c>TentacleApp</c>.
/// </summary>
/// <remarks>
/// <para>Historical context: the class defaults used to be <c>/squid/certs</c>
/// and <c>/squid/work</c> — Docker-image conventions that worked there (the
/// image pre-creates those dirs with correct perms) but silently broke native
/// systemd installs. When the <c>squid-tentacle</c> service user (non-root)
/// tried to <c>CreateDirectory("/squid/certs")</c>, Linux returned
/// <c>EACCES</c> and the agent crashed on startup. Leaving the defaults
/// blank + falling back here to the per-platform instance paths eliminates
/// that trap entirely.</para>
///
/// <para>Explicit values from any config source (appsettings.json, env var,
/// CLI flag, persisted instance config) always win — this helper only fills
/// the gap when nothing else supplied a value.</para>
/// </remarks>
public static class RunCommandPathResolver
{
    /// <summary>
    /// Mutates <paramref name="settings"/> to fill in empty path fields from
    /// the instance layout. <paramref name="resolveCertsPath"/> is a seam so
    /// tests can assert the fallback without depending on real
    /// <see cref="PlatformPaths"/> state; production callers pass
    /// <see cref="InstanceSelector.ResolveCertsPath"/>.
    /// </summary>
    public static void FillMissingPaths(TentacleSettings settings, InstanceRecord instance, Func<InstanceRecord, string> resolveCertsPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(resolveCertsPath);

        string certsPath = null;

        if (string.IsNullOrWhiteSpace(settings.CertsPath))
        {
            certsPath = resolveCertsPath(instance);
            settings.CertsPath = certsPath;
        }

        if (string.IsNullOrWhiteSpace(settings.WorkspacePath))
        {
            // Workspace lives as a sibling of the certs dir under the instance
            // folder: .../instances/{name}/work. Keeps per-instance state
            // self-contained so a future `uninstall --purge` can rm -rf one
            // tree without special-casing workspace cleanup.
            certsPath ??= settings.CertsPath;

            var instanceDir = Path.GetDirectoryName(certsPath);

            settings.WorkspacePath = string.IsNullOrEmpty(instanceDir)
                ? certsPath
                : Path.Combine(instanceDir, "work");
        }
    }
}
