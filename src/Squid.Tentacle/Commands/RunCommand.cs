using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Core;
using Squid.Tentacle.Instance;

namespace Squid.Tentacle.Commands;

public sealed class RunCommand : ITentacleCommand
{
    public string Name => "run";
    public string Description => "Start the Tentacle agent (default command)";

    public async Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        // Extract `--instance NAME` so we know which instance dir to fall back to
        // for path resolution. Program.cs also consumes this for config-file lookup;
        // doing it again here is cheap and keeps RunCommand independent of upstream
        // parsing order (tests, Docker entrypoints, etc. may invoke us directly).
        var (instanceName, _) = InstanceSelector.ExtractInstanceArg(args);
        var instance = InstanceSelector.Resolve(instanceName);

        var tentacleSettings = TentacleApp.LoadTentacleSettings(config);

        // Defense-in-depth: if CertsPath / WorkspacePath arrived empty (fresh
        // install, no persisted instance config, or operator cleared the value),
        // resolve them via the same per-platform layout RegisterCommand uses.
        // Without this the agent would fall through to the legacy "/squid/certs"
        // hardcode and crash with UnauthorizedAccessException on non-root systemd
        // installs. See RunCommandPathResolver for the detailed rationale.
        RunCommandPathResolver.FillMissingPaths(tentacleSettings, instance, InstanceSelector.ResolveCertsPath);

        var app = new TentacleApp();
        await app.RunAsync(tentacleSettings, config, ct).ConfigureAwait(false);

        return 0;
    }
}
