using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Core;

namespace Squid.Tentacle.Commands;

/// <summary>
/// Prints the binary's assembly version to stdout and exits 0.
/// </summary>
/// <remarks>
/// <para>Added to close a CLI gap: <c>--version</c> used to fall into the default
/// <see cref="RunCommand"/> (anything starting with <c>-</c> is treated as a config flag
/// by <see cref="CommandResolver"/>), which started the agent instead of reporting a
/// version — unexpected for anyone running it from the shell to check "did the upgrade
/// land?", and a hang for anyone piping the output. <c>--version</c> is now routed here
/// too, via <see cref="CommandResolver.IsVersionFlag"/>.</para>
///
/// <para>Callers that must also work against OLDER binaries — notably
/// <c>deploy/packaging/after-install.sh</c>, which may probe a not-yet-replaced tentacle
/// mid-upgrade — should keep using the bare <c>version</c> verb, which has always worked.
/// The alias helps humans and new tooling, it does not make old binaries safe to probe
/// with a flag.</para>
///
/// <para>Called by the upgrade script's post-restart version verify step
/// AND available to operators for ad-hoc checks:
/// <code>
/// $ squid-tentacle version
/// 1.3.8
/// </code></para>
///
/// <para>Reads <see cref="AssemblyName.Version"/> off the executing
/// assembly (baked in at <c>dotnet publish -p:Version=X.Y.Z</c> time by
/// <c>build-publish-linux-tentacle.yml</c>). Strips the trailing
/// <c>.0</c> revision to produce canonical semver (<c>1.3.8</c> not
/// <c>1.3.8.0</c>) so the string matches what the server asks for in
/// <c>TARGET_VERSION</c>.</para>
/// </remarks>
public sealed class VersionCommand : ITentacleCommand
{
    public string Name => "version";
    public string Description => "Prints the binary version and exits";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        // AssemblyVersion.Canonical is the single source of truth for how
        // this binary reports its version — same string also surfaces in
        // the Capabilities RPC response (server's /upgrade-info endpoint)
        // and in deployment audit manifests. Keeping one helper means CLI
        // output == what the server sees == what the FE renders.
        Console.WriteLine(AssemblyVersion.Canonical);
        return Task.FromResult(0);
    }
}
