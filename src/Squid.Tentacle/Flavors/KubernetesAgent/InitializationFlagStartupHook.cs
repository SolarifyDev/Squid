using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Flavors.KubernetesAgent;

public sealed class InitializationFlagStartupHook : ITentacleStartupHook
{
    private readonly string _flagPath;

    public InitializationFlagStartupHook(string flagPath = "/squid/initialized")
    {
        _flagPath = flagPath;
    }

    public string Name => "InitializationFlag";

    public Task RunAsync(CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_flagPath);

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                File.Create(_flagPath).Dispose();
        }
        catch
        {
            // Not running in K8s — skip
        }

        return Task.CompletedTask;
    }
}
