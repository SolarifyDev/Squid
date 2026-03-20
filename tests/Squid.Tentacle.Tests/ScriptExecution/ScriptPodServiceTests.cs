using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class ScriptPodServiceTests : IDisposable
{
    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly string _tempWorkspace;

    public ScriptPodServiceTests()
    {
        DiskSpaceChecker.Enabled = false;

        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);

        _tentacleSettings = new TentacleSettings
        {
            WorkspacePath = _tempWorkspace
        };

        _kubernetesSettings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc"
        };

        _ops = new Mock<IKubernetesPodOperations>();

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
    }

    [Fact]
    public void StartScript_CreatesWorkspaceDirectory()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");

        var ticket = service.StartScript(command);

        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_WritesScriptFile()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello world");

        var ticket = service.StartScript(command);

        var scriptPath = Path.Combine(_tempWorkspace, ticket.TaskId, "script.sh");
        File.Exists(scriptPath).ShouldBeTrue();
        File.ReadAllText(scriptPath).ShouldContain("echo hello world");
    }

    [Fact]
    public void StartScript_CallsCreatePod()
    {
        var service = CreateService();
        var command = MakeCommand("echo test");

        service.StartScript(command);

        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void StartScript_ReturnsUniqueTicket()
    {
        var service = CreateService();

        var ticket1 = service.StartScript(MakeCommand("echo 1"));
        var ticket2 = service.StartScript(MakeCommand("echo 2"));

        ticket1.TaskId.ShouldNotBe(ticket2.TaskId);
    }

    [Theory]
    [InlineData("Succeeded", ProcessState.Complete)]
    [InlineData("Failed", ProcessState.Complete)]
    [InlineData("Running", ProcessState.Running)]
    [InlineData("Pending", ProcessState.Running)]
    [InlineData(null, ProcessState.Running)]
    public void GetStatus_MapsPodPhaseToProcessState(string phase, ProcessState expectedState)
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase(phase);
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(expectedState);
    }

    [Fact]
    public void GetStatus_PodNotFound_ReturnsComplete()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new k8s.Autorest.HttpOperationException
            {
                Response = new k8s.Autorest.HttpResponseMessageWrapper(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), string.Empty)
            });
        SetupPodLogs("");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
    }

    [Fact]
    public void GetStatus_UnknownTicket_ReturnsCompletedWithUnknownResultCode()
    {
        var service = CreateService();

        var status = service.GetStatus(new ScriptStatusRequest(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void CompleteScript_DeletesPod()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo done"));

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("done");

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void CompleteScript_CleansUpWorkspace()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo done"));

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeTrue();

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        Directory.Exists(workDir).ShouldBeFalse();
    }

    [Fact]
    public void CancelScript_DeletesPodAndReturnsCanceledCode()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("sleep 999"));

        var status = service.CancelScript(new CancelScriptCommand(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void CancelScript_UnknownTicket_ReturnsCanceledCode()
    {
        var service = CreateService();

        var status = service.CancelScript(new CancelScriptCommand(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    [Fact]
    public void GetStatus_EosMarkerInLogs_ReturnsCompleteEarly()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));
        var ctx = service.ActiveScripts[ticket.TaskId];

        SetupPodPhase("Running");

        var eosLine = $"EOS-{ctx.EosMarkerToken}<<>>42";
        SetupPodLogs($"output line 1\n{eosLine}\n");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(42);
        status.Logs.ShouldNotContain(l => l.Text.Contains("EOS-"));
    }

    [Fact]
    public void GetStatus_NoEosMarker_FallsBackToPodPhase()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");
        SetupPodLogs("just output\n");

        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Running);
    }

    [Fact]
    public void StartScript_WrapsScriptWithEosMarker()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo hello"));

        var scriptPath = Path.Combine(_tempWorkspace, ticket.TaskId, "script.sh");
        var content = File.ReadAllText(scriptPath);

        content.ShouldContain("echo hello");
        content.ShouldContain("__squid_exit_code__=$?");
        content.ShouldContain("EOS-");
        content.ShouldContain("exit $__squid_exit_code__");
    }

    // ========== Isolation Mutex ==========

    [Fact]
    public void StartScript_FullIsolation_AcquiresMutex()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo isolated", "test-mutex");

        var ticket = service.StartScript(command);

        ticket.ShouldNotBeNull();
        service.ActiveScripts.ContainsKey(ticket.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_FullIsolation_QueuesSecondScript()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo first", "blocking-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void GetStatus_PendingScript_ReturnsRunningWithWaitMessage()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo first", "status-mutex");

        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        var status = service.GetStatus(new ScriptStatusRequest(ticket2, 0));

        status.State.ShouldBe(ProcessState.Running);
        status.Logs.ShouldContain(l => l.Text.Contains("Waiting for isolation mutex..."));
        status.NextLogSequence.ShouldBe(1);

        var status2 = service.GetStatus(new ScriptStatusRequest(ticket2, 1));

        status2.State.ShouldBe(ProcessState.Running);
        status2.Logs.ShouldBeEmpty();
        status2.NextLogSequence.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReleasingActiveScript_StartsPendingScript(bool viaComplete)
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo test", "release-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        if (viaComplete)
            service.CompleteScript(new CompleteScriptCommand(ticket1, 0));
        else
            service.CancelScript(new CancelScriptCommand(ticket1, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Exactly(2));
    }

    [Fact]
    public void CancelScript_PendingScript_RemovesFromQueue()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo test", "cancel-pending-mutex");

        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        var status = service.CancelScript(new CancelScriptCommand(ticket2, 0));

        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        service.PendingScripts.ShouldBeEmpty();
        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void StartScript_FullIsolation_DifferentMutexNames_BothStartImmediately()
    {
        var service = CreateService();
        var commandA = MakeIsolatedCommand("echo a", "mutex-a");
        var commandB = MakeIsolatedCommand("echo b", "mutex-b");

        var ticket1 = service.StartScript(commandA);
        var ticket2 = service.StartScript(commandB);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        service.PendingScripts.ShouldBeEmpty();
    }

    [Fact]
    public void StartScript_NoIsolation_NeverQueues()
    {
        var service = CreateService();

        service.StartScript(MakeCommand("echo 1"));
        service.StartScript(MakeCommand("echo 2"));

        service.PendingScripts.Count.ShouldBe(0);
    }

    [Fact]
    public void CancelScript_ReleasesIsolationMutex()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo cancel", "cancel-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        service.CancelScript(new CancelScriptCommand(ticket1, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_NoIsolation_DoesNotBlock()
    {
        var service = CreateService();

        var ticket1 = service.StartScript(MakeCommand("echo 1"));
        var ticket2 = service.StartScript(MakeCommand("echo 2"));

        ticket1.TaskId.ShouldNotBe(ticket2.TaskId);
        service.ActiveScripts.Count.ShouldBe(2);
    }

    // ========== Helpers ==========

    private ScriptPodService CreateService()
    {
        var podManager = new KubernetesPodManager(_ops.Object, _kubernetesSettings);
        return new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null);
    }

    private static StartScriptCommand MakeIsolatedCommand(string scriptBody, string mutexName, TimeSpan? timeout = null)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.FullIsolation,
            timeout ?? TimeSpan.FromMinutes(5),
            mutexName,
            Array.Empty<string>(),
            null);
    }

    private void SetupPodPhase(string phase)
    {
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod
            {
                Status = new V1PodStatus { Phase = phase }
            });
    }

    private void SetupPodExitCode(int exitCode)
    {
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod
            {
                Status = new V1PodStatus
                {
                    Phase = "Succeeded",
                    ContainerStatuses = new List<V1ContainerStatus>
                    {
                        new()
                        {
                            Name = "script",
                            State = new V1ContainerState
                            {
                                Terminated = new V1ContainerStateTerminated { ExitCode = exitCode }
                            }
                        }
                    }
                }
            });
    }

    private void SetupPodLogs(string logs)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs));

        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stream);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
                Directory.Delete(_tempWorkspace, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
