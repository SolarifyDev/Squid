using k8s;
using Squid.Tentacle.Watchdog;

// Signal handling
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Required env vars
var directory = Environment.GetEnvironmentVariable("WATCHDOG_DIRECTORY")
    ?? throw new InvalidOperationException("Missing WATCHDOG_DIRECTORY");
var podName = Environment.GetEnvironmentVariable("HOSTNAME")
    ?? throw new InvalidOperationException("Missing HOSTNAME");

// Optional env vars with defaults
var loopSeconds = ParseDouble("WATCHDOG_LOOP_SECONDS", 5);
var initialBackoff = ParseDouble("WATCHDOG_INITIAL_BACKOFF_SECONDS", 0.5);
var timeout = ParseDouble("WATCHDOG_TIMEOUT_SECONDS", 10);

// K8s client + namespace
var k8sConfig = KubernetesClientConfiguration.IsInCluster()
    ? KubernetesClientConfiguration.InClusterConfig()
    : KubernetesClientConfiguration.BuildConfigFromConfigFile();
var k8sClient = new Kubernetes(k8sConfig);

var podNamespace = File.ReadAllText("/var/run/secrets/kubernetes.io/serviceaccount/namespace").Trim();
var terminator = new PodTerminator(k8sClient, podName, podNamespace);

Console.WriteLine($"Starting NFS Watchdog. Directory={directory}, Loop={loopSeconds}s, Timeout={timeout}s");

// Main ticker loop (aligned with Squid ticker)
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(loopSeconds));

while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
{
    Console.WriteLine("Checking for read access...");

    var healthy = await RetryWithBackoffAsync(
        () => NfsHealthChecker.CheckFilesystem(directory),
        TimeSpan.FromSeconds(initialBackoff),
        TimeSpan.FromSeconds(timeout),
        cts.Token).ConfigureAwait(false);

    if (!healthy)
    {
        Console.Error.WriteLine("NFS mount corrupted after retry timeout. Terminating pod.");
        await terminator.TerminateAsync(cts.Token).ConfigureAwait(false);
        return;
    }
}

// Exponential backoff retry (aligned with cenkalti/backoff ExponentialBackOff)
static async Task<bool> RetryWithBackoffAsync(Func<bool> check, TimeSpan initialBackoff, TimeSpan maxElapsed, CancellationToken ct)
{
    var deadline = DateTime.UtcNow + maxElapsed;
    var backoff = initialBackoff;

    while (DateTime.UtcNow < deadline)
    {
        ct.ThrowIfCancellationRequested();

        if (check()) return true;

        var remaining = deadline - DateTime.UtcNow;
        var delay = backoff < remaining ? backoff : remaining;
        if (delay <= TimeSpan.Zero) break;

        await Task.Delay(delay, ct).ConfigureAwait(false);
        backoff = backoff < maxElapsed ? backoff * 2 : maxElapsed;
    }

    // Final check after timeout
    return check();
}

static double ParseDouble(string envVar, double defaultValue)
{
    var value = Environment.GetEnvironmentVariable(envVar);
    return double.TryParse(value, out var result) ? result : defaultValue;
}
