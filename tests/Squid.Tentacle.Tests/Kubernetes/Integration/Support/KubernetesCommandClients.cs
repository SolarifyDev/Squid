using System.Text;
using Squid.Tentacle.Tests.Support.Process;

namespace Squid.Tentacle.Tests.Kubernetes.Integration.Support;

public sealed class KindClient
{
    private readonly string _workingDirectory;

    public KindClient(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public Task<CommandResult> CreateClusterAsync(string clusterName, CancellationToken ct)
        => CommandRunner.RunAsync("kind", $"create cluster --name {Quote(clusterName)}", _workingDirectory, ct);

    public Task<CommandResult> DeleteClusterAsync(string clusterName, CancellationToken ct)
        => CommandRunner.RunAsync("kind", $"delete cluster --name {Quote(clusterName)}", _workingDirectory, ct);

    private static string Quote(string value) => $"\"{value}\"";
}

public sealed class KubectlClient
{
    private readonly string _workingDirectory;

    public KubectlClient(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public Task<CommandResult> CreateNamespaceIfMissingAsync(string ns, CancellationToken ct)
        => CommandRunner.RunAsync(
            "kubectl",
            $"create namespace {Quote(ns)} --dry-run=client -o yaml",
            _workingDirectory,
            ct);

    public Task<CommandResult> ApplyAsync(string yamlPath, CancellationToken ct)
        => CommandRunner.RunAsync("kubectl", $"apply -f {Quote(yamlPath)}", _workingDirectory, ct);

    public Task<CommandResult> WaitDeploymentAvailableAsync(string ns, string deploymentName, TimeSpan timeout, CancellationToken ct)
        => CommandRunner.RunAsync(
            "kubectl",
            $"wait --namespace {Quote(ns)} --for=condition=Available deployment/{deploymentName} --timeout={(int)timeout.TotalSeconds}s",
            _workingDirectory,
            ct);

    public Task<CommandResult> GetPodsAsync(string ns, CancellationToken ct)
        => CommandRunner.RunAsync("kubectl", $"get pods -n {Quote(ns)} -o wide", _workingDirectory, ct);

    public Task<CommandResult> GetPodsBySelectorAsync(string ns, string selector, CancellationToken ct)
        => CommandRunner.RunAsync(
            "kubectl",
            $"get pods -n {Quote(ns)} -l {Quote(selector)} -o name",
            _workingDirectory,
            ct);

    public Task<CommandResult> GetDeploymentsAsync(string ns, CancellationToken ct)
        => CommandRunner.RunAsync("kubectl", $"get deployments -n {Quote(ns)} -o wide", _workingDirectory, ct);

    public Task<CommandResult> RolloutStatusDeploymentAsync(string ns, string deploymentName, TimeSpan timeout, CancellationToken ct)
        => CommandRunner.RunAsync(
            "kubectl",
            $"rollout status deployment/{Quote(deploymentName)} -n {Quote(ns)} --timeout={(int)timeout.TotalSeconds}s",
            _workingDirectory,
            ct);

    public Task<CommandResult> DeletePodAsync(string ns, string podName, bool wait, CancellationToken ct)
        => CommandRunner.RunAsync(
            "kubectl",
            $"delete pod {Quote(podName)} -n {Quote(ns)} --wait={(wait ? "true" : "false")}",
            _workingDirectory,
            ct);

    private static string Quote(string value) => $"\"{value}\"";
}

public sealed class HelmClient
{
    private readonly string _workingDirectory;

    public HelmClient(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public Task<CommandResult> TemplateAsync(
        string releaseName,
        string chartPath,
        string namespaceName,
        string valuesFilePath,
        CancellationToken ct)
    {
        var args = new StringBuilder();
        args.Append("template ")
            .Append(Quote(releaseName)).Append(' ')
            .Append(Quote(chartPath)).Append(' ')
            .Append("--namespace ").Append(Quote(namespaceName)).Append(' ')
            .Append("-f ").Append(Quote(valuesFilePath));

        return CommandRunner.RunAsync("helm", args.ToString(), _workingDirectory, ct);
    }

    public Task<CommandResult> UpgradeInstallAsync(
        string releaseName,
        string chartPath,
        string namespaceName,
        string valuesFilePath,
        CancellationToken ct)
    {
        var args = new StringBuilder();
        args.Append("upgrade --install --atomic ")
            .Append(Quote(releaseName)).Append(' ')
            .Append(Quote(chartPath)).Append(' ')
            .Append("--create-namespace --namespace ").Append(Quote(namespaceName)).Append(' ')
            .Append("-f ").Append(Quote(valuesFilePath));

        return CommandRunner.RunAsync("helm", args.ToString(), _workingDirectory, ct);
    }

    public Task<CommandResult> UninstallAsync(string releaseName, string namespaceName, CancellationToken ct)
        => CommandRunner.RunAsync(
            "helm",
            $"uninstall {Quote(releaseName)} --namespace {Quote(namespaceName)}",
            _workingDirectory,
            ct);

    private static string Quote(string value) => $"\"{value}\"";
}
