using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Serilog;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Kubernetes;

public sealed class KubernetesLeaderElection : ITentacleBackgroundTask
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RenewDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(2);

    private readonly IKubernetes _client;
    private readonly KubernetesSettings _settings;
    private readonly string _identity;

    private volatile bool _isLeader;

    public KubernetesLeaderElection(IKubernetes client, KubernetesSettings settings, string identity)
    {
        _client = client;
        _settings = settings;
        _identity = identity;
    }

    public string Name => "KubernetesLeaderElection";

    public bool IsLeader => _isLeader;

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Information("Leader election starting. Identity={Identity}, Namespace={Namespace}", _identity, _settings.TentacleNamespace);

        var leaseLock = new LeaseLock(
            _client,
            _settings.TentacleNamespace,
            "squid-tentacle-leader",
            _identity);

        var leaderElector = new LeaderElector(new LeaderElectionConfig(leaseLock)
        {
            LeaseDuration = LeaseDuration,
            RenewDeadline = RenewDeadline,
            RetryPeriod = RetryPeriod
        });

        leaderElector.OnStartedLeading += () =>
        {
            _isLeader = true;
            Log.Information("Became leader. Identity={Identity}", _identity);
        };

        leaderElector.OnStoppedLeading += () =>
        {
            _isLeader = false;
            Log.Information("Lost leadership. Identity={Identity}", _identity);
        };

        leaderElector.OnNewLeader += leader =>
        {
            if (leader != _identity)
                Log.Information("New leader elected: {Leader}", leader);
        };

        try
        {
            await leaderElector.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _isLeader = false;
        }
        catch (Exception ex)
        {
            _isLeader = false;
            Log.Error(ex, "Leader election failed unexpectedly");
        }
    }
}
