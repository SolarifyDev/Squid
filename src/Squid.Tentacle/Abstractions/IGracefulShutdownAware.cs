namespace Squid.Tentacle.Abstractions;

public interface IGracefulShutdownAware
{
    Task WaitForDrainAsync(TimeSpan timeout);
}
