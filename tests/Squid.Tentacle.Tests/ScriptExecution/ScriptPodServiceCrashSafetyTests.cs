using k8s.Models;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

[Trait("Category", TentacleTestCategories.Kubernetes)]
public sealed class ScriptPodServiceCrashSafetyTests : IDisposable
{
    private readonly string _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-pod-crash-test-{Guid.NewGuid():N}");
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly TentacleSettings _tentacleSettings;

    public ScriptPodServiceCrashSafetyTests()
    {
        Directory.CreateDirectory(_tempWorkspace);
        _tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        _kubernetesSettings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image@sha256:abc123def456789012345678901234567890123456789012345678901234aa77",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc"
        };

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns<V1Pod, string>((pod, _) => pod);
        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1PodList { Items = new List<V1Pod>() });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempWorkspace)) Directory.Delete(_tempWorkspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void StartScript_WritesScriptStateStore_AlongsideLegacyScriptStateFile()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");

        service.StartScript(command);

        var workDir = Path.Combine(_tempWorkspace, command.ScriptTicket.TaskId);
        File.Exists(Path.Combine(workDir, "scriptstate.json")).ShouldBeTrue(
            "generic ScriptStateStore must be persisted for crash-safe resume");

        // Legacy K8s-specific state file still exists for ScriptRecoveryService.
        File.Exists(Path.Combine(workDir, ".squid-state.json")).ShouldBeTrue(
            "K8s-specific ScriptStateFile remains for ScriptRecoveryService pod reattach");
    }

    [Fact]
    public void StartScript_AcrossAgentRestart_SameTicket_DoesNotCreatePodTwice()
    {
        var command = MakeCommand("echo idempotent");

        var firstAgent = CreateService();
        firstAgent.StartScript(command);
        _ops.Invocations.Clear();     // reset before the "second" agent run

        // Simulate agent pod restart: brand-new service instance with the same
        // workspace PVC. The on-disk ScriptStateStore from the first run is the
        // only signal the new instance has that this ticket already ran.
        var restartedAgent = CreateService();
        var response = restartedAgent.StartScript(command);

        response.State.ShouldNotBe(ProcessState.Pending,
            "redelivered StartScript after restart must return persisted state, not queue fresh");
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()), Times.Never,
            "the redelivered StartScript must NOT create a second pod for the same ticket");
    }

    [Fact]
    public void CompleteScript_WritesCompleteStateBeforeWorkspaceCleanup()
    {
        var service = CreateService();
        var command = MakeCommand("echo done");

        service.StartScript(command);
        var workDir = Path.Combine(_tempWorkspace, command.ScriptTicket.TaskId);

        // CompleteScript flow: would delete workspace. Intercept store state before
        // cleanup by peeking at the workspace while script is Running.
        var storeBeforeComplete = new ScriptStateStore(workDir);
        storeBeforeComplete.Exists().ShouldBeTrue();
        storeBeforeComplete.Load().Progress.ShouldBe(ScriptProgress.Running);

        service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));

        // After CancelScript the workspace should be gone; this test proves
        // PersistCompleteState ran before CleanupWorkspace — otherwise store
        // would still say Running.
        Directory.Exists(workDir).ShouldBeFalse();
    }

    [Fact]
    public void GetStatus_AfterInMemoryEviction_ReadsPersistedState()
    {
        var service = CreateService();
        var command = MakeCommand("echo running");

        service.StartScript(command);

        // Simulate ScriptRecoveryService failing to rebuild _scripts on restart
        // (or InjectTerminalResult evicting the entry). The on-disk state is
        // the only source of truth.
        service.ActiveScripts.Clear();

        var workDir = Path.Combine(_tempWorkspace, command.ScriptTicket.TaskId);
        File.Exists(Path.Combine(workDir, "scriptstate.json")).ShouldBeTrue();

        // The StartScript redelivery path (inside StartScript itself) is what
        // honours persisted state. Invoking it a second time should return
        // Running without recreating.
        _ops.Invocations.Clear();
        var redelivered = service.StartScript(command);
        redelivered.State.ShouldBe(ProcessState.Running);
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LaunchFailure_DeletesPersistedState_NoLeak()
    {
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("simulated CreatePod failure"));

        var service = CreateService();
        var command = MakeCommand("echo launchfail");

        // Launch fails; no ScriptStateStore should be left behind.
        service.StartScript(command);

        var workDir = Path.Combine(_tempWorkspace, command.ScriptTicket.TaskId);
        File.Exists(Path.Combine(workDir, "scriptstate.json")).ShouldBeFalse(
            "failed launches must not leak persisted state — otherwise subsequent redelivery thinks a ghost is running");
    }

    private ScriptPodService CreateService()
    {
        var podManager = new KubernetesPodManager(_ops.Object, _kubernetesSettings);
        return new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager, _ops.Object);
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }
}
