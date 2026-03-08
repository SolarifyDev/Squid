using k8s;
using Serilog;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Kubernetes;

public sealed class ClusterVersionDetector : ITentacleStartupHook
{
    private const string MinimumSupportedVersion = "1.25";

    private readonly IKubernetes _client;

    public ClusterVersionDetector(IKubernetes client)
    {
        _client = client;
    }

    public string Name => "ClusterVersionDetection";

    public string DetectedVersion { get; private set; }

    public Dictionary<string, string> Metadata { get; } = new();

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var version = await _client.Version.GetCodeAsync(ct).ConfigureAwait(false);

            DetectedVersion = version.GitVersion;
            Metadata["kubernetes.version"] = version.GitVersion ?? "unknown";
            Metadata["kubernetes.platform"] = version.Platform ?? "unknown";

            Log.Information("Connected to Kubernetes {GitVersion} ({Platform})", version.GitVersion, version.Platform);

            WarnIfBelowMinimum(version.GitVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to detect Kubernetes cluster version");
        }
    }

    internal static void WarnIfBelowMinimum(string gitVersion)
    {
        if (string.IsNullOrEmpty(gitVersion)) return;

        var versionPart = gitVersion.TrimStart('v').Split('-')[0];

        if (!Version.TryParse(versionPart, out var parsed)) return;

        if (!Version.TryParse(MinimumSupportedVersion, out var minimum)) return;

        if (parsed < minimum)
            Log.Warning("Kubernetes version {Version} is below minimum supported {Minimum}", gitVersion, MinimumSupportedVersion);
    }
}
