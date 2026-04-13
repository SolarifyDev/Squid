using Microsoft.Extensions.Configuration;

namespace Squid.Tentacle.Commands;

public interface ITentacleCommand
{
    string Name { get; }
    string Description { get; }
    Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct);
}
