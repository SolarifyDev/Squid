using System;
using System.Collections.Generic;
using System.IO;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

[Collection(TentacleMetricsCollection.Name)]
public class ScriptRecoveryServiceTests : IDisposable
{
    private readonly string _tempWorkspace;
    private readonly ScriptRecoveryService _recovery = new();
    private readonly ScriptIsolationMutex _mutex = new();
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly KubernetesPodManager _podManager;
    private readonly ScriptPodService _service;

    public ScriptRecoveryServiceTests()
    {
        DiskSpaceChecker.Enabled = false;

        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-recovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);

        _ops = new Mock<IKubernetesPodOperations>();
        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        var k8sSettings = new KubernetesSettings { TentacleNamespace = "test-ns" };
        _podManager = new KubernetesPodManager(_ops.Object, k8sSettings);

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        _service = new ScriptPodService(tentacleSettings, k8sSettings, _podManager);
    }

    [Fact]
    public void Recovery_NoStateFiles_NoAction()
    {
        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(0);
        _service.ActiveScripts.ShouldBeEmpty();
    }

    [Fact]
    public void Recovery_EmptyWorkspace_NoError()
    {
        var nonexistent = Path.Combine(_tempWorkspace, "does-not-exist");

        var count = _recovery.RecoverScripts(nonexistent, _service, _podManager, _mutex);

        count.ShouldBe(0);
    }

    [Fact]
    public void Recovery_PodStillRunning_RebuildsContext()
    {
        var ticketId = "running-ticket";
        SeedStateFile(ticketId, "squid-script-running", "NoIsolation");
        SetupPodPhase("squid-script-running", "Running");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(1);
        _service.ActiveScripts.ContainsKey(ticketId).ShouldBeTrue();
        _service.ActiveScripts[ticketId].PodName.ShouldBe("squid-script-running");
    }

    [Fact]
    public void Recovery_PodCompleted_InjectsTerminalResult()
    {
        var ticketId = "completed-ticket";
        SeedStateFile(ticketId, "squid-script-completed", "NoIsolation");
        SetupPodPhase("squid-script-completed", "Succeeded");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(1);
        _service.ActiveScripts.ContainsKey(ticketId).ShouldBeFalse();

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void Recovery_PodNotFound_InjectsUnknownResult()
    {
        var ticketId = "missing-ticket";
        SeedStateFile(ticketId, "squid-script-missing", "NoIsolation");
        SetupPodPhase("squid-script-missing", KubernetesPodManager.PhaseNotFound);

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(1);
        _service.ActiveScripts.ContainsKey(ticketId).ShouldBeFalse();

        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket(ticketId), 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void Recovery_FullIsolation_ReacquiresMutex()
    {
        var ticketId = "isolated-ticket";
        SeedStateFile(ticketId, "squid-script-isolated", "FullIsolation", "deploy-mutex");
        SetupPodPhase("squid-script-isolated", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.MutexLocks.ContainsKey(ticketId).ShouldBeTrue();
    }

    [Fact]
    public void Recovery_NoIsolation_ReacquiresReaderLock()
    {
        var ticketId = "reader-ticket";
        SeedStateFile(ticketId, "squid-script-reader", "NoIsolation");
        SetupPodPhase("squid-script-reader", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.MutexLocks.ContainsKey(ticketId).ShouldBeTrue();
    }

    [Fact]
    public void Recovery_CorruptStateFile_SkipsGracefully()
    {
        var ticketDir = Path.Combine(_tempWorkspace, "corrupt-ticket");
        Directory.CreateDirectory(ticketDir);
        File.WriteAllText(Path.Combine(ticketDir, ".squid-state.json"), "not json");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(0);
        _service.ActiveScripts.ShouldBeEmpty();
    }

    [Fact]
    public void Recovery_MultipleScripts_RecoversAll()
    {
        SeedStateFile("ticket-a", "squid-script-a", "NoIsolation");
        SeedStateFile("ticket-b", "squid-script-b", "NoIsolation");
        SetupPodPhase("squid-script-a", "Running");
        SetupPodPhase("squid-script-b", "Running");

        var count = _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        count.ShouldBe(2);
        _service.ActiveScripts.Count.ShouldBe(2);
    }

    [Fact]
    public void Recovery_PodRunning_RestoresLastLogTimestamp()
    {
        var ticketId = "timestamp-ticket";
        var timestamp = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        SeedStateFileWithTimestamp(ticketId, "squid-script-ts", "NoIsolation", timestamp);
        SetupPodPhase("squid-script-ts", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.ActiveScripts.TryGetValue(ticketId, out var ctx).ShouldBeTrue();
        ctx.LastLogTimestamp.ShouldBe(timestamp);
    }

    [Fact]
    public void Recovery_NullTimestamp_DefaultsToNull()
    {
        var ticketId = "null-ts-ticket";
        SeedStateFile(ticketId, "squid-script-nullts", "NoIsolation");
        SetupPodPhase("squid-script-nullts", "Running");

        _recovery.RecoverScripts(_tempWorkspace, _service, _podManager, _mutex);

        _service.ActiveScripts.TryGetValue(ticketId, out var ctx).ShouldBeTrue();
        ctx.LastLogTimestamp.ShouldBeNull();
    }

    private void SeedStateFileWithTimestamp(string ticketId, string podName, string isolation, DateTime? lastLogTimestamp, string? mutexName = null)
    {
        var workDir = Path.Combine(_tempWorkspace, ticketId);
        Directory.CreateDirectory(workDir);

        ScriptStateFile.Write(workDir, new ScriptStateFile
        {
            TicketId = ticketId,
            PodName = podName,
            EosMarkerToken = "test-eos-token",
            Isolation = isolation,
            IsolationMutexName = mutexName,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLogTimestamp = lastLogTimestamp
        });
    }

    private void SeedStateFile(string ticketId, string podName, string isolation, string? mutexName = null)
    {
        var workDir = Path.Combine(_tempWorkspace, ticketId);
        Directory.CreateDirectory(workDir);

        ScriptStateFile.Write(workDir, new ScriptStateFile
        {
            TicketId = ticketId,
            PodName = podName,
            EosMarkerToken = "test-eos-token",
            Isolation = isolation,
            IsolationMutexName = mutexName,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    [Fact]
    public void RecoverPending_InjectsTerminalFailure_InsteadOfResubmit()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };

        var secret = new k8s.Models.V1Secret
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = "squid-pending-abc123" },
            StringData = new Dictionary<string, string>
            {
                ["ticketId"] = "abc123456789extra",
                ["scriptBody"] = "echo recovered",
                ["isolation"] = "NoIsolation",
                ["isolationMutexName"] = "",
                ["targetNamespace"] = "",
                ["enqueuedAt"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        ops.Setup(o => o.ListSecrets("test-ns", "squid.io/context-type=pending-script"))
            .Returns(new k8s.Models.V1SecretList { Items = new List<k8s.Models.V1Secret> { secret } });

        var podManager = new KubernetesPodManager(ops.Object, settings);
        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var service = new ScriptPodService(tentacleSettings, settings, podManager);

        _recovery.RecoverPendingScripts(ops.Object, settings, service);

        // Should NOT be re-launched as active script
        service.ActiveScripts.Count.ShouldBe(0);

        // Should have terminal result injected
        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("abc123456789extra"), 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void RecoverPending_DeletesSecretAfterInjection()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };

        var secret = new k8s.Models.V1Secret
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = "squid-pending-xyz789" },
            StringData = new Dictionary<string, string>
            {
                ["ticketId"] = "xyz789012345extra",
                ["scriptBody"] = "echo test",
                ["isolation"] = "NoIsolation",
                ["isolationMutexName"] = "",
                ["targetNamespace"] = ""
            }
        };

        ops.Setup(o => o.ListSecrets("test-ns", "squid.io/context-type=pending-script"))
            .Returns(new k8s.Models.V1SecretList { Items = new List<k8s.Models.V1Secret> { secret } });

        var podManager = new KubernetesPodManager(ops.Object, settings);
        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var service = new ScriptPodService(tentacleSettings, settings, podManager);

        _recovery.RecoverPendingScripts(ops.Object, settings, service);

        ops.Verify(o => o.DeleteSecret("squid-pending-xyz789", "test-ns"), Times.Once);
    }

    [Fact]
    public void RecoverPending_NoSecrets_NoAction()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };

        ops.Setup(o => o.ListSecrets("test-ns", "squid.io/context-type=pending-script"))
            .Returns(new k8s.Models.V1SecretList { Items = new List<k8s.Models.V1Secret>() });

        var podManager = new KubernetesPodManager(ops.Object, settings);
        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var service = new ScriptPodService(tentacleSettings, settings, podManager);

        _recovery.RecoverPendingScripts(ops.Object, settings, service);

        service.ActiveScripts.Count.ShouldBe(0);
    }

    private void SetupPodPhase(string podName, string phase)
    {
        if (phase == KubernetesPodManager.PhaseNotFound)
        {
            _ops.Setup(o => o.ReadPodStatus(podName, It.IsAny<string>()))
                .Throws(new k8s.Autorest.HttpOperationException
                {
                    Response = new k8s.Autorest.HttpResponseMessageWrapper(
                        new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), string.Empty)
                });
        }
        else
        {
            _ops.Setup(o => o.ReadPodStatus(podName, It.IsAny<string>()))
                .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase } });
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
                Directory.Delete(_tempWorkspace, recursive: true);
        }
        catch { }
    }
}
