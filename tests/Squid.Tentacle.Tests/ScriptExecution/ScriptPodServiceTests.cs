using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.Health;
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

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", It.IsAny<int?>()), Times.Once);
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

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", It.IsAny<int?>()), Times.Once);
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

    // ========== Log Rotation Detection ==========

    [Fact]
    public void GetStatus_LogTruncation_ResetsReadPosition()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");

        // First read: 100 chars
        SetupPodLogs(new string('a', 50) + "\nline1\nline2\n");
        service.GetStatus(new ScriptStatusRequest(ticket, 0));

        // Simulate truncation: new log shorter than last read
        SetupPodLogs("new-line-after-rotation\n");
        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Running);
        status.Logs.ShouldContain(l => l.Text.Contains("new-line-after-rotation"));
    }

    [Fact]
    public void GetStatus_LogTruncation_TerminalPod_InjectsWarning()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");

        // First read: establish a log position
        SetupPodLogs("initial output that is fairly long\n");
        service.GetStatus(new ScriptStatusRequest(ticket, 0));

        // Simulate truncation + terminal pod
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("short\n");
        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.Logs.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("Log rotation detected"));
    }

    [Fact]
    public void GetStatus_NoTruncation_NormalBehavior()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo test"));

        SetupPodPhase("Running");

        SetupPodLogs("line1\n");
        var status1 = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        // Second read: longer log (no truncation)
        SetupPodLogs("line1\nline2\n");
        var status2 = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status2.State.ShouldBe(ProcessState.Running);
        status2.Logs.ShouldContain(l => l.Text.Contains("line2"));
        status2.Logs.ShouldNotContain(l => l.Text.Contains("Log rotation detected"));
    }

    // ========== Log Output Size Limits ==========

    [Fact]
    public void ExtractNewLogLines_ExceedsMaxBuffer_InjectsTruncationWarning()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        var logs = new string('x', 100) + "\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 50);

        result.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("Log output truncated"));
        ctx.LogOutputTruncated.ShouldBeTrue();
    }

    [Fact]
    public void ExtractNewLogLines_ExceedsMaxBuffer_StopsEmittingLines()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        var logs = new string('x', 100) + "\nline2\nline3\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 50);

        result.ShouldNotContain(l => l.Text == "line2");
        result.ShouldNotContain(l => l.Text == "line3");
    }

    [Fact]
    public void ExtractNewLogLines_ExceedsMaxBuffer_StillDetectsEos()
    {
        var eosToken = "abcdef1234567890abcdef1234567890";
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", eosToken);
        var eosLine = $"EOS-{eosToken}<<>>42";
        var logs = new string('x', 100) + $"\n{eosLine}\n";

        ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 50);

        ctx.EosDetected.ShouldBeTrue();
        ctx.EosExitCode.ShouldBe(42);
    }

    [Fact]
    public void ExtractNewLogLines_WithinLimit_NoTruncation()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        var logs = "line1\nline2\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);

        result.Count.ShouldBe(2);
        ctx.LogOutputTruncated.ShouldBeFalse();
    }

    // ========== Sensitive Output Masking ==========

    [Fact]
    public void ExtractNewLogLines_SensitiveValueInOutput_Masked()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        ctx.SensitiveValues = new HashSet<string>(StringComparer.Ordinal) { "my-secret-token" };

        var logs = "deploying with token: my-secret-token\n";

        var result = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);

        result.Count.ShouldBe(1);
        result[0].Text.ShouldBe("deploying with token: ***");
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
        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
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

    // ========== Mixed Isolation ==========

    [Fact]
    public void StartScript_FullIsolation_BlocksNoIsolation()
    {
        var service = CreateService();
        var writer = MakeIsolatedCommand("echo writer", "mixed-mutex");
        var reader = MakeCommand("echo reader", "mixed-mutex");

        var ticket1 = service.StartScript(writer);
        var ticket2 = service.StartScript(reader);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_NoIsolation_BlocksFullIsolation()
    {
        var service = CreateService();
        var reader = MakeCommand("echo reader", "mixed-mutex-2");
        var writer = MakeIsolatedCommand("echo writer", "mixed-mutex-2");

        var ticket1 = service.StartScript(reader);
        var ticket2 = service.StartScript(writer);

        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_NoIsolation_MultipleParallel_SameMutex()
    {
        var service = CreateService();

        var ticket1 = service.StartScript(MakeCommand("echo 1", "parallel-mutex"));
        var ticket2 = service.StartScript(MakeCommand("echo 2", "parallel-mutex"));
        var ticket3 = service.StartScript(MakeCommand("echo 3", "parallel-mutex"));

        service.ActiveScripts.Count.ShouldBe(3);
        service.PendingScripts.ShouldBeEmpty();
    }

    [Fact]
    public void CompleteScript_FullIsolation_UnblocksPendingNoIsolation()
    {
        var service = CreateService();
        var writer = MakeIsolatedCommand("echo writer", "unblock-mutex");
        var reader = MakeCommand("echo reader", "unblock-mutex");

        var ticket1 = service.StartScript(writer);
        var ticket2 = service.StartScript(reader);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket1, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
    }

    // ========== Pending Batch Launch ==========

    [Fact]
    public void CompleteScript_FullIsolation_BatchLaunchesMultiplePendingNoIsolation()
    {
        var service = CreateService();
        var writer = MakeIsolatedCommand("echo writer", "batch-mutex");
        var reader1 = MakeCommand("echo reader1", "batch-mutex");
        var reader2 = MakeCommand("echo reader2", "batch-mutex");
        var reader3 = MakeCommand("echo reader3", "batch-mutex");

        var writerTicket = service.StartScript(writer);
        var readerTicket1 = service.StartScript(reader1);
        var readerTicket2 = service.StartScript(reader2);
        var readerTicket3 = service.StartScript(reader3);

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(3);

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(writerTicket, 0));

        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.Count.ShouldBe(3);
        service.ActiveScripts.ContainsKey(readerTicket1.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(readerTicket2.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(readerTicket3.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void CompleteScript_FullIsolation_StopsAfterLaunchingPendingWriter()
    {
        var service = CreateService();
        var writer1 = MakeIsolatedCommand("echo writer1", "serial-mutex");
        var writer2 = MakeIsolatedCommand("echo writer2", "serial-mutex");
        var reader = MakeCommand("echo reader", "serial-mutex");

        var ticket1 = service.StartScript(writer1);
        var ticket2 = service.StartScript(writer2);
        var ticket3 = service.StartScript(reader);

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(2);

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket1, 0));

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(1);
    }

    [Fact]
    public void CompleteScript_ThreeFullIsolation_ExecuteSequentially()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo serial", "sequential-mutex");

        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);
        var ticket3 = service.StartScript(command);

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(2);

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket1, 0));

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.Count.ShouldBe(1);

        // ConcurrentDictionary iteration order is non-deterministic, so find which ticket was launched
        var secondActive = service.ActiveScripts.Keys.Single();
        service.CompleteScript(new CompleteScriptCommand(new ScriptTicket(secondActive), 0));

        service.ActiveScripts.Count.ShouldBe(1);
        service.PendingScripts.ShouldBeEmpty();

        var thirdActive = service.ActiveScripts.Keys.Single();
        service.CompleteScript(new CompleteScriptCommand(new ScriptTicket(thirdActive), 0));

        service.ActiveScripts.ShouldBeEmpty();
        service.PendingScripts.ShouldBeEmpty();

        // All 3 pods created: 1 immediate + 2 via pending dequeue
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Exactly(3));
    }

    // ========== State File ==========

    [Fact]
    public void LaunchScript_WritesStateFile()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo hello"));

        var statePath = Path.Combine(_tempWorkspace, ticket.TaskId, ".squid-state.json");
        File.Exists(statePath).ShouldBeTrue();

        var state = ScriptStateFile.TryRead(Path.Combine(_tempWorkspace, ticket.TaskId));
        state.ShouldNotBeNull();
        state!.TicketId.ShouldBe(ticket.TaskId);
        state.Isolation.ShouldBe("NoIsolation");
    }

    [Fact]
    public void LaunchScript_FullIsolation_WritesStateFile()
    {
        var service = CreateService();
        var command = MakeIsolatedCommand("echo test", "state-mutex");
        var ticket = service.StartScript(command);

        var state = ScriptStateFile.TryRead(Path.Combine(_tempWorkspace, ticket.TaskId));
        state.ShouldNotBeNull();
        state!.Isolation.ShouldBe("FullIsolation");
        state.IsolationMutexName.ShouldBe("state-mutex");
    }

    [Fact]
    public void CompleteScript_DeletesStateFile()
    {
        var service = CreateService();
        var ticket = service.StartScript(MakeCommand("echo done"));

        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        var workDir = Path.Combine(_tempWorkspace, ticket.TaskId);
        Directory.Exists(workDir).ShouldBeFalse();
    }

    // ========== Isolation Mutex Timeout ==========

    [Fact]
    public void PendingScript_ExceedsTimeout_InjectedAsTimeout()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc",
            IsolationMutexTimeoutMinutes = 0 // Immediate timeout
        };

        var podManager = new KubernetesPodManager(_ops.Object, settings);
        var service = new ScriptPodService(_tentacleSettings, settings, podManager);

        var command = MakeIsolatedCommand("echo test", "timeout-mutex");
        var ticket1 = service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        // GetStatus should detect timeout and inject terminal result
        var status = service.GetStatus(new ScriptStatusRequest(ticket2, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        status.Logs.ShouldContain(l => l.Text.Contains("isolation mutex"));
    }

    [Fact]
    public void PendingScript_WithinTimeout_StillPending()
    {
        var service = CreateService(); // Default 30min timeout

        var command = MakeIsolatedCommand("echo test", "within-timeout-mutex");
        service.StartScript(command);
        var ticket2 = service.StartScript(command);

        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        var status = service.GetStatus(new ScriptStatusRequest(ticket2, 0));

        status.State.ShouldBe(ProcessState.Running);
        status.Logs.ShouldContain(l => l.Text.Contains("Waiting for isolation mutex..."));
    }

    // ========== LaunchScript Failure Handling ==========

    [Fact]
    public void StartScript_LaunchFails_InjectsTerminalResultAndReleasesMutex()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("K8s API unavailable"));

        var podManager = new KubernetesPodManager(ops.Object, _kubernetesSettings);
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
        var ticket = service.StartScript(MakeCommand("echo fail"));

        // Should not be in active scripts (launch failed)
        service.ActiveScripts.ContainsKey(ticket.TaskId).ShouldBeFalse();

        // Should have a terminal result
        var status = service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        status.Logs.ShouldContain(l => l.Text.Contains("K8s API unavailable"));
    }

    [Fact]
    public void StartScript_LaunchFails_MutexReleasedSoNextScriptCanStart()
    {
        var callCount = 0;
        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Transient error");
                return pod;
            });

        var podManager = new KubernetesPodManager(ops.Object, _kubernetesSettings);
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
        var command = MakeIsolatedCommand("echo test", "fail-release-mutex");

        // First script fails during launch
        var ticket1 = service.StartScript(command);
        service.ActiveScripts.ContainsKey(ticket1.TaskId).ShouldBeFalse();

        // Second script should acquire mutex and launch successfully (mutex was released)
        var ticket2 = service.StartScript(command);
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        service.PendingScripts.ShouldBeEmpty();
    }

    // ========== Metrics ==========

    [Fact]
    public void StartScript_IncrementsMetricsGauge()
    {
        TentacleMetrics.Reset();
        var service = CreateService();
        var command = MakeCommand("echo metrics");

        service.StartScript(command);

        TentacleMetrics.ActiveScripts.ShouldBe(1);
        TentacleMetrics.ScriptsStartedTotal.ShouldBe(1);

        TentacleMetrics.Reset();
    }

    [Fact]
    public void CompleteScript_DecrementsMetricsGauge()
    {
        TentacleMetrics.Reset();
        var service = CreateService();
        var command = MakeCommand("echo metrics");

        var ticket = service.StartScript(command);
        SetupPodPhase("Succeeded");
        SetupPodExitCode(0);
        SetupPodLogs("");

        service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        TentacleMetrics.ActiveScripts.ShouldBe(0);
        TentacleMetrics.ScriptsCompletedTotal.ShouldBe(1);

        TentacleMetrics.Reset();
    }

    [Fact]
    public void CancelScript_IncrementsCancel()
    {
        TentacleMetrics.Reset();
        var service = CreateService();
        var command = MakeCommand("echo cancel");

        var ticket = service.StartScript(command);
        service.CancelScript(new CancelScriptCommand(ticket, 0));

        TentacleMetrics.ScriptsCanceledTotal.ShouldBe(1);

        TentacleMetrics.Reset();
    }

    // ========== sinceTime Deduplication ==========

    [Fact]
    public void ExtractNewLogLines_SinceTime_OnlyNewLines()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");

        var firstBatch = "line one\nline two\n";
        ScriptPodService.ExtractNewLogLines(ctx, firstBatch, maxLogBufferBytes: 10 * 1024 * 1024);

        var secondBatch = "line one\nline two\nline three\n";
        var result = ScriptPodService.ExtractNewLogLines(ctx, secondBatch, maxLogBufferBytes: 10 * 1024 * 1024);

        result.Count.ShouldBe(1);
        result[0].Text.ShouldBe("line three");
    }

    [Fact]
    public void ExtractNewLogLines_MultipleCallsSameContent_NoDuplicates()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");

        var logs = "alpha\nbeta\n";
        var result1 = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);
        var result2 = ScriptPodService.ExtractNewLogLines(ctx, logs, maxLogBufferBytes: 10 * 1024 * 1024);

        result1.Count.ShouldBe(2);
        result2.Count.ShouldBe(0);
    }

    [Fact]
    public void ExtractNewLogLines_SetsLastLogTimestamp()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");
        ctx.LastLogTimestamp.ShouldBeNull();

        ScriptPodService.ExtractNewLogLines(ctx, "line\n", maxLogBufferBytes: 10 * 1024 * 1024);

        ctx.LastLogTimestamp.ShouldNotBeNull();
    }

    [Fact]
    public void ExtractNewLogLines_EmptyInput_ReturnsEmpty()
    {
        var ctx = new ScriptPodContext("test-ticket", "pod-1", "/tmp/work", "marker123");

        var result = ScriptPodService.ExtractNewLogLines(ctx, "", maxLogBufferBytes: 10 * 1024 * 1024);

        result.ShouldBeEmpty();
    }

    // ========== Helpers ==========

    // === Multi-Namespace ===

    [Fact]
    public void StartScript_WithTargetNamespace_CreatesPodInNamespace()
    {
        _ops.Setup(o => o.ListPods("custom-ns", It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), "custom-ns"))
            .Returns((V1Pod pod, string ns) => pod);

        var service = CreateService();
        var command = MakeCommand("echo hello");
        command.TargetNamespace = "custom-ns";

        service.StartScript(command);

        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "custom-ns"), Times.Once);
    }

    [Fact]
    public void StartScript_WithTargetNamespace_ContextCarriesNamespace()
    {
        _ops.Setup(o => o.ListPods("custom-ns", It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), "custom-ns"))
            .Returns((V1Pod pod, string ns) => pod);

        var service = CreateService();
        var command = MakeCommand("echo hello");
        command.TargetNamespace = "custom-ns";

        var ticket = service.StartScript(command);

        service.ActiveScripts.TryGetValue(ticket.TaskId, out var ctx).ShouldBeTrue();
        ctx.Namespace.ShouldBe("custom-ns");
    }

    [Fact]
    public void StartScript_NoTargetNamespace_ContextNamespaceIsNull()
    {
        var service = CreateService();
        var command = MakeCommand("echo hello");

        var ticket = service.StartScript(command);

        service.ActiveScripts.TryGetValue(ticket.TaskId, out var ctx).ShouldBeTrue();
        ctx.Namespace.ShouldBeNull();
    }

    [Fact]
    public void StateFile_RoundTripsNamespace()
    {
        var dir = Path.Combine(_tempWorkspace, "ns-test");
        Directory.CreateDirectory(dir);

        ScriptStateFile.Write(dir, new ScriptStateFile
        {
            TicketId = "abc",
            PodName = "pod-1",
            EosMarkerToken = "eos",
            Isolation = "NoIsolation",
            Namespace = "tenant-ns",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var state = ScriptStateFile.TryRead(dir);
        state.ShouldNotBeNull();
        state.Namespace.ShouldBe("tenant-ns");
    }

    [Fact]
    public void StateFile_NullNamespace_RoundTrips()
    {
        var dir = Path.Combine(_tempWorkspace, "ns-test-null");
        Directory.CreateDirectory(dir);

        ScriptStateFile.Write(dir, new ScriptStateFile
        {
            TicketId = "abc",
            PodName = "pod-1",
            EosMarkerToken = "eos",
            Isolation = "NoIsolation",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var state = ScriptStateFile.TryRead(dir);
        state.ShouldNotBeNull();
        state.Namespace.ShouldBeNull();
    }

    private ScriptPodService CreateService()
    {
        var podManager = new KubernetesPodManager(_ops.Object, _kubernetesSettings);
        return new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
    }

    private static StartScriptCommand MakeCommand(string scriptBody, string? mutexName = null)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            mutexName,
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

        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
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
