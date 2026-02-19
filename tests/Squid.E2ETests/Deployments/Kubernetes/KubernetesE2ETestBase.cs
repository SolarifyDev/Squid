using Squid.E2ETests.Infrastructure;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public abstract class KubernetesE2ETestBase
{
    protected readonly KindClusterFixture Cluster;

    protected KubernetesE2ETestBase(KindClusterFixture cluster)
    {
        Cluster = cluster;
    }

    protected async Task<string> GetClusterUrlAsync()
    {
        var output = await Cluster.KubectlAsync("config view --minify -o jsonpath='{.clusters[0].cluster.server}'")
            .ConfigureAwait(false);

        return output.Trim('\'');
    }

    protected async Task<string> GetServiceAccountTokenAsync()
    {
        const string sa = "squid-e2e-admin";
        const string ns = "kube-system";

        try { await Cluster.KubectlAsync($"create serviceaccount {sa} -n {ns}").ConfigureAwait(false); } catch { }
        try { await Cluster.KubectlAsync($"create clusterrolebinding {sa}-binding --clusterrole=cluster-admin --serviceaccount={ns}:{sa}").ConfigureAwait(false); } catch { }

        var token = await Cluster.KubectlAsync($"create token {sa} -n {ns} --duration=3600s")
            .ConfigureAwait(false);

        return token.Trim();
    }

    protected static async Task ExecuteBashAsync(string command)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync().ConfigureAwait(false);
    }

    protected static async Task<ScriptResult> ExecuteBashScriptAsync(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"squid-e2e-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptPath, script).ConfigureAwait(false);

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = scriptPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            return new ScriptResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    protected record ScriptResult(int ExitCode, string StdOut, string StdErr);
}
