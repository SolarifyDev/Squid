using Squid.Tentacle.Tests.Kubernetes.Integration.Support;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;
using Squid.Tentacle.Tests.Support.Fakes;
using Squid.Tentacle.Tests.Support.Paths;

namespace Squid.Tentacle.Tests.Kubernetes.Integration;

[Trait("Category", TentacleTestCategories.Kubernetes)]
[Trait("Category", TentacleTestCategories.Integration)]
public class KubernetesScriptPodExecutionE2ETests : KubernetesAgentIntegrationTestBase
{
    public KubernetesScriptPodExecutionE2ETests() : base(TimeSpan.FromMinutes(8))
    {
    }

    [Fact]
    public async Task UseScriptPodsTrue_RealScriptPodExecutionE2E_Succeeds()
    {
        await RunScriptPodExecutionE2EAsync(
            scriptLines: ["echo scriptpod-smoke-ok"],
            expectedPhase: "Succeeded",
            expectedLogText: "scriptpod-smoke-ok",
            expectedExitCodeFragment: "exitCode: 0");
    }

    [Fact]
    public async Task UseScriptPodsTrue_RealScriptPodExecutionE2E_PropagatesFailure()
    {
        await RunScriptPodExecutionE2EAsync(
            scriptLines: ["echo scriptpod-fail", "exit 7"],
            expectedPhase: "Failed",
            expectedLogText: "scriptpod-fail",
            expectedExitCodeFragment: "exitCode: 7");
    }

    [Fact]
    public async Task UseScriptPodsTrue_TentaclePodRestart_E2E_StillExecutesScriptPods()
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var settings = KubernetesE2EEnvironmentSettings.Load();

        if (!settings.Enabled || !prereqs.IsAvailable || !settings.HasRequiredInstallSettings)
            return;

        await using var fakeRegistrationServer = FakeMachineRegistrationServer.Start();
        var serverUrlForCluster = fakeRegistrationServer.ContainerReachableBaseAddress.ToString();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var releaseName = $"{settings.ReleaseName}-{unique}";
        var namespaceName = $"{settings.Namespace}-{unique}";

        var repoRoot = WorkspacePaths.RepositoryRoot;
        var chartPath = Path.Combine(repoRoot, "deploy", "helm", "kubernetes-agent");
        using var tempDir = new TemporaryDirectory();
        var valuesPath = Path.Combine(tempDir.Path, "values.override.yaml");
        var helperPodYamlPath = Path.Combine(tempDir.Path, "helper-pod.yaml");
        var scriptPodYamlPath = Path.Combine(tempDir.Path, "script-pod.yaml");

        var valuesYaml = SquidTentacleHelmValuesOverrideBuilder.BuildYaml(new SquidTentacleHelmValuesOverride
        {
            TentacleImageRepository = settings.TentacleImageRepository,
            TentacleImageTag = settings.TentacleImageTag,
            ScriptPodImage = settings.ScriptPodImage,
            ServerUrl = serverUrlForCluster,
            BearerToken = settings.BearerToken,
            KubernetesNamespace = settings.KubernetesTargetNamespace,
            WorkspaceStorageClassName = settings.WorkspaceStorageClassName,
            ForceReadWriteOnceForSmoke = true
        });
        await File.WriteAllTextAsync(valuesPath, valuesYaml, TestCancellationToken);

        var pvcName = $"{releaseName}-workspace";
        var scriptServiceAccount = $"{releaseName}-script";
        var workDir = $"/squid/work/e2e-scriptpod-{unique}";
        var helperPodName = $"squid-scriptpod-writer-{unique}";
        var scriptPodName = $"squid-scriptpod-runner-{unique}";

        await File.WriteAllTextAsync(helperPodYamlPath, BuildWorkspaceWriterPodYaml(
            namespaceName, helperPodName, pvcName, settings.ScriptPodImage, workDir, ["echo restarted-tentacle-ok"]), TestCancellationToken);
        await File.WriteAllTextAsync(scriptPodYamlPath, BuildScriptPodYaml(
            namespaceName, scriptPodName, scriptServiceAccount, pvcName, settings.ScriptPodImage, workDir), TestCancellationToken);

