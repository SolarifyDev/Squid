using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Core;
using Serilog;

namespace Squid.Tentacle.Commands;

public sealed class RunCommand : ITentacleCommand
{
    public string Name => "run";
    public string Description => "Start the Tentacle agent (default command)";

    public async Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var tentacleSettings = TentacleApp.LoadTentacleSettings(config);

        var app = new TentacleApp();
        await app.RunAsync(tentacleSettings, config, ct).ConfigureAwait(false);

        return 0;
    }
}
