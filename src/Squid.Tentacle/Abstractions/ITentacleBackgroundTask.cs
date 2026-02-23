namespace Squid.Tentacle.Abstractions;

public interface ITentacleBackgroundTask
{
    string Name { get; }

    Task RunAsync(CancellationToken ct);
}
