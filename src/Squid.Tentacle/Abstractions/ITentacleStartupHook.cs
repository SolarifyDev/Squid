namespace Squid.Tentacle.Abstractions;

public interface ITentacleStartupHook
{
    string Name { get; }

    Task RunAsync(CancellationToken ct);
}
