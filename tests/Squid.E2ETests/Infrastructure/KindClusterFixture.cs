using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Squid.E2ETests.Infrastructure;

/// <summary>
/// Manages a Kubernetes cluster lifecycle for E2E tests.
///
/// Configuration (appsettings.json → E2E section):
///   - KubeconfigPath: path to an external kubeconfig (K3s, etc.) — skips Kind entirely
///   - When empty: creates a Kind cluster locally (requires Docker + kind CLI + kubectl CLI)
/// </summary>
public class KindClusterFixture : IAsyncLifetime
{
    public const string ClusterName = "squid-e2e";
    public string Kubeconfig { get; private set; }

    private bool _isExternalCluster;

    // Dedicated kubeconfig path for kind — avoids merging into (potentially corrupted) ~/.kube/config
    private static readonly string KindKubeconfigPath =
        Path.Combine(Path.GetTempPath(), "squid-e2e-kind.yaml");

    public async Task InitializeAsync()
    {
        var config = LoadConfiguration();
        var externalKubeconfig = config["E2E:KubeconfigPath"];

        if (!string.IsNullOrEmpty(externalKubeconfig))
        {
            _isExternalCluster = true;
            Kubeconfig = externalKubeconfig;
            File.Copy(externalKubeconfig, DefaultKubeconfigPath, overwrite: true);
            await WaitForClusterReadyAsync();
            return;
        }

        // Check if cluster already exists
        var existing = await RunProcessAsync("kind", $"get clusters");
        if (existing.Output.Contains(ClusterName))
        {
            // Cluster already running, reuse it
            Kubeconfig = await GetKubeconfigAsync();
            return;
        }

        // Create kind cluster — KUBECONFIG points to a dedicated file to avoid
        // merging into the system kubeconfig (which may have duplicate keys)
        var result = await RunProcessAsync(
            "kind", $"create cluster --name {ClusterName} --wait 60s",
            ("KUBECONFIG", KindKubeconfigPath));

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to create kind cluster: {result.Error}");

        Kubeconfig = await GetKubeconfigAsync();

        // Wait for cluster to be ready
        await WaitForClusterReadyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_isExternalCluster) return;

        // Optionally delete cluster. Keep it if SQUID_KEEP_CLUSTER=true for debugging
        var keep = Environment.GetEnvironmentVariable("SQUID_KEEP_CLUSTER");
        if (string.Equals(keep, "true", StringComparison.OrdinalIgnoreCase))
            return;

        await RunProcessAsync("kind", $"delete cluster --name {ClusterName}");
    }

    public async Task<string> KubectlAsync(string args)
    {
        var result = await RunProcessAsync("kubectl", $"--kubeconfig {Kubeconfig} {args}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"kubectl {args} failed: {result.Error}");
        return result.Output;
    }

    // Fixed path consumed by TentacleStub and SquidAgentE2EFixture (SQUID_E2E_KUBECONFIG fallback)
    public static readonly string DefaultKubeconfigPath =
        Path.Combine(Path.GetTempPath(), "squid-e2e-kubeconfig.yaml");

    private async Task<string> GetKubeconfigAsync()
    {
        var result = await RunProcessAsync("kind", $"get kubeconfig --name {ClusterName}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to get kubeconfig: {result.Error}");

        await File.WriteAllTextAsync(DefaultKubeconfigPath, result.Output);
        return DefaultKubeconfigPath;
    }

    private async Task WaitForClusterReadyAsync()
    {
        var timeout = TimeSpan.FromSeconds(120);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                var result = await RunProcessAsync("kubectl",
                    $"--kubeconfig {Kubeconfig} get nodes -o jsonpath='{{.items[0].status.conditions[-1:].type}}'");
                if (result.Output.Contains("Ready"))
                    return;
            }
            catch
            {
                // Cluster not ready yet
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException("Kind cluster did not become ready within 120 seconds");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName, string arguments,
        params (string Key, string Value)[] envOverrides)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var (key, value) in envOverrides)
            process.StartInfo.Environment[key] = value;

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, output.Trim(), error.Trim());
    }

    private static IConfiguration LoadConfiguration()
    {
        var basePath = Path.GetDirectoryName(typeof(KindClusterFixture).Assembly.Location)!;

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
    }

    private record ProcessResult(int ExitCode, string Output, string Error);
}

[CollectionDefinition("KindCluster")]
public class KindClusterCollection : ICollectionFixture<KindClusterFixture>
{
}
