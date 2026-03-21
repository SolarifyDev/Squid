using System;
using System.Collections.Generic;
using System.IO;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Tests.ScriptExecution;

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
