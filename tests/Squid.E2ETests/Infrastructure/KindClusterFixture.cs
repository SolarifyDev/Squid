using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Squid.E2ETests.Infrastructure;

/// <summary>
/// Manages a kind (Kubernetes in Docker) cluster lifecycle for E2E tests.
/// Creates the cluster once per test collection and tears it down afterward.
///
/// Prerequisites:
///   - Docker running
///   - kind CLI installed (https://kind.sigs.k8s.io/)
///   - kubectl CLI installed
/// </summary>
public class KindClusterFixture : IAsyncLifetime
{
    public const string ClusterName = "squid-e2e";
    public string Kubeconfig { get; private set; }

    public async Task InitializeAsync()
    {
        // Check if cluster already exists
        var existing = await RunProcessAsync("kind", $"get clusters");
        if (existing.Output.Contains(ClusterName))
        {
            // Cluster already running, reuse it
            Kubeconfig = await GetKubeconfigAsync();
            return;
        }

        // Create kind cluster
        var result = await RunProcessAsync("kind", $"create cluster --name {ClusterName} --wait 60s");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to create kind cluster: {result.Error}");

        Kubeconfig = await GetKubeconfigAsync();

        // Wait for cluster to be ready
        await WaitForClusterReadyAsync();
    }

    public async Task DisposeAsync()
    {
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

    private async Task<string> GetKubeconfigAsync()
    {
        var result = await RunProcessAsync("kind", $"get kubeconfig --name {ClusterName}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to get kubeconfig: {result.Error}");

        var path = Path.Combine(Path.GetTempPath(), $"squid-e2e-kubeconfig-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, result.Output);
        return path;
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

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments)
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

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, output.Trim(), error.Trim());
    }

    private record ProcessResult(int ExitCode, string Output, string Error);
}

[CollectionDefinition("KindCluster")]
public class KindClusterCollection : ICollectionFixture<KindClusterFixture>
{
}
