using Squid.Tentacle.Tests.Kubernetes.Integration.Support;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;
using Squid.Tentacle.Tests.Support.Fakes;
using Squid.Tentacle.Tests.Support.Paths;

namespace Squid.Tentacle.Tests.Kubernetes.Integration;

[Trait("Category", TentacleTestCategories.Kubernetes)]
[Trait("Category", TentacleTestCategories.Integration)]
public class KubernetesScriptPodExecutionSubstrateSmokeTests : KubernetesAgentIntegrationTestBase
{
    public KubernetesScriptPodExecutionSubstrateSmokeTests() : base(TimeSpan.FromMinutes(8))
    {
    }

    [Fact]
    public async Task UseScriptPodsTrue_Can_Run_A_Real_ScriptPod_Using_Chart_Pvc_And_ServiceAccount()
    {
        var prereqs = KubernetesIntegrationPrerequisites.Detect();
        var settings = KubernetesE2EEnvironmentSettings.Load();

        settings.ReleaseName.ShouldNotBeNullOrWhiteSpace();
        settings.Namespace.ShouldNotBeNullOrWhiteSpace();

        if (!settings.Enabled || !prereqs.IsAvailable || !settings.HasRequiredInstallSettings)
            return;

        await using var fakeRegistrationServer = FakeAgentRegistrationServer.Start();
        var serverUrlForCluster = fakeRegistrationServer.ContainerReachableBaseAddress.ToString();

        var repoRoot = WorkspacePaths.RepositoryRoot;
        var chartPath = Path.Combine(repoRoot, "deploy", "helm", "squid-tentacle");
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

        var pvcName = $"{settings.ReleaseName}-workspace";
        var scriptServiceAccount = $"{settings.ReleaseName}-script";
        const string workTicket = "e2e-scriptpod-smoke";
        var workDir = $"/squid/work/{workTicket}";
        var helperPodName = "squid-scriptpod-writer";
        var scriptPodName = "squid-scriptpod-runner";

        await File.WriteAllTextAsync(helperPodYamlPath, BuildWorkspaceWriterPodYaml(
            settings.Namespace,
            helperPodName,
            pvcName,
            settings.ScriptPodImage,
            workDir), TestCancellationToken);

        await File.WriteAllTextAsync(scriptPodYamlPath, BuildScriptPodYaml(
            settings.Namespace,
            scriptPodName,
            scriptServiceAccount,
            pvcName,
            settings.ScriptPodImage,
            workDir), TestCancellationToken);

        var helm = new HelmClient(repoRoot);
        var kubectl = new KubectlClient(repoRoot);

        try
        {
            _ = await helm.UninstallAsync(settings.ReleaseName, settings.Namespace, CancellationToken.None);

            var install = await helm.UpgradeInstallAsync(settings.ReleaseName, chartPath, settings.Namespace, valuesPath, TestCancellationToken);
            install.ExitCode.ShouldBe(0, $"helm upgrade --install failed:{Environment.NewLine}{install.StdOut}{Environment.NewLine}{install.StdErr}");

            var rollout = await kubectl.RolloutStatusDeploymentAsync(settings.Namespace, settings.ReleaseName, TimeSpan.FromMinutes(5), TestCancellationToken);
            rollout.ExitCode.ShouldBe(0, $"kubectl rollout status failed:{Environment.NewLine}{rollout.StdOut}{Environment.NewLine}{rollout.StdErr}");

            var registrationBody = await fakeRegistrationServer.WaitForFirstRegistrationAsync(TestCancellationToken);
            registrationBody.ShouldContain("subscriptionId");

            var applyHelper = await kubectl.ApplyAsync(helperPodYamlPath, TestCancellationToken);
            applyHelper.ExitCode.ShouldBe(0, $"kubectl apply helper pod failed:{Environment.NewLine}{applyHelper.StdOut}{Environment.NewLine}{applyHelper.StdErr}");

            var helperDone = await kubectl.WaitPodPhaseAsync(settings.Namespace, helperPodName, "Succeeded", TimeSpan.FromMinutes(2), TestCancellationToken);
            helperDone.ExitCode.ShouldBe(0, $"helper pod did not complete:{Environment.NewLine}{helperDone.StdOut}{Environment.NewLine}{helperDone.StdErr}");

            var applyScript = await kubectl.ApplyAsync(scriptPodYamlPath, TestCancellationToken);
            applyScript.ExitCode.ShouldBe(0, $"kubectl apply script pod failed:{Environment.NewLine}{applyScript.StdOut}{Environment.NewLine}{applyScript.StdErr}");

            var scriptDone = await kubectl.WaitPodPhaseAsync(settings.Namespace, scriptPodName, "Succeeded", TimeSpan.FromMinutes(3), TestCancellationToken);
            scriptDone.ExitCode.ShouldBe(0, $"script pod did not complete:{Environment.NewLine}{scriptDone.StdOut}{Environment.NewLine}{scriptDone.StdErr}");

            var logs = await kubectl.GetPodLogsAsync(settings.Namespace, scriptPodName, TestCancellationToken);
            logs.ExitCode.ShouldBe(0, $"kubectl logs failed:{Environment.NewLine}{logs.StdOut}{Environment.NewLine}{logs.StdErr}");
            logs.StdOut.ShouldContain("scriptpod-smoke-ok");
        }
        finally
        {
            _ = await helm.UninstallAsync(settings.ReleaseName, settings.Namespace, CancellationToken.None);
        }
    }

    private static string BuildWorkspaceWriterPodYaml(string ns, string podName, string pvcName, string image, string workDir)
    {
        var shell = string.Join(" && ", new[]
        {
            "set -eu",
            $"mkdir -p {workDir}",
            $"printf 'echo scriptpod-smoke-ok\\n' > {workDir}/script.sh",
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
    app.kubernetes.io/managed-by: squid-tentacle
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