        var helm = new HelmClient(repoRoot);
        var kubectl = new KubectlClient(repoRoot);

        try
        {
            _ = await helm.UninstallAsync(releaseName, namespaceName, CancellationToken.None);
            (await helm.UpgradeInstallAsync(releaseName, chartPath, namespaceName, valuesPath, TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.RolloutStatusDeploymentAsync(namespaceName, releaseName, TimeSpan.FromMinutes(5), TestCancellationToken)).ExitCode.ShouldBe(0);

            var registrationBody = await fakeRegistrationServer.WaitForFirstRegistrationAsync(TestCancellationToken);
            registrationBody.ShouldContain("subscriptionId");

            var selector = $"app.kubernetes.io/name=kubernetes-agent,app.kubernetes.io/instance={releaseName}";
            var listed = await kubectl.GetPodsBySelectorAsync(namespaceName, selector, TestCancellationToken);
            listed.ExitCode.ShouldBe(0);
            var tentaclePodName = ParseFirstPodName(listed.StdOut);
            tentaclePodName.ShouldNotBeNullOrWhiteSpace();

            (await kubectl.DeletePodAsync(namespaceName, tentaclePodName, wait: true, TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.RolloutStatusDeploymentAsync(namespaceName, releaseName, TimeSpan.FromMinutes(5), TestCancellationToken)).ExitCode.ShouldBe(0);

            (await kubectl.ApplyAsync(helperPodYamlPath, TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.WaitPodPhaseAsync(namespaceName, helperPodName, "Succeeded", TimeSpan.FromMinutes(2), TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.ApplyAsync(scriptPodYamlPath, TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.WaitPodPhaseAsync(namespaceName, scriptPodName, "Succeeded", TimeSpan.FromMinutes(3), TestCancellationToken)).ExitCode.ShouldBe(0);

            var logs = await kubectl.GetPodLogsAsync(namespaceName, scriptPodName, TestCancellationToken);
            logs.ExitCode.ShouldBe(0);
            logs.StdOut.ShouldContain("restarted-tentacle-ok");
        }
        finally
        {
            _ = await helm.UninstallAsync(releaseName, namespaceName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task UseScriptPodsTrue_ScriptPodDelete_E2E_DeletionIsObserved()
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var settings = KubernetesE2EEnvironmentSettings.Load();

        if (!settings.Enabled || !prereqs.IsAvailable || !settings.HasRequiredInstallSettings)
            return;

        await using var fakeRegistrationServer = FakeMachineRegistrationServer.Start();
        var serverUrlForCluster = fakeRegistrationServer.ContainerReachableBaseAddress.ToString();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var releaseName = $"{settings.ReleaseName}-{unique}";
        var namespaceName = $"{settings.Namespace}-{unique}";

        var repoRoot = WorkspacePaths.RepositoryRoot;
        var chartPath = Path.Combine(repoRoot, "deploy", "helm", "kubernetes-agent");
        using var tempDir = new TemporaryDirectory();
        var valuesPath = Path.Combine(tempDir.Path, "values.override.yaml");
        var helperPodYamlPath = Path.Combine(tempDir.Path, "helper-pod.yaml");
        var scriptPodYamlPath = Path.Combine(tempDir.Path, "script-pod.yaml");

        var valuesYaml = SquidTentacleHelmValuesOverrideBuilder.BuildYaml(new SquidTentacleHelmValuesOverride
        {
            TentacleImageRepository = settings.TentacleImageRepository,
            TentacleImageTag = settings.TentacleImageTag,
            ScriptPodImage = settings.ScriptPodImage,
            ServerUrl = serverUrlForCluster,
            BearerToken = settings.BearerToken,
            KubernetesNamespace = settings.KubernetesTargetNamespace,
            WorkspaceStorageClassName = settings.WorkspaceStorageClassName,
            ForceReadWriteOnceForSmoke = true
        });
        await File.WriteAllTextAsync(valuesPath, valuesYaml, TestCancellationToken);

        var pvcName = $"{releaseName}-workspace";
        var scriptServiceAccount = $"{releaseName}-script";
        var workDir = $"/squid/work/e2e-scriptpod-{unique}";
        var helperPodName = $"squid-scriptpod-writer-{unique}";
        var scriptPodName = $"squid-scriptpod-runner-{unique}";

        await File.WriteAllTextAsync(helperPodYamlPath, BuildWorkspaceWriterPodYaml(
            namespaceName, helperPodName, pvcName, settings.ScriptPodImage, workDir, ["echo start-delete-test", "sleep 30", "echo should-not-reach"]), TestCancellationToken);
        await File.WriteAllTextAsync(scriptPodYamlPath, BuildScriptPodYaml(
            namespaceName, scriptPodName, scriptServiceAccount, pvcName, settings.ScriptPodImage, workDir), TestCancellationToken);

        var helm = new HelmClient(repoRoot);
        var kubectl = new KubectlClient(repoRoot);

        try
        {
            _ = await helm.UninstallAsync(releaseName, namespaceName, CancellationToken.None);
            (await helm.UpgradeInstallAsync(releaseName, chartPath, namespaceName, valuesPath, TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.RolloutStatusDeploymentAsync(namespaceName, releaseName, TimeSpan.FromMinutes(5), TestCancellationToken)).ExitCode.ShouldBe(0);

            _ = await fakeRegistrationServer.WaitForFirstRegistrationAsync(TestCancellationToken);

            (await kubectl.ApplyAsync(helperPodYamlPath, TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.WaitPodPhaseAsync(namespaceName, helperPodName, "Succeeded", TimeSpan.FromMinutes(2), TestCancellationToken)).ExitCode.ShouldBe(0);
            (await kubectl.ApplyAsync(scriptPodYamlPath, TestCancellationToken)).ExitCode.ShouldBe(0);

            (await kubectl.WaitPodPhaseAsync(namespaceName, scriptPodName, "Running", TimeSpan.FromMinutes(2), TestCancellationToken)).ExitCode.ShouldBe(0);
            var delete = await kubectl.DeletePodAsync(namespaceName, scriptPodName, wait: true, TestCancellationToken);
            delete.ExitCode.ShouldBe(0, $"kubectl delete script pod failed:{Environment.NewLine}{delete.StdOut}{Environment.NewLine}{delete.StdErr}");

            var afterDelete = await kubectl.GetPodAsync(namespaceName, scriptPodName, TestCancellationToken);
            afterDelete.ExitCode.ShouldNotBe(0);
            (afterDelete.StdErr + afterDelete.StdOut).ShouldContain("NotFound");
        }
        finally
        {
            _ = await helm.UninstallAsync(releaseName, namespaceName, CancellationToken.None);
        }
    }

    private async Task RunScriptPodExecutionE2EAsync(
        IReadOnlyList<string> scriptLines,
        string expectedPhase,
        string expectedLogText,
        string expectedExitCodeFragment)
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var settings = KubernetesE2EEnvironmentSettings.Load();

        settings.ReleaseName.ShouldNotBeNullOrWhiteSpace();
        settings.Namespace.ShouldNotBeNullOrWhiteSpace();

        if (!settings.Enabled || !prereqs.IsAvailable || !settings.HasRequiredInstallSettings)
            return;

        await using var fakeRegistrationServer = FakeMachineRegistrationServer.Start();
        var serverUrlForCluster = fakeRegistrationServer.ContainerReachableBaseAddress.ToString();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var releaseName = $"{settings.ReleaseName}-{unique}";
        var namespaceName = $"{settings.Namespace}-{unique}";

        var repoRoot = WorkspacePaths.RepositoryRoot;
        var chartPath = Path.Combine(repoRoot, "deploy", "helm", "kubernetes-agent");
        Directory.Exists(chartPath).ShouldBeTrue();

        using var tempDir = new TemporaryDirectory();
        var valuesPath = Path.Combine(tempDir.Path, "values.override.yaml");
        var helperPodYamlPath = Path.Combine(tempDir.Path, "helper-pod.yaml");
        var scriptPodYamlPath = Path.Combine(tempDir.Path, "script-pod.yaml");

        var valuesYaml = SquidTentacleHelmValuesOverrideBuilder.BuildYaml(new SquidTentacleHelmValuesOverride
        {
            TentacleImageRepository = settings.TentacleImageRepository,
            TentacleImageTag = settings.TentacleImageTag,
            ScriptPodImage = settings.ScriptPodImage,
            ServerUrl = serverUrlForCluster,
            BearerToken = settings.BearerToken,
            KubernetesNamespace = settings.KubernetesTargetNamespace,
            WorkspaceStorageClassName = settings.WorkspaceStorageClassName,
            ForceReadWriteOnceForSmoke = true
        });
        await File.WriteAllTextAsync(valuesPath, valuesYaml, TestCancellationToken);

        var pvcName = $"{releaseName}-workspace";
        var scriptServiceAccount = $"{releaseName}-script";
        var workTicket = $"e2e-scriptpod-{unique}";
        var workDir = $"/squid/work/{workTicket}";
        var helperPodName = $"squid-scriptpod-writer-{unique}";
        var scriptPodName = $"squid-scriptpod-runner-{unique}";

        await File.WriteAllTextAsync(helperPodYamlPath, BuildWorkspaceWriterPodYaml(
            namespaceName,
            helperPodName,
            pvcName,
            settings.ScriptPodImage,
            workDir,
            scriptLines), TestCancellationToken);

        await File.WriteAllTextAsync(scriptPodYamlPath, BuildScriptPodYaml(
            namespaceName,
            scriptPodName,
            scriptServiceAccount,
            pvcName,
            settings.ScriptPodImage,
            workDir), TestCancellationToken);

        var helm = new HelmClient(repoRoot);
        var kubectl = new KubectlClient(repoRoot);

        try
        {
            _ = await helm.UninstallAsync(releaseName, namespaceName, CancellationToken.None);

            var install = await helm.UpgradeInstallAsync(releaseName, chartPath, namespaceName, valuesPath, TestCancellationToken);
            install.ExitCode.ShouldBe(0, $"helm upgrade --install failed:{Environment.NewLine}{install.StdOut}{Environment.NewLine}{install.StdErr}");

            var rollout = await kubectl.RolloutStatusDeploymentAsync(namespaceName, releaseName, TimeSpan.FromMinutes(5), TestCancellationToken);
            rollout.ExitCode.ShouldBe(0, $"kubectl rollout status failed:{Environment.NewLine}{rollout.StdOut}{Environment.NewLine}{rollout.StdErr}");

            var registrationBody = await fakeRegistrationServer.WaitForFirstRegistrationAsync(TestCancellationToken);
            registrationBody.ShouldContain("subscriptionId");

            var applyHelper = await kubectl.ApplyAsync(helperPodYamlPath, TestCancellationToken);
            applyHelper.ExitCode.ShouldBe(0, $"kubectl apply helper pod failed:{Environment.NewLine}{applyHelper.StdOut}{Environment.NewLine}{applyHelper.StdErr}");

            var helperDone = await kubectl.WaitPodPhaseAsync(namespaceName, helperPodName, "Succeeded", TimeSpan.FromMinutes(2), TestCancellationToken);
            helperDone.ExitCode.ShouldBe(0, $"helper pod did not complete:{Environment.NewLine}{helperDone.StdOut}{Environment.NewLine}{helperDone.StdErr}");

            var applyScript = await kubectl.ApplyAsync(scriptPodYamlPath, TestCancellationToken);
            applyScript.ExitCode.ShouldBe(0, $"kubectl apply script pod failed:{Environment.NewLine}{applyScript.StdOut}{Environment.NewLine}{applyScript.StdErr}");

            var scriptDone = await kubectl.WaitPodPhaseAsync(namespaceName, scriptPodName, expectedPhase, TimeSpan.FromMinutes(3), TestCancellationToken);
            scriptDone.ExitCode.ShouldBe(0, $"script pod did not complete:{Environment.NewLine}{scriptDone.StdOut}{Environment.NewLine}{scriptDone.StdErr}");

            var logs = await kubectl.GetPodLogsAsync(namespaceName, scriptPodName, TestCancellationToken);
            logs.ExitCode.ShouldBe(0, $"kubectl logs failed:{Environment.NewLine}{logs.StdOut}{Environment.NewLine}{logs.StdErr}");
            logs.StdOut.ShouldContain(expectedLogText);

            var podYaml = await kubectl.GetPodAsync(namespaceName, scriptPodName, TestCancellationToken);
            podYaml.ExitCode.ShouldBe(0, $"kubectl get pod yaml failed:{Environment.NewLine}{podYaml.StdOut}{Environment.NewLine}{podYaml.StdErr}");
            podYaml.StdOut.ShouldContain(expectedExitCodeFragment);
        }
        finally
        {
            _ = await helm.UninstallAsync(releaseName, namespaceName, CancellationToken.None);
        }
    }

    private static string BuildWorkspaceWriterPodYaml(
        string ns,
        string podName,
        string pvcName,
        string image,
        string workDir,
        IReadOnlyList<string> scriptLines)
    {
        var scriptBody = string.Join("\\n", scriptLines) + "\\n";
        var shell = string.Join(" && ", new[]
        {
            "set -eu",
            $"mkdir -p {workDir}",
            $"printf {SingleQuoted(shellEscaped: scriptBody)} > {workDir}/script.sh",
            $"printf '{{}}' > {workDir}/variables.json"
        });

        return string.Join('\n', new[]
        {
            "apiVersion: v1",
            "kind: Pod",
            "metadata:",
            $"  name: {podName}",
            $"  namespace: {ns}",
            "spec:",
            "  restartPolicy: Never",
            "  containers:",
            "  - name: writer",
            $"    image: {image}",
            "    imagePullPolicy: IfNotPresent",
            "    command: [\"bash\", \"-lc\"]",
            $"    args: [{YamlDoubleQuoted(shell)}]",
            "    volumeMounts:",
            "    - name: workspace",
            "      mountPath: /squid/work",
            "  volumes:",
            "  - name: workspace",
            "    persistentVolumeClaim:",
            $"      claimName: {pvcName}",
            string.Empty
        });
    }

    private static string BuildScriptPodYaml(string ns, string podName, string serviceAccountName, string pvcName, string image, string workDir)
    {
        return $$"""
apiVersion: v1
kind: Pod
metadata:
  name: {{podName}}
  namespace: {{ns}}
  labels:
    app.kubernetes.io/managed-by: kubernetes-agent
spec:
  serviceAccountName: {{serviceAccountName}}
  restartPolicy: Never
  containers:
  - name: script
    image: {{image}}
    imagePullPolicy: IfNotPresent
    command: ["squid-calamari"]
    args:
    - run-script
    - --script={{workDir}}/script.sh
    - --variables={{workDir}}/variables.json
    workingDir: {{workDir}}
    volumeMounts:
    - name: workspace
      mountPath: /squid/work
  volumes:
  - name: workspace
    persistentVolumeClaim:
      claimName: {{pvcName}}
""";
    }

    private static string YamlDoubleQuoted(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string SingleQuoted(string shellEscaped)
        => $"'{shellEscaped.Replace("'", "'\\''")}'";

    private static string ParseFirstPodName(string stdout)
    {
        return stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("pod/", StringComparison.OrdinalIgnoreCase) ? line["pod/".Length..] : line)
            .FirstOrDefault() ?? string.Empty;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "squid-tentacle-k8s-scriptpod-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
