using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Squid.Tentacle.Commands;

/// <summary>
/// Prints the binary's assembly version to stdout and exits 0.
/// </summary>
/// <remarks>
/// <para>Added to close a CLI gap: <c>--version</c> falls into the default
/// <see cref="RunCommand"/> (anything starting with <c>-</c> is treated as
/// a config flag by <see cref="CommandResolver"/>), which starts the agent
/// instead of reporting version — unexpected for anyone running it from
/// the shell to check "did the upgrade land?".</para>
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
        var asmVersion = Assembly.GetExecutingAssembly().GetName().Version;

        if (asmVersion == null)
        {
            Console.WriteLine("unknown");
            return Task.FromResult(0);
        }

        // AssemblyVersion is Major.Minor.Build.Revision. We build with
        // `-p:Version=1.3.8` which populates Build as the patch number and
        // leaves Revision=0. Trim .Revision for canonical semver output.
        var canonical = asmVersion.Revision == 0
            ? $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}"
            : asmVersion.ToString();

        Console.WriteLine(canonical);

        return Task.FromResult(0);
    }
}
